namespace AutoPilot.Targeting;

/// <summary>
/// Modo de combate ativo. O alvo "certo" depende do contexto, não de pesos fixos — esta é a
/// ideia central do targeting dinâmico do CombatRoutine.
/// </summary>
public enum CombatMode
{
    /// <summary>Clear normal: prioriza packs densos e estabilidade (sticky + cluster).</summary>
    Normal,

    /// <summary>Há Rare/Unique no alcance: foca os elites mesmo rodeado de trash.</summary>
    Elite,

    /// <summary>Muitos mobs colados ao jogador: foca o mais perto para limpar a ameaça. Sobrevivência.</summary>
    Danger,
}

/// <summary>
/// Multiplicadores que cada modo aplica aos componentes de peso base. Não há calculadoras de peso
/// separadas por modo — há UMA base (distância/raridade/cluster) e cada modo realça o que importa.
/// </summary>
public readonly struct ModeProfile
{
    public float DistanceMultiplier { get; init; }
    public float RarityMultiplier { get; init; }
    public float ClusterMultiplier { get; init; }

    public ModeProfile(float distance, float rarity, float cluster)
    {
        DistanceMultiplier = distance;
        RarityMultiplier = rarity;
        ClusterMultiplier = cluster;
    }
}
