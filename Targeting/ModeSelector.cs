using System;
using AutoPilot.Detection;

namespace AutoPilot.Targeting;

/// <summary>
/// Decide o <see cref="CombatMode"/> ativo a cada tick, com HISTERESE para não saltar de modo
/// constantemente (o que causaria tremedeira de alvo pior que a do AutoMyAim).
///
/// Dois mecanismos de estabilidade:
///   • Banda morta (Schmitt trigger): entra em Danger quando há ≥ enterCount mobs perto, mas só
///     sai quando há &lt; exitCount (exit &lt; enter). Entre os dois, mantém o estado — sem saltos
///     quando a contagem oscila à volta do limiar.
///   • Tempo mínimo no modo: depois de entrar num modo, fica lá pelo menos minHoldMs antes de poder
///     mudar. Dá estabilidade temporal mesmo que a condição pisque.
///
/// Prioridade default: Danger &gt; Elite &gt; Normal (sobreviver primeiro). Configurável.
/// </summary>
public sealed class ModeSelector
{
    // ── Parâmetros (defaults; ligados aos settings na integração) ──────────────────────────
    public float DangerRadius { get; set; } = 35f;
    public int DangerEnterCount { get; set; } = 6;
    public int DangerExitCount { get; set; } = 3;
    public float EliteRange { get; set; } = 90f;
    public int MinHoldMs { get; set; } = 600;
    public int EliteGraceMs { get; set; } = 400;
    public bool DangerOverridesElite { get; set; } = true;

    private CombatMode _current = CombatMode.Normal;
    private long _modeEnteredTicks = DateTime.UtcNow.Ticks;
    private long _lastEliteSeenTicks;

    public CombatMode Current => _current;

    /// <summary>
    /// Reavalia o modo a partir do snapshot do <see cref="EntityCache"/>. Aplica histerese:
    /// o modo só muda se o tempo mínimo passou E a nova condição se mantém pela banda morta.
    /// </summary>
    public CombatMode Evaluate(EntityCache entities)
    {
        var now = DateTime.UtcNow.Ticks;
        var heldMs = (now - _modeEnteredTicks) / TimeSpan.TicksPerMillisecond;
        var canChange = heldMs >= MinHoldMs;

        var nearCount = entities.CountNearPlayer(DangerRadius);
        var hasElite = HasEliteInRange(entities, now);

        // Determina o modo "desejado" pela banda morta a partir do estado atual.
        var desired = ResolveDesired(nearCount, hasElite);

        if (desired != _current && canChange)
            EnterMode(desired, now);

        return _current;
    }

    /// <summary>Multiplicadores de peso do modo atual. Consumido pelo motor de pesos.</summary>
    public ModeProfile CurrentProfile => ProfileFor(_current);

    private CombatMode ResolveDesired(int nearCount, bool hasElite)
    {
        // Banda morta do Danger: depende de já estarmos (ou não) em Danger.
        bool danger = _current == CombatMode.Danger
            ? nearCount >= DangerExitCount   // já em danger: só sai abaixo do exit
            : nearCount >= DangerEnterCount; // fora de danger: só entra acima do enter

        if (danger && (DangerOverridesElite || !hasElite))
            return CombatMode.Danger;

        if (hasElite)
            return CombatMode.Elite;

        // Se há danger mas elite tem prioridade e existe elite, já retornámos Elite acima.
        // Aqui danger pode ser true só quando !DangerOverridesElite e !hasElite — coberto.
        return danger ? CombatMode.Danger : CombatMode.Normal;
    }

    private bool HasEliteInRange(EntityCache entities, long now)
    {
        foreach (var m in entities.Monsters)
        {
            if (m.Distance > EliteRange) continue;
            if (m.Rarity is ExileCore2.Shared.Enums.MonsterRarity.Rare
                         or ExileCore2.Shared.Enums.MonsterRarity.Unique)
            {
                _lastEliteSeenTicks = now;
                return true;
            }
        }

        // Grace: se vimos um elite há pouco (oclusão momentânea), mantém o modo Elite.
        var sinceEliteMs = (now - _lastEliteSeenTicks) / TimeSpan.TicksPerMillisecond;
        return _current == CombatMode.Elite && sinceEliteMs < EliteGraceMs;
    }

    private void EnterMode(CombatMode mode, long now)
    {
        _current = mode;
        _modeEnteredTicks = now;
    }

    private static ModeProfile ProfileFor(CombatMode mode) => mode switch
    {
        // Danger: distância domina (mais perto = mais peso); raridade/cluster quase ignorados.
        CombatMode.Danger => new ModeProfile(distance: 4.0f, rarity: 0.3f, cluster: 0.5f),

        // Elite: raridade domina; elites ganham peso esmagador mesmo rodeados de trash.
        CombatMode.Elite => new ModeProfile(distance: 1.0f, rarity: 4.0f, cluster: 0.5f),

        // Normal: pesos base equilibrados; cluster com bónus total (clear eficiente).
        _ => new ModeProfile(distance: 1.0f, rarity: 1.0f, cluster: 1.0f),
    };

    public void Reset()
    {
        _current = CombatMode.Normal;
        _modeEnteredTicks = DateTime.UtcNow.Ticks;
        _lastEliteSeenTicks = 0;
    }
}
