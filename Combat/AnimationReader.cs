using ExileCore2;
using ExileCore2.PoEMemory.Components;

namespace AutoPilot.Combat;

/// <summary>
/// Lê o estado de animação do jogador UMA vez por tick e expõe-no de forma limpa às routines.
///
/// Existe por causa de um problema concreto: skills diferentes precisam de sinais diferentes.
///   • Snipe (canalizada) → o <see cref="Stage"/> avança até ao ponto de release (~21). Útil.
///   • Barrage (não canalizada) → o Stage fica a 0 durante toda a animação (~500ms). INÚTIL.
///     Para o Barrage usamos <see cref="Progress"/> (float que avança) e <see cref="AnimationId"/>
///     (muda enquanto a animação corre), com fallback de tempo na routine.
///
/// Esta peça NÃO decide nada — só lê e expõe. A interpretação ("o Barrage ainda está a animar?")
/// vive na routine, que combina estes sinais. Tudo num único ponto de leitura para o HUD debug
/// poder mostrar os valores ao vivo e confirmarmos, a jogar, qual sinal é fiável.
/// </summary>
public sealed class AnimationReader
{
    private readonly GameController _gc;

    public int Stage { get; private set; } = -1;
    public int AnimationId { get; private set; } = -1;
    public float Progress { get; private set; }
    public string Action { get; private set; } = "";

    /// <summary>Quanto FALTA (ms) para a animação atual acabar (API AnimationCompletesIn). -1 se ilegível.</summary>
    public double CompletesInMs { get; private set; } = -1;
    /// <summary>Há quanto tempo (ms) a animação atual corre (API AnimationActiveFor). -1 se ilegível.</summary>
    public double ActiveForMs { get; private set; } = -1;

    public AnimationReader(GameController gameController)
    {
        _gc = gameController;
    }

    /// <summary>Relê o estado de animação do jogador. Chamar uma vez por tick antes das routines.</summary>
    public void Update()
    {
        Stage = -1;
        AnimationId = -1;
        Progress = 0f;
        Action = "";
        CompletesInMs = -1;
        ActiveForMs = -1;

        var player = _gc?.Player;
        if (player == null) return;
        if (!player.TryGetComponent<Actor>(out var actor) || actor == null) return;

        try { Action = actor.Action.ToString(); } catch { }

        var ac = actor.AnimationController;
        if (ac == null) return;

        try { Stage = ac.CurrentAnimationStage; } catch { }
        try { AnimationId = ac.CurrentAnimationId; } catch { }
        try { Progress = ac.AnimationProgress; } catch { }
        try { CompletesInMs = ac.AnimationCompletesIn.TotalMilliseconds; } catch { }
        try { ActiveForMs = ac.AnimationActiveFor.TotalMilliseconds; } catch { }
    }

    /// <summary>True se o estágio de animação atingiu o ponto de release (para skills canalizadas).</summary>
    public bool StageReached(int releaseStage) => Stage >= releaseStage;

    /// <summary>Resumo legível para o HUD debug — os 3 sinais lado a lado.</summary>
    public string DebugLine() => $"anim id={AnimationId} stage={Stage} progress={Progress:F2} " +
        $"completesIn={CompletesInMs:F0}ms activeFor={ActiveForMs:F0}ms action={Action}";
}
