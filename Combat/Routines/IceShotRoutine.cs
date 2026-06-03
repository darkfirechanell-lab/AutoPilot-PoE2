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
    // NOTA: o debuff do Tornado (provável "blind") está por confirmar no jogo — ver memória
    // iceshot-tornado-blind-mechanic. Quando confirmado, entra aqui e na lógica do Tornado.

    // ── Cooldowns internos anti-spam (ms) ───────────────────────────────────────────────────
    // Iguais ao AutoMyAim (que não levava kick) — o problema do kick era o tap não-atómico, não o CD.
    private const int CD_ICE_SHOT = 50;
    private const int CD_SALVO = 1500;
    private const int CD_MARK_RETRY = 1000;
    private const int CD_ICE_TIPPED = 1200;
    private const int CD_TORNADO = 2000;
    private const int CD_BARRAGE = 2000;

    // Snipe
    private const int SNIPE_RELEASE_STAGE = 21;
    private const int SNIPE_MIN_CHANNEL_MS = 200;
    private const int SNIPE_MAX_CHANNEL_MS = 2000;
    // Salvo / Mark — timeouts de segurança a segurar a tecla
    private const int SALVO_COMMIT_MAX_MS = 1200;
    private const int MARK_COMMIT_MAX_MS = 500;
    // Barrage — fallback de tempo se a leitura de animação falhar (rede de segurança, não principal)
    private const int BARRAGE_COMMIT_FALLBACK_MS = 400;

    // Limiares configuráveis (defaults; ligados aos settings na integração)
    public int MinSalvoSeals { get; set; } = 10;
    public bool UseSnipeOnRares { get; set; } = true;
    public int TornadoBossCooldownMs { get; set; } = 4000;
    public int BarrageCommitMs { get; set; } = 400;

    private readonly CooldownTracker _cd = new();

    // Estado de canalização (uma máquina só, partilhada)
    private enum Channel { None, Snipe, Salvo, Mark }
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

    // Ordem alinhada com o AutoMyAim (testada). No boss/elite, o combo frozen (Barrage→Snipe) tem
    // PRIORIDADE quando o alvo está frozen — é o burst de dano; só depois Mark/Ice-Tipped/filler.
    private void ExecuteBoss(RoutineContext ctx, Entity target)
    {
        if (TryFrozenCombo(ctx, target)) return;
        if (TryMark(ctx, target)) return;
        if (TryIceTipped(ctx)) return;
        if (TryTornado(ctx, TornadoBossCooldownMs)) return;
        if (TrySalvo(ctx, target)) return;
        TryFiller(ctx);
    }

    private void ExecuteElite(RoutineContext ctx, Entity target)
    {
        if (UseSnipeOnRares && TryFrozenCombo(ctx, target)) return;
        if (TryMark(ctx, target)) return;
        if (TryIceTipped(ctx)) return;
        if (TrySalvo(ctx, target)) return;
        TryFiller(ctx);
    }

    private void ExecuteClear(RoutineContext ctx, Entity target)
    {
        if (TryMark(ctx, target)) return; // regra geral: marca se não houver buff de dano ativo
        if (TryIceTipped(ctx)) return;
        if (TryTornado(ctx, CD_TORNADO)) return;
        TryFiller(ctx);
    }

    // ── Ações ───────────────────────────────────────────────────────────────────────────────

    private void TryFiller(RoutineContext ctx)
    {
        var s = ctx.Find(ICE_SHOT);
        // Diagnóstico do filler (a skill que DEVE sempre disparar). Mostra porque é que (não) sai.
        FillerDebug = $"filler: cd={_cd.Ready(ICE_SHOT, CD_ICE_SHOT)} " +
                      $"found={(s == null ? "null" : "ok")} ready={(s == null ? "-" : s.IsReady.ToString())} " +
                      $"key={(s == null ? "-" : s.Key.Value.Key.ToString())}";

        if (!_cd.Ready(ICE_SHOT, CD_ICE_SHOT)) return;
        if (s == null || !s.IsReady) return;
        ctx.Skills.Tap(s.Key.Value.Key, s.TapHoldMs.Value);
        _cd.Mark(ICE_SHOT);
    }

    public string FillerDebug { get; private set; } = "";

    private bool TryTornado(RoutineContext ctx, int cooldownMs)
    {
        if (!_cd.Ready(TORNADO, cooldownMs)) return false;
        var s = ctx.Find(TORNADO);
        if (s == null || !s.IsReady) return false;
        ctx.Skills.Tap(s.Key.Value.Key, s.TapHoldMs.Value);
        _cd.Mark(TORNADO);
        return true;
    }

    // Ice-Tipped: reaplica por presença do buff, não por timer cego.
    private bool TryIceTipped(RoutineContext ctx)
    {
        if (BuffReader.Has(ctx.Game?.Player, ICE_TIPPED_BUFF)) return false;
        if (!_cd.Ready(ICE_TIPPED, CD_ICE_TIPPED)) return false;
        var s = ctx.Find(ICE_TIPPED);
        if (s == null || !s.IsReady) return false;
        ctx.Skills.Tap(s.Key.Value.Key, s.TapHoldMs.Value);
        _cd.Mark(ICE_TIPPED);
        return true;
    }

    // ── Combo Barrage → Snipe (alvo frozen) ─────────────────────────────────────────────────
    // Estrutura do AutoMyAim + deteção de fim de animação do Barrage pelo SINAL real (não só timer):
    //   1. TryUseBarrage() → se disparou, return.
    //   2. Espera a animação do Barrage ACABAR antes de o Snipe entrar (senão o Snipe corta-a e o
    //      Barrage não dá dano). Fim de animação detetado por `action != "UsingAbility"` (sinal preciso
    //      descoberto no log — o Barrage anima ~384ms com action=UsingAbility), com um tempo mínimo
    //      para a animação arrancar e o BarrageCommitMs como teto de segurança.
    //   3. BeginSnipe.
    private bool TryFrozenCombo(RoutineContext ctx, Entity target)
    {
        var frozen = BuffReader.Has(target, FROZEN);
        var snipe = ctx.Find(SNIPE);
        var sinceBarrage = _cd.SinceMs(BARRAGE);
        ComboDebug = $"{_rarityDebug} | frozen={frozen} sinceBarrage={(sinceBarrage > 99999 ? -1 : sinceBarrage)} action={ctx.Animation.Action}";

        if (!frozen) return false;

        // 1. Barrage primeiro. Se disparou, return.
        if (TryUseBarrage(ctx)) return true;

        // 2. Só deixa o Snipe entrar quando a animação do Barrage acabou.
        if (!BarrageAnimationDone(ctx, sinceBarrage)) return true;

        // 3. Snipe.
        if (snipe != null && snipe.Key.Value.Key != Keys.None && snipe.IsReady)
        {
            BeginChannel(ctx, Channel.Snipe, snipe.Key.Value.Key, target.Id);
            return true;
        }

        // Snipe indisponível → não bloqueia: deixa a rotação seguir para o filler.
        return false;
    }

    // Tempo mínimo após o tap do Barrage antes de aceitar "animação acabou" — a animação demora
    // 1-2 ticks a arrancar (action ainda não é UsingAbility logo a seguir ao tap), por isso sem este
    // mínimo o Snipe entrava antes do Barrage sequer começar.
    private const int BARRAGE_ANIM_MIN_MS = 80;

    /// <summary>
    /// A animação do Barrage acabou? True se:
    ///   • passou o mínimo de arranque E o jogador já NÃO está em UsingAbility (a animação terminou), OU
    ///   • o teto de segurança (BarrageCommitMs) já passou (rede de segurança se o sinal falhar).
    /// </summary>
    private bool BarrageAnimationDone(RoutineContext ctx, long sinceBarrageMs)
    {
        if (sinceBarrageMs >= BarrageCommitMs) return true; // teto de segurança

        if (sinceBarrageMs >= BARRAGE_ANIM_MIN_MS)
        {
            var action = ctx.Animation.Action;
            // Quando a animação do Barrage acaba, action deixa de ser "UsingAbility".
            if (!string.Equals(action, "UsingAbility", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Barrage: cópia do TryUseBarrage do AutoMyAim. Tap explícito, anti-spam por CD_BARRAGE.
    private bool TryUseBarrage(RoutineContext ctx)
    {
        if (!_cd.Ready(BARRAGE, CD_BARRAGE)) return false;
        var s = ctx.Find(BARRAGE);
        if (s == null || !s.IsReady) return false;
        ctx.Skills.Tap(s.Key.Value.Key, s.TapHoldMs.Value);
        _cd.Mark(BARRAGE);
        return true;
    }

    // Nº de casts da Mark no boss. Começa em 1 (teste do utilizador). Se a marca não pegar com o
    // buff de dano ativo (o re-cast pode consumir o buff sem marcar), passar para 2.
    private const int BOSS_MARK_CASTS = 1;

    // ── Mark ─────────────────────────────────────────────────────────────────────────────────
    // A fonte de verdade é o DEBUFF NO ALVO (freezing_mark), não o buff de dano do jogador.
    //   • Se o alvo já tem o debuff → não toca (já marcado).
    //   • BOSS (Unique): re-marca assim que o debuff sai do boss, IGNORANDO o buff de dano do jogador.
    //     Faz BOSS_MARK_CASTS taps (1 por agora; mudar p/ 2 se a marca não pegar — ver constante).
    //   • Fora de boss: não recasta enquanto o jogador tem o buff de dano (evita desperdício); 1 tap.
    private bool TryMark(RoutineContext ctx, Entity target)
    {
        if (BuffReader.Has(target, MARK_ON_ENEMY)) return false; // o alvo já está marcado

        var isBoss = target.Rarity == MonsterRarity.Unique;
        if (!isBoss && BuffReader.Has(ctx.Game?.Player, MARK_PLAYER_BUFF)) return false;

        if (!_cd.Ready(MARK, CD_MARK_RETRY)) return false;

        var s = ctx.Find(MARK);
        if (s == null || s.Key.Value.Key == Keys.None) return false;

        // Tap(s) simples — previsível, sem hold prolongado. No boss faz BOSS_MARK_CASTS taps.
        var casts = isBoss ? BOSS_MARK_CASTS : 1;
        for (var i = 0; i < casts; i++)
            ctx.Skills.Tap(s.Key.Value.Key, s.TapHoldMs.Value);

        _cd.Mark(MARK);
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
