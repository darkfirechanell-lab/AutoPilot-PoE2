using System.Collections.Generic;
using AutoPilot.Detection;
using AutoPilot.Input;
using ExileCore2;

namespace AutoPilot.Combat;

/// <summary>
/// Tudo o que uma routine precisa para decidir e agir num tick, passado num só objeto.
///
/// Em vez de cada routine ir buscar GameController/skills/animação por conta própria (como no
/// AutoMyAim, onde dependiam de um singleton estático global), recebem este contexto já pronto.
/// Mantém as routines testáveis e desacopladas, e deixa explícito o que cada uma pode tocar.
/// </summary>
public sealed class RoutineContext
{
    public GameController Game { get; init; }
    public SkillExecutor Skills { get; init; }
    public AnimationReader Animation { get; init; }
    public EntityCache Entities { get; init; }

    /// <summary>O alvo escolhido pelo targeting neste tick (pode ser null).</summary>
    public TrackedEntity Target { get; set; }

    /// <summary>
    /// C1: o cursor está suficientemente em cima do alvo para acertar? Default true (se o C1 estiver
    /// desligado, nunca bloqueia). A routine consulta isto para skills de DANO DIRETO (Ice Shot,
    /// Barrage). NÃO bloqueia Mark (utilitária) nem o fecho de canais já em curso.
    /// </summary>
    public bool CanHit { get; set; } = true;

    /// <summary>
    /// HP_ROTATION: nível de dureza do alvo deste tick (Easy/Medium/Tank). Calculado 1x/tick no plugin
    /// (não na routine), tal como o <see cref="CanHit"/>. O RuleEvaluator usa-o como gate por skill.
    /// Default Easy = não filtra (se o classificador não correr, nada muda).
    /// </summary>
    public General.TargetHardness TargetHardness { get; set; } = General.TargetHardness.Easy;

    /// <summary>
    /// Detetor de entidades no chão (tornado, sino, totem…). O RuleEvaluator usa-o para o gate
    /// GroundEntityPath/SkipIfGroundActive de cada skill. Pode ser null (gate ignora-se nesse caso).
    /// </summary>
    public GroundEntityTracker Ground { get; init; }

    /// <summary>Alcance (grid) para a deteção de entidades no chão à volta do alvo. Default = um valor seguro.</summary>
    public float GroundRange { get; set; } = 60f;

    /// <summary>As skills configuradas/detetadas, com a ref viva já religada.</summary>
    public List<SkillSlot> SkillSlots { get; init; }

    /// <summary>Encontra um slot pelo nome de memória (ex.: "BarragePlayer"), pronto a usar ou não.</summary>
    public SkillSlot Find(string name)
    {
        if (SkillSlots == null) return null;
        foreach (var s in SkillSlots)
            if (s.Enabled.Value && s.Name == name)
                return s;
        return null;
    }
}
