using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Detection;

/// <summary>
/// Um monstro hostil válido captado num scan, com os dados derivados que o resto do plugin
/// usa. Calculamos a distância e a raridade UMA vez no scan (não a cada leitura de memória),
/// porque ler componentes do processo do jogo é caro e repetia-se imenso no AutoMyAim.
/// </summary>
public sealed class TrackedEntity
{
    /// <summary>A entidade do jogo. Pode ficar inválida entre ticks — revalidar antes de usar.</summary>
    public Entity Entity { get; init; }

    /// <summary>Distância ao jogador em unidades de grid, no momento do scan.</summary>
    public float Distance { get; set; }

    /// <summary>Raridade lida no scan (White/Magic/Rare/Unique). Evita re-ler o componente.</summary>
    public MonsterRarity Rarity { get; init; }

    /// <summary>Peso de targeting atribuído pelo motor de pesos (Fase 3). 0 até lá.</summary>
    public float Weight { get; set; }
}

/// <summary>
/// Um mob com o buff Proximal Tangibility, captado no scan para o HUD desenhar a marca.
/// <see cref="Immune"/> = está imune AGORA (mais longe que o limiar configurado).
/// </summary>
public sealed class ProximalMark
{
    public Entity Entity { get; init; }

    /// <summary>true se imune à distância neste tick (longe); false se já estás perto o suficiente para o atacar.</summary>
    public bool Immune { get; init; }
}
