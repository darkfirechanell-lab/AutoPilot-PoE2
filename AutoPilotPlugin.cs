using System;
using System.Collections.Generic;
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
    private IceShotRoutine _routine;
    private RoutineContext _ctx;
    private CombatHud _hud;

    private TrackedEntity _currentTarget;
    private bool _aimToggled;

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
        _routine = new IceShotRoutine();
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

        // Botão "Re-detetar Teclas": limpa as teclas todas e re-atribui (corrige teclas erradas).
        Settings.RedetectKeys.OnPressed += () =>
        {
            foreach (var s in Settings.Skills.Content)
                s.Key.Value = new ExileCore2.Shared.Nodes.HotkeyNodeV2.HotkeyNodeValue(System.Windows.Forms.Keys.None);
            TrySyncSkills(force: true);
            DebugWindow.LogMsg("[CombatRoutine] Teclas re-detetadas.");
        };

        // HotkeyNodeV2 expõe a tecla via .Value (ao contrário do HotkeyNode antigo, já obsoleto).
        ExileCore2.Input.RegisterKey(Settings.AimKey.Value);
        ExileCore2.Input.RegisterKey(Settings.AimToggleKey.Value);
        Settings.AimKey.OnValueChanged += () => ExileCore2.Input.RegisterKey(Settings.AimKey.Value);
        Settings.AimToggleKey.OnValueChanged += () =>
        {
            ExileCore2.Input.RegisterKey(Settings.AimToggleKey.Value);
            _aimToggled = false;
        };

        return true;
    }

    public override void Tick()
    {
        // O toggle alterna mesmo fora de processamento (para poder ligar/desligar a qualquer momento).
        if (Settings.AimToggleKey.PressedOnce())
            _aimToggled = !_aimToggled;

        if (!ShouldProcess())
        {
            // Se a routine está a canalizar, deixa-a fechar o canal com segurança antes de parar.
            if (_routine.IsBusy) { _ctx.Target = null; _routine.Execute(_ctx); }
            _routine.Reset();
            _skills.ReleaseAll();
            _currentTarget = null;
            return;
        }

        // Liberta os taps cujo tempo expirou (substitui o Thread.Sleep por relógio real).
        _inputQueue.Pump();

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

        var aimActive = _aimToggled || ExileCore2.Input.GetKeyState(Settings.AimKey.Value.Key);
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

        // Targeting dinâmico: modo (Danger/Elite/Normal) → pesos → cluster → visibilidade → sticky.
        _currentTarget = _targets.SelectTarget(_entities);

        // Aim: aponta o cursor ao alvo (centro do corpo). Sem alvo, esquece o último cursor.
        if (_currentTarget != null)
            _aim.AimAt(_currentTarget);
        else
            _aim.Reset();

        // Routine: usa as skills. Corre mesmo sem alvo SE estiver a canalizar (fecha o canal).
        if (Settings.Combat.Enabled.Value)
        {
            _ctx.Target = _currentTarget;
            if (_currentTarget != null || _routine.IsBusy)
            {
                ApplyIceShotSettings();
                _routine.Execute(_ctx);
            }
        }

        // Escreve o estado para ficheiro (diagnóstico fora do jogo) quando o Debug está ligado.
        DebugLog.Enabled = Settings.ShowDebug.Value;
        if (Settings.ShowDebug.Value)
        {
            var t = _currentTarget?.Entity;
            DebugLog.Write(
                $"aimActive={aimActive} combatEnabled={Settings.Combat.Enabled.Value}\n" +
                $"alvo={(t == null ? "(nenhum)" : $"{t.Rarity} dist={_currentTarget.Distance:F0} id={t.Id}")}\n" +
                $"mobs total={_targets.DiagTotal} cPeso={_targets.DiagWithWeight} visiveis={_targets.DiagVisible} maisperto={_targets.DiagNearestDist:F0}\n" +
                $"{_routine.ComboDebug}\n" +
                $"{_routine.FillerDebug}\n" +
                $"playerAnim {_animation.DebugLine()}");
        }
    }

    public override void Render()
    {
        try
        {
            if (!Settings.Enable) return;
            if (GameController is not { InGame: true, Player: not null }) return;

            // Só desenha quando o aim está ativo (evita poluir o ecrã quando não estás a combater).
            var aimActive = _aimToggled || ExileCore2.Input.GetKeyState(Settings.AimKey.Value.Key);
            if (!aimActive && !Settings.ShowDebug.Value) return;

            string dbg = null;
            if (Settings.ShowDebug.Value)
                dbg = _routine.ComboDebug + "\n"
                    + _routine.FillerDebug + "\n"
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
    }

    /// <summary>Sincroniza a lista de skills detetadas com a barra do jogador (tolerante a falhas).</summary>
    private void TrySyncSkills(bool force)
    {
        try { _skillDetector.Sync(Settings.Skills.Content, force); }
        catch (Exception err) { DebugWindow.LogError($"[CombatRoutine.SyncSkills] {err}"); }
    }

    /// <summary>Propaga os settings IceShot do utilizador para a routine antes de a executar.</summary>
    private void ApplyIceShotSettings()
    {
        _routine.MinSalvoSeals = Settings.IceShot.MinSalvoSeals.Value;
        _routine.UseSnipeOnRares = Settings.IceShot.UseSnipeOnRares.Value;
        _routine.TornadoBossCooldownMs = Settings.IceShot.TornadoBossCooldownMs.Value;
        _routine.BarrageCommitMs = Settings.IceShot.BarrageCommitMs.Value;
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
            return !GameController.Settings.CoreSettings.Enable;
        }
        catch (Exception err)
        {
            DebugWindow.LogError($"[CombatRoutine.ShouldProcess] {err}");
            return false;
        }
    }
}
