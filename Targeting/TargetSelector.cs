using System;
using AutoPilot.Detection;

namespace AutoPilot.Targeting;

/// <summary>
/// Orquestra a escolha do alvo a cada tick, juntando todas as peças do targeting dinâmico:
///
///   1. <see cref="ModeSelector"/> decide o modo (Danger/Elite/Normal) com histerese.
///   2. <see cref="WeightEngine"/> calcula os pesos base e aplica o perfil do modo.
///   3. <see cref="ClusterEngine"/> dá bónus de densidade (escalado pelo modo).
///   4. <see cref="RayCaster"/> filtra alvos sem linha de vista.
///   5. Sticky target: mantém o alvo atual e só troca quando outro é claramente melhor e passou
///      o cooldown — evita a tremedeira DENTRO de um modo. Na mudança de modo, força reavaliação.
/// </summary>
public sealed class TargetSelector
{
    private readonly ModeSelector _modes;
    private readonly WeightEngine _weights;
    private readonly ClusterEngine _clusters;
    private readonly RayCaster _rays;

    public bool EnableVisibility { get; set; } = true;
    public bool EnableSticky { get; set; } = true;
    public int SwitchCooldownMs { get; set; } = 400;
    public float MinWeightAdvantage { get; set; } = 0.5f;

    private uint _currentTargetId;
    private long _lastSwitchTicks;
    private CombatMode _lastMode = CombatMode.Normal;

    // Diagnóstico para o HUD debug: onde é que os mobs se perdem na pipeline.
    public int DiagTotal { get; private set; }
    public int DiagWithWeight { get; private set; }
    public int DiagVisible { get; private set; }
    public float DiagNearestDist { get; private set; }

    public TargetSelector(ModeSelector modes, WeightEngine weights, ClusterEngine clusters, RayCaster rays)
    {
        _modes = modes;
        _weights = weights;
        _clusters = clusters;
        _rays = rays;
    }

    public CombatMode CurrentMode => _modes.Current;

    /// <summary>
    /// Escolhe o melhor alvo do snapshot atual. Devolve null se não houver nenhum mirável.
    /// </summary>
    public TrackedEntity SelectTarget(EntityCache entities)
    {
        if (entities.Monsters.Count == 0)
        {
            _currentTargetId = 0;
            return null;
        }

        // 1. Modo dinâmico (com histerese).
        var mode = _modes.Evaluate(entities);
        var profile = _modes.CurrentProfile;

        // 2+3. Pesos base × perfil do modo, depois bónus de cluster escalado pelo modo.
        _weights.Apply(entities.Monsters, profile);
        _clusters.Apply(entities.Monsters, profile.ClusterMultiplier);

        // Se o modo mudou, o critério de "melhor" mudou → solta o sticky e reavalia já.
        var modeChanged = mode != _lastMode;
        _lastMode = mode;

        // 4. Melhor candidato visível. (Com diagnóstico para o HUD debug.)
        TrackedEntity best = null;
        TrackedEntity current = null;
        DiagTotal = entities.Monsters.Count;
        DiagWithWeight = 0;
        DiagVisible = 0;
        DiagNearestDist = float.MaxValue;
        foreach (var m in entities.Monsters)
        {
            if (m.Distance < DiagNearestDist) DiagNearestDist = m.Distance;
            if (m.Weight <= 0f) continue;
            DiagWithWeight++;
            if (EnableVisibility && !_rays.IsVisible(entities.PlayerGridPos, m.Entity.GridPos)) continue;
            DiagVisible++;

            if (best == null || m.Weight > best.Weight) best = m;
            if (m.Entity.Id == _currentTargetId) current = m;
        }

        if (best == null)
        {
            _currentTargetId = 0;
            return null;
        }

        // 5. Sticky: mantém o atual a não ser que o melhor o supere por margem após o cooldown.
        var chosen = ApplySticky(best, current, modeChanged);
        _currentTargetId = chosen.Entity.Id;
        return chosen;
    }

    private TrackedEntity ApplySticky(TrackedEntity best, TrackedEntity current, bool modeChanged)
    {
        if (!EnableSticky || current == null || modeChanged)
        {
            _lastSwitchTicks = DateTime.UtcNow.Ticks;
            return best;
        }

        if (current.Entity.Id == best.Entity.Id)
            return current;

        var elapsedMs = (DateTime.UtcNow.Ticks - _lastSwitchTicks) / TimeSpan.TicksPerMillisecond;
        if (elapsedMs >= SwitchCooldownMs && best.Weight > current.Weight + MinWeightAdvantage)
        {
            _lastSwitchTicks = DateTime.UtcNow.Ticks;
            return best;
        }

        return current; // anti-flicker: fica no alvo atual
    }

    public void Reset()
    {
        _currentTargetId = 0;
        _lastSwitchTicks = 0;
        _lastMode = CombatMode.Normal;
        _modes.Reset();
    }
}
