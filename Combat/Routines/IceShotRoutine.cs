using System;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Combat.Routines;

/// <summary>
/// Rotina IceShot reconstruída de raiz sobre os sistemas novos (InputQueue, AnimationReader,
/// BuffReader, CooldownTracker). Reúne o conhecimento testado do AutoMyAim SEM as 3 cópias:
///
///   • Snipe (canalizada): segura a tecla até o estágio de animação chegar ao release (21), mas só
///     DEPOIS de ter passado por um estágio de canal real — senão soltava à toa e o combo entrava
///     em loop sem dano. (Lição registada.)
///   • Salvo: segura a tecla até as charges (skill_seals) BAIXAREM = disparo confirmado.
///   • Mark: segura a tecla até a marca pegar (debuff no inimigo) ou timeout.
///   • Combo Barrage→Snipe quando o alvo está frozen: dispara Barrage e ESPERA a animação dele
///     acabar (via AnimationReader, não timer cego) antes de o Snipe entrar e a cancelar.
///
/// A máquina de estados de canalização (Snipe/Salvo/Mark) vive aqui numa só, partilhada entre os
/// três modos de alvo, em vez de copiada.
/// </summary>
public sealed class IceShotRoutine : IRoutine
{
    public string Name => "Ice Shot";

    // ── Nomes de skills (memória) ───────────────────────────────────────────────────────────
    private const string ICE_SHOT = "IceShotPlayer";
    private const string SNIPE = "SnipePlayer";
    private const string SALVO = "FreezingSalvoPlayer";
    private const string MARK = "FreezingMarkPlayer";
    private const string ICE_TIPPED = "IceTippedArrowsPlayer";
    private const string TORNADO = "TornadoShotPlayer";
    private const string BARRAGE = "BarragePlayer";

    // ── Buffs/debuffs (nomes internos do jogo) ──────────────────────────────────────────────
    private const string FROZEN = "frozen";
    private const string ICE_TIPPED_BUFF = "shearing_bolts";
    private const string MARK_ON_ENEMY = "freezing_mark";
    private const string MARK_PLAYER_BUFF = "freezing_mark_damage_buff";
    private const string SALVO_SEALS = "skill_seals";
    // Debuff do Tornado CONFIRMADO no jogo: "blinded" (visto no dump de buffs como ALVO:blinded).
    // O Tornado serve para MANTER este debuff no alvo (uptime = mais dano), não para spammar.
    private const string TORNADO_DEBUFF = "blinded";

    // ── Cooldowns internos anti-spam (ms) ───────────────────────────────────────────────────
    // Iguais ao AutoMyAim (que não levava kick) — o problema do kick era o tap não-atómico, não o CD.
    private const int CD_ICE_SHOT = 50;
    private const int CD_SALVO = 1500;
    private const int CD_MARK_RETRY = 1000;
    private const int CD_ICE_TIPPED = 1200;
    private const int CD_TORNADO = 2000;
    // CD_BARRAGE: o cooldown REAL do Barrage no jogo é 1,54s (tooltip). O CanBeUsed do Barrage NÃO o
    // bloqueia de forma fiável (é um empower), por isso ESTE gate tem de o fazer. 2000ms era grande
    // demais (bloqueava o 2º Rare); 150ms era curto demais (spam, o Snipe nunca entrava). 1540ms =
    // o cooldown real → o Barrage sai uma vez por ciclo e deixa o Snipe entrar. Ver barrage-empower-mechanic.
    private const int CD_BARRAGE = 1540;

    // Snipe
    private const int SNIPE_RELEASE_STAGE = 21;
    private const int SNIPE_MIN_CHANNEL_MS = 200;
    private const int SNIPE_MAX_CHANNEL_MS = 2000;
    // Salvo / Mark / Ice-Tipped / Tornado — timeouts de segurança a segurar a tecla até confirmar.
    private const int SALVO_COMMIT_MAX_MS = 1200;
    private const int MARK_COMMIT_MAX_MS = 500;
    private const int ICE_TIPPED_COMMIT_MAX_MS = 500; // segura até o buff shearing_bolts aparecer
    private const int TORNADO_COMMIT_MAX_MS = 500;    // segura até confirmar o uso (IsUsing/cooldown)
    // Barrage — segura até confirmar o uso (IsUsing/cooldown do ActorSkill) ou timeout.
    private const int BARRAGE_COMMIT_MAX_MS = 600;

    // Limiares configuráveis (defaults; ligados aos settings na integração)
    public int MinSalvoSeals { get; set; } = 10;
    public bool UseSnipeOnRares { get; set; } = true;
    public int TornadoBossCooldownMs { get; set; } = 4000;
    public int BarrageCommitMs { get; set; } = 400;

    private readonly CooldownTracker _cd = new();

    // Estado de canalização (uma máquina só, partilhada)
    private enum Channel { None, Snipe, Salvo, Mark, IceTipped, Tornado, Barrage }
    private Channel _channel;
    private Keys _channelKey;
    private long _channelStartTicks;
    private uint _channelTargetId;
    private bool _snipeSawChannelStage;
    private int _salvoSealsAtStart;

    public bool IsBusy => _channel != Channel.None;

    // Diagnóstico do combo para o HUD debug (porque é que o Barrage não dispara).
    public string ComboDebug { get; private set; } = "";

    public void Execute(RoutineContext ctx)
    {
        // 1. Se está a canalizar, continua a máquina — mesmo sem alvo (para soltar com segurança).
        if (_channel != Channel.None)
        {
            ContinueChannel(ctx);
            return;
        }

        var target = ctx.Target?.Entity;
        if (target == null || !IsAlive(target))
        {
            ComboDebug = "combo: (sem alvo)";
            return;
        }

        // Diagnóstico base: raridade do alvo + se o gate de raros deixa o combo passar.
        _rarityDebug = $"alvoRar={target.Rarity} snipeRaros={UseSnipeOnRares}";

        // 2. Rotação por raridade do alvo.
        switch (target.Rarity)
        {
            case MonsterRarity.Unique: ExecuteBoss(ctx, target); break;
            case MonsterRarity.Rare: ExecuteElite(ctx, target); break;
            default: ExecuteClear(ctx, target); break;
        }
    }

    private string _rarityDebug = "";

    // ── Rotações ────────────────────────────────────────────────────────────────────────────

    // Ordem (pedido do utilizador 2026-06-04):
    //   FROZEN  → Tornado → Barrage → Snipe   (burst no congelado)
    //   SENÃO   → Mark → Ice-Tipped → Salvo    (preparar/iniciar a luta)
    //   FIM     → Ice Shot                      (filler, vai congelando)
    // O Tornado tem PRIORIDADE no combo frozen (entra ANTES do Barrage), mas TAMBÉM sai fora do
    // frozen pelo seu cooldown (mantém o blind/debuff ativo = mais dano, mesmo sem congelar).
    // Só em Rare e Boss — NUNCA em lixo (Normal/Magic). Ver ExecuteClear.
    // No boss o Tornado usa o cooldown de boss; no elite o cooldown normal.
    private void ExecuteBoss(RoutineContext ctx, Entity target)
    {
        if (TryFrozenCombo(ctx, target, TornadoBossCooldownMs)) return;
        if (TryTornado(ctx, TornadoBossCooldownMs)) return; // mantém o blind mesmo sem frozen
        if (TryMark(ctx, target)) return;
        if (TryIceTipped(ctx)) return;
        if (TrySalvo(ctx, target)) return;
        TryFiller(ctx);
    }

    private void ExecuteElite(RoutineContext ctx, Entity target)
    {
        if (UseSnipeOnRares && TryFrozenCombo(ctx, target, CD_TORNADO)) return;
        if (TryTornado(ctx, CD_TORNADO)) return; // mantém o blind mesmo sem frozen
        if (TryMark(ctx, target)) return;
        if (TryIceTipped(ctx)) return;
        if (TrySalvo(ctx, target)) return;
        TryFiller(ctx);
    }

    private void ExecuteClear(RoutineContext ctx, Entity target)
    {
        // Lixo (Normal/Magic): SEM Tornado — o Tornado é só para Rare e Boss (pedido do utilizador
        // 2026-06-05). Aqui só Mark/Ice-Tipped/filler; o lixo morre depressa no filler.
        if (TryMark(ctx, target)) return; // regra geral: marca se não houver buff de dano ativo
        if (TryIceTipped(ctx)) return;
        TryFiller(ctx);
    }

    // ── Ações ───────────────────────────────────────────────────────────────────────────────

    private void TryFiller(RoutineContext ctx)
    {
        var s = ctx.Find(ICE_SHOT);
        // Diagnóstico do filler (a skill que DEVE sempre disparar). Mostra porque é que (não) sai.
        FillerDebug = $"filler: cd={_cd.Ready(ICE_SHOT, CD_ICE_SHOT)} " +
                      $"found={(s == null ? "null" : "ok")} ready={(s == null ? "-" : s.IsReady.ToString())} " +
                      $"key={(s == null ? "-" : s.Key.Value.Key.ToString())} canHit={ctx.CanHit}";

        // C1: Ice Shot é dano direto — não dispara se o cursor não está em cima do alvo.
        if (!ctx.CanHit) return;
        if (!_cd.Ready(ICE_SHOT, CD_ICE_SHOT)) return;
        if (s == null || !s.IsReady) return;
        ctx.Skills.Tap(s.Key.Value.Key, s.TapHoldMs.Value);
        _cd.Mark(ICE_SHOT);
    }

    public string FillerDebug { get; private set; } = "";

    private bool TryTornado(RoutineContext ctx, int cooldownMs)
    {
        if (!_cd.Ready(TORNADO, cooldownMs)) return false;
        // UPTIME do blind: se o alvo JÁ tem "blinded", o debuff ainda está ativo → não reaplicar
        // (poupa ações, não spamma). Só refresca quando o blind cair. Mecânica de uptime, não de timer.
        var tgt = ctx.Target?.Entity;
        if (tgt != null && BuffReader.Has(tgt, TORNADO_DEBUFF)) return false;
        var s = ctx.Find(TORNADO);
        if (s == null || !s.IsReady) return false;
        // SEGURA até confirmar o uso (IsUsing/cooldown do ActorSkill) ou timeout — senão o Ice Shot
        // passava por cima e o Tornado podia não sair (perdia o uptime do blind).
        BeginChannel(ctx, Channel.Tornado, s.Key.Value.Key, ctx.Target?.Entity?.Id ?? 0);
        return true;
    }

    // Ice-Tipped: reaplica por presença do buff, não por timer cego.
    private bool TryIceTipped(RoutineContext ctx)
    {
        if (BuffReader.Has(ctx.Game?.Player, ICE_TIPPED_BUFF)) return false;
        if (!_cd.Ready(ICE_TIPPED, CD_ICE_TIPPED)) return false;
        var s = ctx.Find(ICE_TIPPED);
        if (s == null || !s.IsReady) return false;
        // SEGURA até o buff shearing_bolts aparecer no player (confirmação) ou timeout — senão o
        // Ice Shot passava por cima e o Ice-Tipped não aplicava.
        BeginChannel(ctx, Channel.IceTipped, s.Key.Value.Key, ctx.Target?.Entity?.Id ?? 0);
        return true;
    }

    // ── Combo no alvo FROZEN: Tornado → Barrage → Snipe ─────────────────────────────────────
    // Ordem pedida pelo utilizador: o Tornado entra PRIMEIRO no congelado (mantém o debuff/blind no
    // boss = mais dano), depois o burst Barrage→Snipe.
    //   0. Tornado (se pronto pelo seu cooldown). Não bloqueia a sequência: se em CD, segue.
    //   1. TryUseBarrage() → se disparou, return.
    //   2. Espera o commit do Barrage antes do Snipe (timer, como o AutoMyAim).
    //   3. BeginSnipe.
    private bool TryFrozenCombo(RoutineContext ctx, Entity target, int tornadoCooldownMs)
    {
        var frozen = BuffReader.Has(target, FROZEN);
        var snipe = ctx.Find(SNIPE);
        ComboDebug = $"{_rarityDebug} | frozen={frozen} sinceBarrage={(_cd.SinceMs(BARRAGE) > 99999 ? -1 : _cd.SinceMs(BARRAGE))}";

        if (!frozen) return false;

        // 0. Tornado Shot primeiro no congelado. Se disparou, return (mantém o uptime do debuff).
        if (TryTornado(ctx, tornadoCooldownMs)) return true;

        // 1. Barrage. Se disparou, return.
        if (TryUseBarrage(ctx)) return true;

        // 2. Espera a animação do Barrage acabar antes do Snipe (timer simples, como o AutoMyAim).
        if (!_cd.Ready(BARRAGE, BarrageCommitMs)) return true;

        // 3. Snipe. C1: só COMEÇA o canal se o cursor está no alvo (é dano direto). Um canal já a
        // decorrer não é afetado por isto — este gate só impede iniciar um novo.
        if (ctx.CanHit && snipe != null && snipe.Key.Value.Key != Keys.None && snipe.IsReady)
        {
            BeginChannel(ctx, Channel.Snipe, snipe.Key.Value.Key, target.Id);
            return true;
        }

        return false;
    }

    // Barrage: cópia do TryUseBarrage do AutoMyAim. Tap explícito, anti-spam por CD_BARRAGE.
    private bool TryUseBarrage(RoutineContext ctx)
    {
        // DIAGNÓSTICO: mostra QUAL condição bloqueia o Barrage (porque ele não sai nos Rares frozen).
        var s0 = ctx.Find(BARRAGE);
        BarrageDebug = $"barrage: canHit={ctx.CanHit} cdReady={_cd.Ready(BARRAGE, CD_BARRAGE)} " +
                       $"found={(s0 == null ? "null" : "ok")} ready={(s0 == null ? "-" : s0.IsReady.ToString())} " +
                       $"key={(s0 == null ? "-" : s0.Key.Value.Key.ToString())}";

        if (!ctx.CanHit) return false; // C1: Barrage é dano direto — exige cursor no alvo.
        if (!_cd.Ready(BARRAGE, CD_BARRAGE)) return false;
        var s = ctx.Find(BARRAGE);
        if (s == null || !s.IsReady) return false;
        AutoPilot.ActionLog.Event($"Barrage USADO: frozen={BuffReader.Has(ctx.Target?.Entity, FROZEN)}");
        // SEGURA até confirmar o uso (IsUsing/cooldown do ActorSkill) ou timeout — igual às outras.
        // Ao soltar, _cd.Mark(BARRAGE) é feito no EndChannel; o commit (passo 2) deixa o Snipe entrar.
        BeginChannel(ctx, Channel.Barrage, s.Key.Value.Key, ctx.Target?.Entity?.Id ?? 0);
        return true;
    }

    public string BarrageDebug { get; private set; } = "";

    // ── Mark ─────────────────────────────────────────────────────────────────────────────────
    // A fonte de verdade é o DEBUFF NO ALVO (freezing_mark), não o buff de dano do jogador.
    //   • Se o alvo já tem o debuff → não toca (já marcado).
    //   • BOSS (Unique): re-marca assim que o debuff sai do boss, IGNORANDO o buff de dano do jogador.
    //   • Fora de boss: não recasta enquanto o jogador tem o buff de dano (evita desperdício).
    //   • SEGURA a tecla até a marca pegar (hold), em vez de um tap que se cancela no Ice Shot.
    private bool TryMark(RoutineContext ctx, Entity target)
    {
        if (BuffReader.Has(target, MARK_ON_ENEMY)) return false; // o alvo já está marcado

        var isBoss = target.Rarity == MonsterRarity.Unique;
        if (!isBoss && BuffReader.Has(ctx.Game?.Player, MARK_PLAYER_BUFF)) return false;

        if (!_cd.Ready(MARK, CD_MARK_RETRY)) return false;

        var s = ctx.Find(MARK);
        if (s == null || s.Key.Value.Key == Keys.None) return false;

        // SEGURA a tecla até a marca PEGAR (debuff freezing_mark no alvo) ou timeout — igual ao
        // AutoMyAim. Um tap simples cancelava-se no lock do Ice Shot e a marca não aplicava (o boss
        // mostrava o ícone mas o BuffsList nunca tinha o debuff → spam da Mark). O hold confirma.
        BeginChannel(ctx, Channel.Mark, s.Key.Value.Key, target.Id);
        return true;
    }

    // ── Salvo (segura até seals baixarem) ───────────────────────────────────────────────────
    private bool TrySalvo(RoutineContext ctx, Entity target)
    {
        if (!_cd.Ready(SALVO, CD_SALVO)) return false;
        var s = ctx.Find(SALVO);
        if (s == null || !s.IsReady) return false;

        var seals = BuffReader.Charges(ctx.Game?.Player, SALVO_SEALS);
        if (seals < MinSalvoSeals) return false; // -1 (ilegível) também bloqueia: sem baseline fiável

        _salvoSealsAtStart = seals;
        BeginChannel(ctx, Channel.Salvo, s.Key.Value.Key, target.Id);
        return true;
    }

    // ── Máquina de canalização (uma só, partilhada) ─────────────────────────────────────────

    private void BeginChannel(RoutineContext ctx, Channel channel, Keys key, uint targetId)
    {
        _channel = channel;
        _channelKey = key;
        _channelTargetId = targetId;
        _channelStartTicks = DateTime.UtcNow.Ticks;
        _snipeSawChannelStage = false;
        ctx.Skills.Channel(key); // KeyDown contínuo
    }

    private void ContinueChannel(RoutineContext ctx)
    {
        var elapsed = (DateTime.UtcNow.Ticks - _channelStartTicks) / TimeSpan.TicksPerMillisecond;
        var target = ctx.Target?.Entity;
        var targetGone = target == null || !IsAlive(target) || target.Id != _channelTargetId;

        switch (_channel)
        {
            case Channel.Snipe:
            {
                if (targetGone) { EndChannel(ctx); return; }

                var stage = ctx.Animation.Stage;
                if (stage >= 0 && stage < SNIPE_RELEASE_STAGE) _snipeSawChannelStage = true;

                var releaseByStage = elapsed >= SNIPE_MIN_CHANNEL_MS && _snipeSawChannelStage
                                     && stage >= SNIPE_RELEASE_STAGE;
                var releaseByTimer = elapsed >= SNIPE_MAX_CHANNEL_MS;
                if (releaseByStage || releaseByTimer) EndChannel(ctx);
                return;
            }
            case Channel.Salvo:
            {
                var seals = BuffReader.Charges(ctx.Game?.Player, SALVO_SEALS);
                var consumed = seals >= 0 && seals < _salvoSealsAtStart; // baixou = disparou
                var timeout = elapsed >= SALVO_COMMIT_MAX_MS;
                if (consumed || timeout || targetGone) { EndChannel(ctx); _cd.Mark(SALVO); }
                return;
            }
            case Channel.Mark:
            {
                var marked = target != null && BuffReader.Has(target, MARK_ON_ENEMY);
                var timeout = elapsed >= MARK_COMMIT_MAX_MS;
                if (marked || timeout || targetGone) { EndChannel(ctx); _cd.Mark(MARK); }
                return;
            }
            case Channel.IceTipped:
            {
                // Confirma pelo buff shearing_bolts no player (aplicou). targetGone NÃO termina aqui:
                // o Ice-Tipped é um buff do jogador, não depende do alvo continuar vivo.
                var applied = BuffReader.Has(ctx.Game?.Player, ICE_TIPPED_BUFF);
                var timeout = elapsed >= ICE_TIPPED_COMMIT_MAX_MS;
                if (applied || timeout) { EndChannel(ctx); _cd.Mark(ICE_TIPPED); }
                return;
            }
            case Channel.Tornado:
            {
                // Confirma pelo ActorSkill: a skill entrou em uso (IsUsing) ou em cooldown (=saiu).
                var s = ctx.Find(TORNADO);
                var used = s != null && (s.IsUsing || s.IsOnCooldown);
                var timeout = elapsed >= TORNADO_COMMIT_MAX_MS;
                if (used || timeout || targetGone) { EndChannel(ctx); _cd.Mark(TORNADO); }
                return;
            }
            case Channel.Barrage:
            {
                // Confirma pelo ActorSkill: o Barrage entrou em uso (IsUsing) ou cooldown (=saiu). Ao
                // soltar, marca o CD → no tick seguinte o combo passa o commit e entra o Snipe.
                var s = ctx.Find(BARRAGE);
                var used = s != null && (s.IsUsing || s.IsOnCooldown);
                var timeout = elapsed >= BARRAGE_COMMIT_MAX_MS;
                if (used || timeout || targetGone) { EndChannel(ctx); _cd.Mark(BARRAGE); }
                return;
            }
        }
    }

    private void EndChannel(RoutineContext ctx)
    {
        ctx.Skills.Release(); // KeyUp da tecla canalizada
        _channel = Channel.None;
        _channelKey = Keys.None;
        _channelTargetId = 0;
        _snipeSawChannelStage = false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static bool IsAlive(Entity e)
    {
        try { return e is { IsValid: true, IsAlive: true, IsDead: false }; }
        catch { return false; }
    }

    public void Reset()
    {
        _channel = Channel.None;
        _channelKey = Keys.None;
        _channelTargetId = 0;
        _snipeSawChannelStage = false;
        _cd.Clear();
    }
}
