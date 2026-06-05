using System;
using System.Collections.Generic;
using System.Linq;
using AutoPilot.Aiming;
using AutoPilot.Combat;
using AutoPilot.Combat.Routines;
using AutoPilot.Detection;
using AutoPilot.Hud;
using AutoPilot.Input;
using AutoPilot.Settings;
using AutoPilot.Targeting;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;

namespace AutoPilot;

/// <summary>
/// Ponto de entrada do plugin AutoPilot (ExileCore2 / PoE2).
///
/// Input sem bloqueio (InputQueue + SkillExecutor), targeting dinâmico (3 modos), aim no centro
/// do corpo, e routines de combate (IceShot). Reconstruído de raiz — corre lado a lado com o
/// AutoMyAim; os settings têm nome próprio (AutoPilot) para não colidir.
/// </summary>
public class AutoPilotPlugin : BaseSettingsPlugin<AutoPilotSettings>
{
    private InputQueue _inputQueue;
    private SkillExecutor _skills;
    private EntityCache _entities;

    private ModeSelector _modes;
    private WeightEngine _weights;
    private ClusterEngine _clusters;
    private RayCaster _rays;
    private TargetSelector _targets;
    private AimController _aim;

    private AnimationReader _animation;
    private SkillDetector _skillDetector;
    private IceShotRoutine _iceShot;
    private StaffRoutine _staff;
    private GeneralRoutine _general;  // Fase 3: motor configurável (corre lado a lado, opt-in).
    private IRoutine _routine;        // a routine ativa (selecionada pelo dropdown)
    private string _lastRoutineName;  // para detetar mudança no dropdown e fazer Reset
    private RoutineContext _ctx;
    private CombatHud _hud;

    private TrackedEntity _currentTarget;
    private bool _aimToggled;
    private bool _wasProcessing; // estado anterior do ShouldProcess (para marcar transições no log).
    private readonly Combat.General.BaselineRecorder _baseline = new(); // Fase 2: grava baseline do IceShot.
    private Combat.DangerDetector _danger;   // Kiting: deteta perigo (mobs a atacar perto).
    private Combat.DodgeController _dodge;    // Kiting: esquiva quando há perigo (prioridade sobre o aim).
    private readonly Combat.General.ProfileManager _profiles = new(); // perfis guardar/carregar.

    // Sincronização periódica de skills (a cada ~N ticks, não todos — é trabalho com reflection/memória).
    private const int SkillSyncEveryTicks = 30;
    private int _skillSyncCounter;

    public AutoPilotPlugin()
    {
        Name = "AutoPilot";
    }

    public override bool Initialise()
    {
        _inputQueue = new InputQueue();
        _skills = new SkillExecutor(_inputQueue);
        _entities = new EntityCache(GameController);

        _modes = new ModeSelector();
        _weights = new WeightEngine();
        _clusters = new ClusterEngine();
        _rays = new RayCaster(GameController);
        _targets = new TargetSelector(_modes, _weights, _clusters, _rays);
        _aim = new AimController(GameController);

        _animation = new AnimationReader(GameController);
        _skillDetector = new SkillDetector(GameController);
        _iceShot = new IceShotRoutine();
        _staff = new StaffRoutine();
        _general = new GeneralRoutine();
        _general.SetRules(Combat.General.IceShotPreset.Build()); // Fase 3: por agora usa o preset de gelo.
        _danger = new Combat.DangerDetector();
        _dodge = new Combat.DodgeController(_inputQueue);
        _routine = SelectRoutine(); // escolhe pela Settings.Routine (default: Ice Shot)
        _hud = new CombatHud();
        _ctx = new RoutineContext
        {
            Game = GameController,
            Skills = _skills,
            Animation = _animation,
            Entities = _entities,
            SkillSlots = Settings.Skills.Content,
        };

        _rays.UpdateArea();
        TrySyncSkills(force: true);

        // Fase 2: o recorder de baseline recebe cada ação de input via o hook do ActionLog.
        ActionLog.OnAction = (kind, key) => _baseline.Record(kind, key);

        // Botão "Carregar preset Ice Shot": preenche os campos [Geral] de cada skill com a rotação de
        // gelo afinada — para o utilizador não configurar 23 campos por skill à mão.
        Settings.LoadIceShotPreset.OnPressed += () =>
        {
            // 1º re-deteta as skills da barra: garante que slots novos (ex.: trocaste o Salvo pelo
            // Tornado) JÁ existem na lista antes de aplicar o preset — senão o preset saltava-os e
            // ficavam por configurar (Prioridade 0). Resolve o "o Tornado não tem prioridade".
            TrySyncSkills(force: true);

            var preset = Combat.General.IceShotPreset.Build();
            var n = Combat.General.PresetApplier.Apply(preset, Settings.Skills.Content);
            Settings.GeneralUseUiRules.Value = true; // ativa as regras da UI logo a seguir.

            // Diagnóstico: lista as skills do preset que NÃO encontraram slot (não estão na barra).
            var faltam = new System.Collections.Generic.List<string>();
            foreach (var r in preset)
            {
                var existe = false;
                foreach (var s in Settings.Skills.Content)
                    if (s != null && s.Name == r.SkillName) { existe = true; break; }
                if (!existe && !faltam.Contains(r.SkillName)) faltam.Add(r.SkillName);
            }
            var msg = $"[AutoPilot] Preset aplicado a {n} skills. Liga 'Geral' no dropdown para usar.";
            if (faltam.Count > 0) msg += $" SEM slot na barra (ignoradas): {string.Join(", ", faltam)}.";
            DebugWindow.LogMsg(msg);
        };

        // M0: dump dos mods dos monstros perto (descobrir nomes internos). Usa o snapshot atual; se
        // ainda não há snapshot (combate não correu), faz um rebuild rápido para o dump ter dados.
        Settings.DumpMods.OnPressed += () =>
        {
            try { _entities.Rebuild(); } catch { }
            Combat.ModDumper.Dump(_entities);
            DebugWindow.LogMsg($"[AutoPilot] {Combat.ModDumper.LastMessage}");
        };

        // Perfis: preenche o dropdown com os perfis já guardados.
        RefreshProfileList();
        Settings.SaveProfile.OnPressed += () =>
        {
            var name = Settings.ProfileName.Value;
            var data = BuildProfileFromSettings();
            _profiles.Save(name, data);
            RefreshProfileList();
            Settings.ProfileList.Value = name; // seleciona o que acabou de guardar.
            DebugWindow.LogMsg($"[AutoPilot] {_profiles.LastMessage}");
        };
        Settings.LoadProfile.OnPressed += () =>
        {
            var name = Settings.ProfileList.Value;
            var data = _profiles.Load(name);
            if (data != null) ApplyProfileToSettings(data);
            DebugWindow.LogMsg($"[AutoPilot] {_profiles.LastMessage}");
        };

        // Botão "Re-detetar Teclas": limpa as teclas todas e re-atribui (corrige teclas erradas).
        Settings.RedetectKeys.OnPressed += () =>
        {
            foreach (var s in Settings.Skills.Content)
                s.Key.Value = new ExileCore2.Shared.Nodes.HotkeyNodeV2.HotkeyNodeValue(System.Windows.Forms.Keys.None);
            TrySyncSkills(force: true);
            DebugWindow.LogMsg("[CombatRoutine] Teclas re-detetadas.");
        };

        // Aim Key/Toggle são HotkeyNode antigo (aceita botões do rato). RegisterKey recebe o nó inteiro
        // e a tecla lê-se por .Value direto (Keys), ao contrário do V2 (.Value.Key). Padrão do AutoMyAim.
        ExileCore2.Input.RegisterKey(Settings.AimKey);
        ExileCore2.Input.RegisterKey(Settings.AimToggleKey);
        ExileCore2.Input.RegisterKey(Settings.Kiting.DodgeKey);
        Settings.Kiting.DodgeKey.OnValueChanged += () => ExileCore2.Input.RegisterKey(Settings.Kiting.DodgeKey);
        Settings.AimKey.OnValueChanged += () => ExileCore2.Input.RegisterKey(Settings.AimKey);
        Settings.AimToggleKey.OnValueChanged += () =>
        {
            ExileCore2.Input.RegisterKey(Settings.AimToggleKey);
            _aimToggled = false;
        };

        return true;
    }

    public override void Tick()
    {
        // O toggle alterna mesmo fora de processamento (para poder ligar/desligar a qualquer momento).
        if (Settings.AimToggleKey.PressedOnce())
            _aimToggled = !_aimToggled;

        // M0: dump de mods POR HOTKEY — lido ANTES do gate ShouldProcess para funcionar com o jogo a
        // correr, sem abrir o overlay do Core2 (que pausaria o plugin e esvaziaria o snapshot). Faz um
        // rebuild na hora para apanhar os monstros que estão à frente neste instante.
        if (Settings.DumpModsKey.Value != System.Windows.Forms.Keys.None && Settings.DumpModsKey.PressedOnce())
        {
            try { _entities.Rebuild(); } catch { }
            Combat.ModDumper.Dump(_entities);
            DebugWindow.LogMsg($"[AutoPilot] {Combat.ModDumper.LastMessage}");
        }

        if (!ShouldProcess())
        {
            // Marca a transição ativo→parado uma vez (diagnóstico do "atacar paredes" ao retomar).
            if (_wasProcessing) { ActionLog.Event("PAROU (ShouldProcess=false: painel aberto ou plugin off)"); _wasProcessing = false; }

            // Se a routine está a canalizar, deixa-a fechar o canal com segurança antes de parar.
            if (_routine.IsBusy) { _ctx.Target = null; _routine.Execute(_ctx); }
            _routine.Reset();
            _skills.ReleaseAll();
            _aim.Reset();            // esquece o último cursor — ao retomar, mira do zero (sem arrastar posição velha).
            _currentTarget = null;
            return;
        }

        // Marca a transição parado→ativo (o 1º tick após fechar o painel — onde o bug aparecia).
        if (!_wasProcessing) { ActionLog.Event("RETOMOU (ShouldProcess=true)"); _wasProcessing = true; }

        // Liberta os taps cujo tempo expirou (substitui o Thread.Sleep por relógio real).
        _inputQueue.Pump();

        // B2: invalida a cache de buffs no início do tick. A partir daqui, cada entidade é lida da
        // memória só uma vez por tick; as ~8 consultas de buff da rotação reusam a snapshot.
        Combat.BuffReader.NewTick();

        // Lê o estado de animação do jogador uma vez (Snipe stage, Barrage progress, etc.).
        _animation.Update();

        // Sincroniza a lista de skills de tempos a tempos (deteta skills ao entrar no jogo / swap de
        // arma), e re-liga a ref viva todos os ticks (o endereço muda entre frames).
        if (++_skillSyncCounter >= SkillSyncEveryTicks) { _skillSyncCounter = 0; TrySyncSkills(force: false); }
        _skillDetector.RelinkLive(Settings.Skills.Content);

        // Propaga o alcance configurado aos motores de targeting.
        _weights.MaxDistance = Settings.AttackRange.Value;
        _modes.EliteRange = Settings.AttackRange.Value;
        _targets.EnableVisibility = Settings.UseVisibility.Value;

        // A3 + humanização: propaga a randomização e a suavização do cursor (0 = desligado/teleporte).
        _aim.JitterRadius = Settings.CursorJitter.Value;
        _aim.Smoothing = Settings.CursorSmoothing.Value;

        // Proximal Tangibility: alcance a partir do qual o mob com esse mod passa a ser mirável.
        Detection.EntityCache.ProximalTangibilityRange = Settings.ProximalRange.Value;

        var aimActive = _aimToggled || ExileCore2.Input.GetKeyState(Settings.AimKey.Value);
        if (!aimActive)
        {
            // Aim desligado: deixa fechar canal a meio, depois para.
            if (_routine.IsBusy) { _ctx.Target = null; _routine.Execute(_ctx); }
            else { _routine.Reset(); _skills.ReleaseAll(); }
            _currentTarget = null;
            return;
        }

        // Um único scan de entidades por tick — todas as consultas seguintes leem este snapshot.
        _entities.Rebuild();

        // M0: dump AUTOMÁTICO de mods. Com o AutoPilot ativo, ao aparecer um Rare/Unique perto grava os
        // mods sozinho (intervalo ~1.5s, não escreve o ficheiro a cada frame). Opt-in; desligar quando
        // já tivermos os nomes. Não interfere no combate — só lê e escreve ficheiro.
        if (Settings.AutoDumpMods.Value) Combat.ModDumper.AutoDump(_entities);

        // KITING: dodge tem PRIORIDADE sobre o aim. Se há perigo (mobs a atacar perto) e o dodge quer
        // agir, esquiva AGORA e salta o resto do tick (não mira nem ataca neste instante).
        _danger.DangerRange = Settings.Kiting.DangerRange.Value;
        _dodge.Enabled = Settings.Kiting.UseDodge.Value;
        _dodge.DodgeKey = Settings.Kiting.DodgeKey.Value;
        _dodge.DangerThreshold = Settings.Kiting.DangerThreshold.Value;
        _dodge.CooldownMs = Settings.Kiting.DodgeCooldownMs.Value;
        var dangerScore = _dodge.Enabled ? _danger.Evaluate(_entities) : 0f;
        if (_dodge.WantsControl(dangerScore))
        {
            // Larga o que a rotação esteja a segurar (não fica com tecla presa durante a esquiva) e esquiva.
            if (_routine.IsBusy) _routine.Reset();
            _skills.ReleaseAll();
            _dodge.Dodge();
            ActionLog.Event($"DODGE (perigo {dangerScore:F1})");
            return;
        }

        // Targeting dinâmico: modo (Danger/Elite/Normal) → pesos → cluster → visibilidade → sticky.
        var previousTargetId = _currentTarget?.Entity?.Id;
        _currentTarget = _targets.SelectTarget(_entities);

        // Marca no ActionLog quando o alvo MUDA (contexto para a sequência de teclas a seguir).
        var newTargetId = _currentTarget?.Entity?.Id;
        if (newTargetId != previousTargetId)
        {
            if (newTargetId == null) ActionLog.Event("alvo PERDIDO");
            else ActionLog.Event($"alvo -> {_currentTarget.Entity.Rarity} id={newTargetId} dist={_currentTarget.Distance:F0} path={SafePath(_currentTarget.Entity)} | {_targets.DiagTargetPick}");
        }

        // Fase 2: baseline. Só grava com a rotina Ice Shot selecionada (a referência verdadeira).
        var iceShotActive = Settings.Routine?.Value == "Ice Shot";
        _baseline.Enabled = Settings.RecordBaseline.Value && iceShotActive;
        _baseline.SetScenario(CurrentScenario());

        // Aim: aponta o cursor ao alvo (centro do corpo). Sem alvo, esquece o último cursor.
        if (_currentTarget != null)
            _aim.AimAt(_currentTarget);
        else
            _aim.Reset();

        // C1: o cursor está em cima do alvo? Usa o erro de mira que o AIM calculou ESTE tick (sem o
        // lag do MousePosition). Se o C1 estiver desligado, CanHit é sempre true (nunca bloqueia).
        if (Settings.Combat.RequireCursorOnTarget.Value)
            _ctx.CanHit = _aim.LastAimErrorPx <= Settings.Combat.CursorOnTargetTolerance.Value;
        else
            _ctx.CanHit = true;

        // Routine: usa as skills. Corre mesmo sem alvo SE estiver a canalizar (fecha o canal).
        // Reage à seleção do dropdown ANTES de aplicar settings/executar (faz Reset se mudou).
        _routine = SelectRoutine();
        if (Settings.Combat.Enabled.Value)
        {
            _ctx.Target = _currentTarget;
            if (_currentTarget != null || _routine.IsBusy)
            {
                ApplyRoutineSettings();
                _routine.Execute(_ctx);
            }
        }

        // Escreve o estado para ficheiro (diagnóstico fora do jogo) quando o Debug está ligado.
        DebugLog.Enabled = Settings.ShowDebug.Value;
        ActionLog.Enabled = Settings.ShowDebug.Value;
        if (Settings.ShowDebug.Value)
        {
            var t = _currentTarget?.Entity;
            DebugLog.Write(
                $"aimActive={aimActive} combatEnabled={Settings.Combat.Enabled.Value}\n" +
                $"alvo={(t == null ? "(nenhum)" : $"{t.Rarity} dist={_currentTarget.Distance:F0} id={t.Id}")}\n" +
                $"mobs total={_targets.DiagTotal} cPeso={_targets.DiagWithWeight} visiveis={_targets.DiagVisible} maisperto={_targets.DiagNearestDist:F0}\n" +
                $"pick: {_targets.DiagTargetPick}\n" +
                $"{RoutineDebug()}\n" +
                $"{_aim.AimDebug}\n" +
                $"{SkillUseDebugLine()}\n" +
                $"alvoBuffs: {BuffNamesLine(_currentTarget?.Entity)}\n" +
                $"playerBuffs: {BuffNamesLine(GameController?.Player)}\n" +
                $"{EvaluatorObserveLine()}\n" +
                $"{_danger.Debug} | {_dodge.Debug}\n" +
                $"playerAnim {_animation.DebugLine()}");
        }
    }

    // FASE 1: observador do RuleEvaluator. Corre o avaliador com o preset de gelo SÓ para verificação
    // (não age, não preme nada) e regista que skills PASSARIAM as condições este tick. Serve para
    // comparar com o que o IceShot realmente faz, antes de o motor (Fase 3) depender do avaliador.
    private List<Combat.General.SkillRule> _evalPreset;
    private string EvaluatorObserveLine()
    {
        try
        {
            _evalPreset ??= Combat.General.IceShotPreset.Build();
            var sb = new System.Text.StringBuilder("eval:");
            foreach (var rule in _evalPreset)
            {
                if (Combat.General.RuleEvaluator.Evaluate(_ctx, rule, out _))
                {
                    var n = rule.SkillName.EndsWith("Player") ? rule.SkillName[..^6] : rule.SkillName;
                    sb.Append($" {n}(p{rule.Priority})");
                }
            }
            return sb.ToString();
        }
        catch { return "eval: (erro)"; }
    }

    /// <summary>
    /// Fase 2: classifica o encontro atual para o baseline. Boss = alvo Unique; Rare = alvo Rare;
    /// Pack = alvo normal/magic (lixo). None = sem alvo.
    /// </summary>
    private Combat.General.BaselineRecorder.Scenario CurrentScenario()
    {
        var t = _currentTarget?.Entity;
        if (t == null) return Combat.General.BaselineRecorder.Scenario.None;
        try
        {
            return t.Rarity switch
            {
                ExileCore2.Shared.Enums.MonsterRarity.Unique => Combat.General.BaselineRecorder.Scenario.Boss,
                ExileCore2.Shared.Enums.MonsterRarity.Rare => Combat.General.BaselineRecorder.Scenario.Rare,
                _ => Combat.General.BaselineRecorder.Scenario.Pack,
            };
        }
        catch { return Combat.General.BaselineRecorder.Scenario.None; }
    }

    /// <summary>Path do metadata da entidade (para identificar o clone do jogador e excluí-lo).</summary>
    private static string SafePath(ExileCore2.PoEMemory.MemoryObjects.Entity e)
    {
        try { return e?.Path ?? "?"; } catch { return "?"; }
    }

    /// <summary>
    /// DIAGNÓSTICO: lista os nomes de buffs/debuffs de uma entidade para o log em ficheiro. Serve
    /// para DESCOBRIR que debuff aparece no boss quando a Mark é aplicada (e confirmar nomes de buffs
    /// em geral). Leitura defensiva; "(nenhum)" se vazio, "?" em erro. Também ACUMULA cada nome novo
    /// num ficheiro à parte (apanha buffs que duram só 1 frame, ex.: a Freezing Mark).
    /// </summary>
    private string BuffNamesLine(ExileCore2.PoEMemory.MemoryObjects.Entity entity)
    {
        try
        {
            if (entity == null) return "(sem entidade)";
            if (!entity.TryGetComponent<ExileCore2.PoEMemory.Components.Buffs>(out var buffs) || buffs?.BuffsList == null)
                return "(ilegivel)";
            var names = new List<string>();
            foreach (var b in buffs.BuffsList)
                if (!string.IsNullOrEmpty(b?.Name) && !names.Contains(b.Name))
                {
                    names.Add(b.Name);
                    AccumulateBuffName(entity == GameController?.Player ? "PLAYER" : "ALVO", b.Name);
                }
            return names.Count == 0 ? "(nenhum)" : string.Join(", ", names);
        }
        catch { return "?"; }
    }

    // Acumula cada nome de buff já visto (alvo/player) num ficheiro à parte, com a 1ª hora a que
    // apareceu. Assim apanhamos nomes que duram pouco (ex.: o debuff da Freezing Mark no boss).
    private readonly HashSet<string> _seenBuffNames = new();
    private void AccumulateBuffName(string who, string name)
    {
        var key = $"{who}:{name}";
        if (!_seenBuffNames.Add(key)) return; // já registado
        try
        {
            var line = $"[{DateTime.Now:mm:ss.fff}] {key}\n";
            System.IO.File.AppendAllText(
                @"C:\Users\clona\Desktop\GamePoe\TestePoE\AutoPilot_buffnames.txt", line);
        }
        catch { }
    }

    /// <summary>
    /// DIAGNÓSTICO (não muda rotação): mostra o estado de USO de cada skill detetada — stage/using/
    /// channel/cd/uses — para confirmar quais campos do ActorSkill são fiáveis nesta build ANTES de
    /// construir a confirmação de uso em cima deles. Ver memória actorskill-use-confirmation.
    /// </summary>
    private string SkillUseDebugLine()
    {
        var sb = new System.Text.StringBuilder("skillUse:");
        foreach (var s in Settings.Skills.Content)
        {
            if (s == null || string.IsNullOrEmpty(s.Name) || s.Live == null) continue;
            // nome curto (sem o sufixo "Player") para a linha não ficar gigante.
            var n = s.Name.EndsWith("Player") ? s.Name[..^6] : s.Name;
            sb.Append($" {n}[st={s.UseStage} use={(s.IsUsing ? 1 : 0)} ch={(s.IsChanneling ? 1 : 0)} cd={(s.IsOnCooldown ? 1 : 0)} n={s.TotalUses}]");
        }
        return sb.ToString();
    }

    public override void Render()
    {
        try
        {
            if (!Settings.Enable) return;
            if (GameController is not { InGame: true, Player: not null }) return;

            // Só desenha quando o aim está ativo (evita poluir o ecrã quando não estás a combater).
            var aimActive = _aimToggled || ExileCore2.Input.GetKeyState(Settings.AimKey.Value);
            if (!aimActive && !Settings.ShowDebug.Value) return;

            string dbg = null;
            if (Settings.ShowDebug.Value)
                dbg = RoutineDebug() + "\n"
                    + _skillDetector.DebugSkillSlots(Settings.Skills.Content);

            _hud.Render(GameController, Graphics, Settings.ShowDebug.Value,
                _targets.CurrentMode, _currentTarget, _animation, _targets, dbg);
        }
        catch (Exception err)
        {
            DebugWindow.LogError($"[CombatRoutine.Render] {err}");
        }
    }

    public override void AreaChange(AreaInstance area)
    {
        // Mudança de zona: larga tudo. Um hold/canal a meio não deve atravessar o loading.
        _routine?.Reset();
        _skills?.ReleaseAll();
        _entities?.Clear();
        _rays?.UpdateArea();
        _targets?.Reset();
        _aim?.Reset();
        _currentTarget = null;
        TrySyncSkills(force: true);
    }

    public override void OnClose()
    {
        // Ao desligar/fechar o plugin: nenhuma tecla pode ficar presa.
        _routine?.Reset();
        _skills?.ReleaseAll();
        _baseline?.FlushAll(); // Fase 2: grava o baseline pendente antes de fechar.
    }

    /// <summary>Sincroniza a lista de skills detetadas com a barra do jogador (tolerante a falhas).</summary>
    private void TrySyncSkills(bool force)
    {
        try { _skillDetector.Sync(Settings.Skills.Content, force); }
        catch (Exception err) { DebugWindow.LogError($"[CombatRoutine.SyncSkills] {err}"); }
    }

    /// <summary>
    /// Devolve a routine correspondente ao dropdown <see cref="AutoPilotSettings.Routine"/>. Se a
    /// seleção mudou desde o último tick, faz Reset à routine anterior (larga teclas/estado) antes de
    /// trocar — senão um hold a meio podia ficar preso ao mudar de build no menu.
    /// </summary>
    private IRoutine SelectRoutine()
    {
        var name = Settings.Routine?.Value ?? "Ice Shot";
        IRoutine chosen = name switch
        {
            "Staff" => _staff,
            "Geral" => _general,
            _ => _iceShot,
        };

        if (_lastRoutineName != name)
        {
            // Larga o que a routine anterior estivesse a segurar antes de trocar.
            _routine?.Reset();
            _skills?.ReleaseAll();
            _lastRoutineName = name;
        }
        return chosen;
    }

    /// <summary>Linhas de diagnóstico da routine ATIVA (cada uma expõe as suas próprias).</summary>
    private string RoutineDebug()
    {
        switch (_routine)
        {
            case IceShotRoutine ice:
                return $"{ice.ComboDebug}\n{ice.BarrageDebug}\n{ice.FillerDebug}";
            case StaffRoutine staff:
                return $"{staff.ComboDebug}\n{staff.MaintenanceDebug}\n{staff.ThunderDebug}\n{staff.FillerDebug}";
            case GeneralRoutine gen:
                return gen.Debug;
            default:
                return "";
        }
    }

    // ── Perfis: settings ↔ ProfileData ─────────────────────────────────────────────────────

    /// <summary>Atualiza o dropdown de perfis com os ficheiros existentes (mantém a seleção se possível).</summary>
    private void RefreshProfileList()
    {
        var names = _profiles.ListProfiles();
        var prev = Settings.ProfileList.Value;
        Settings.ProfileList.Values = names;
        if (names.Count > 0)
            Settings.ProfileList.Value = names.Contains(prev) ? prev : names[0];
    }

    /// <summary>Lê os settings atuais para um ProfileData (guardar).</summary>
    private Combat.General.ProfileData BuildProfileFromSettings()
    {
        var d = new Combat.General.ProfileData
        {
            AttackRange = Settings.AttackRange.Value,
            ProximalRange = Settings.ProximalRange.Value,
            CursorJitter = Settings.CursorJitter.Value,
            RequireCursorOnTarget = Settings.Combat.RequireCursorOnTarget.Value,
            CursorOnTargetTolerance = Settings.Combat.CursorOnTargetTolerance.Value,
            UseVisibility = Settings.UseVisibility.Value,
            PauseOnPanels = Settings.PauseOnPanels.Value,
            UseDodge = Settings.Kiting.UseDodge.Value,
            DangerRange = Settings.Kiting.DangerRange.Value,
            DangerThreshold = Settings.Kiting.DangerThreshold.Value,
            DodgeCooldownMs = Settings.Kiting.DodgeCooldownMs.Value,
            GeneralUseUiRules = Settings.GeneralUseUiRules.Value,
            Routine = Settings.Routine.Value,
        };
        foreach (var s in Settings.Skills.Content)
        {
            if (s == null || string.IsNullOrEmpty(s.Name)) continue;
            d.Skills.Add(new Combat.General.ProfileSkill
            {
                Name = s.Name, Enabled = s.Enabled.Value, Priority = s.Priority.Value, TapHoldMs = s.TapHoldMs.Value,
                UseType = s.UseType.Value, CooldownMs = s.CooldownMs.Value, AttackInPlace = s.AttackInPlace.Value,
                MinRarity = s.MinRarity.Value, IgnoreRangeForUnique = s.IgnoreRangeForUnique.Value,
                MinDistance = s.MinDistance.Value, MaxDistance = s.MaxDistance.Value,
                TargetHpMin = s.TargetHpMin.Value, TargetHpMax = s.TargetHpMax.Value,
                CloseTargets = s.CloseTargets.Value, CloseTargetsRange = s.CloseTargetsRange.Value,
                TargetHasBuff = s.TargetHasBuff.Value, TargetMissingBuff = s.TargetMissingBuff.Value,
                PlayerHasBuff = s.PlayerHasBuff.Value, PlayerMissingBuff = s.PlayerMissingBuff.Value,
                BossIgnoresPlayerMissingBuff = s.BossIgnoresPlayerMissingBuff.Value,
                ChargeBuff = s.ChargeBuff.Value, ChargeMin = s.ChargeMin.Value,
                AfterSkill = s.AfterSkill.Value, AfterSkillDelayMs = s.AfterSkillDelayMs.Value,
                ReleaseWhen = s.ReleaseWhen.Value, ReleaseBuffName = s.ReleaseBuffName.Value,
                ReleaseAnimationStage = s.ReleaseAnimationStage.Value, ReleaseTimeoutMs = s.ReleaseTimeoutMs.Value,
            });
        }
        return d;
    }

    /// <summary>Aplica um ProfileData carregado aos settings (settings gerais + regras por nome de skill).</summary>
    private void ApplyProfileToSettings(Combat.General.ProfileData d)
    {
        Settings.AttackRange.Value = d.AttackRange;
        Settings.ProximalRange.Value = d.ProximalRange;
        Settings.CursorJitter.Value = d.CursorJitter;
        Settings.Combat.RequireCursorOnTarget.Value = d.RequireCursorOnTarget;
        Settings.Combat.CursorOnTargetTolerance.Value = d.CursorOnTargetTolerance;
        Settings.UseVisibility.Value = d.UseVisibility;
        Settings.PauseOnPanels.Value = d.PauseOnPanels;
        Settings.Kiting.UseDodge.Value = d.UseDodge;
        Settings.Kiting.DangerRange.Value = d.DangerRange;
        Settings.Kiting.DangerThreshold.Value = d.DangerThreshold;
        Settings.Kiting.DodgeCooldownMs.Value = d.DodgeCooldownMs;
        Settings.GeneralUseUiRules.Value = d.GeneralUseUiRules;
        Settings.Routine.Value = d.Routine;

        // Regras por nome: aplica a cada skill da build atual a regra do perfil com o mesmo nome.
        foreach (var s in Settings.Skills.Content)
        {
            if (s == null || string.IsNullOrEmpty(s.Name)) continue;
            Combat.General.ProfileSkill p = null;
            foreach (var ps in d.Skills) if (ps.Name == s.Name) { p = ps; break; }
            if (p == null) continue;

            s.Enabled.Value = p.Enabled; s.Priority.Value = p.Priority; s.TapHoldMs.Value = p.TapHoldMs;
            s.UseType.Value = p.UseType; s.CooldownMs.Value = p.CooldownMs; s.AttackInPlace.Value = p.AttackInPlace;
            s.MinRarity.Value = p.MinRarity; s.IgnoreRangeForUnique.Value = p.IgnoreRangeForUnique;
            s.MinDistance.Value = p.MinDistance; s.MaxDistance.Value = p.MaxDistance;
            s.TargetHpMin.Value = p.TargetHpMin; s.TargetHpMax.Value = p.TargetHpMax;
            s.CloseTargets.Value = p.CloseTargets; s.CloseTargetsRange.Value = p.CloseTargetsRange;
            s.TargetHasBuff.Value = p.TargetHasBuff; s.TargetMissingBuff.Value = p.TargetMissingBuff;
            s.PlayerHasBuff.Value = p.PlayerHasBuff; s.PlayerMissingBuff.Value = p.PlayerMissingBuff;
            s.BossIgnoresPlayerMissingBuff.Value = p.BossIgnoresPlayerMissingBuff;
            s.ChargeBuff.Value = p.ChargeBuff; s.ChargeMin.Value = p.ChargeMin;
            s.AfterSkill.Value = p.AfterSkill; s.AfterSkillDelayMs.Value = p.AfterSkillDelayMs;
            s.ReleaseWhen.Value = p.ReleaseWhen; s.ReleaseBuffName.Value = p.ReleaseBuffName;
            s.ReleaseAnimationStage.Value = p.ReleaseAnimationStage; s.ReleaseTimeoutMs.Value = p.ReleaseTimeoutMs;
        }
    }

    /// <summary>Propaga os settings do utilizador para a routine ativa antes de a executar.</summary>
    private void ApplyRoutineSettings()
    {
        _iceShot.MinSalvoSeals = Settings.IceShot.MinSalvoSeals.Value;
        _iceShot.UseSnipeOnRares = Settings.IceShot.UseSnipeOnRares.Value;
        _iceShot.TornadoBossCooldownMs = Settings.IceShot.TornadoBossCooldownMs.Value;
        _iceShot.BarrageCommitMs = Settings.IceShot.BarrageCommitMs.Value;

        _staff.MaintainChargedStaff = Settings.Staff.MaintainChargedStaff.Value;
        _staff.MinPowerCharges = Settings.Staff.MinPowerCharges.Value;
        _staff.UseRend = Settings.Staff.UseRend.Value;
        _staff.UseHollowForm = Settings.Staff.UseHollowForm.Value;
        _staff.TempestBellDurationMs = Settings.Staff.TempestBellDurationMs.Value;
        _staff.UseFallingThunder = Settings.Staff.UseFallingThunder.Value;
        _staff.FallingThunderCharges = Settings.Staff.FallingThunderCharges.Value;

        // Fase 3.4: o motor Geral usa as regras da UI (se ligado) ou o preset de gelo embutido.
        var rules = Settings.GeneralUseUiRules.Value
            ? Combat.General.SkillRuleMapper.FromSlots(Settings.Skills.Content)
            : Combat.General.IceShotPreset.Build();
        _general.SetRules(rules);
    }

    /// <summary>
    /// Só processa quando o plugin está ligado, o jogo está em curso e há jogador.
    /// O overlay nativo do ExileCore não pode estar ativo ao mesmo tempo (evita conflito de input).
    /// </summary>
    private bool ShouldProcess()
    {
        try
        {
            if (!Settings.Enable) return false;
            if (GameController is not { InGame: true, Player: not null }) return false;
            if (GameController.Settings.CoreSettings.Enable) return false;

            // A2: pára o combate quando há painéis de UI abertos (inventário/loja/tree). Copiado do
            // AutoMyAim. Ao fechar o painel, ShouldProcess volta a true e o Tick recomeça do zero.
            if (Settings.PauseOnPanels.Value && AreUiPanelsBlocking())
                return false;

            return true;
        }
        catch (Exception err)
        {
            DebugWindow.LogError($"[CombatRoutine.ShouldProcess] {err}");
            return false;
        }
    }

    /// <summary>
    /// Há algum painel de UI aberto que deva bloquear o combate? (inventário, loja, skill tree…)
    /// Copiado letra a letra do AutoMyAim: qualquer painel fullscreen, o painel esquerdo (inventário)
    /// ou o direito (loja/stash) visível conta como bloqueio. Falha fechado (true) em erro.
    /// </summary>
    private bool AreUiPanelsBlocking()
    {
        try
        {
            var ui = GameController?.IngameState?.IngameUi;
            if (ui == null) return false;
            if (ui.FullscreenPanels != null && ui.FullscreenPanels.Any(x => x.IsVisible)) return true;
            if (ui.OpenLeftPanel != null && ui.OpenLeftPanel.IsVisible) return true;
            if (ui.OpenRightPanel != null && ui.OpenRightPanel.IsVisible) return true;
            return false;
        }
        catch (Exception err)
        {
            DebugWindow.LogError($"[CombatRoutine.AreUiPanelsBlocking] {err}");
            return true; // em dúvida, bloqueia (mais seguro do que disparar com a UI aberta).
        }
    }
}
