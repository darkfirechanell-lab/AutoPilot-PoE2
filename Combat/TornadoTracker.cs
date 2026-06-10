using System;
using System.Numerics;
using ExileCore2;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Combat;

/// <summary>
/// Deteta tornados de Tornado Shot no chão. O tornado é uma ENTIDADE do mundo
/// (<c>Metadata/MiscellaneousObjects/TornadoShotTornado</c>), não um buff no alvo — por isso dá para
/// saber se já há um ativo (e onde) em vez de adivinhar pelo cooldown.
///
/// Mecânica (poe2db): o tornado dura ~15s e é um MULTIPLICADOR de projéteis (Ice Shot/Snipe disparados
/// através dele cospem cópias). Logo a lógica certa é: só re-lançar quando NÃO há nenhum tornado vivo
/// perto do alvo. Reusa o padrão do AutoMyAim (LightningRod) sobre EntityType.MiscellaneousObjects.
///
/// Custo: um varrimento de MiscellaneousObjects 1×/tick (poucos objetos). Resultado cacheado no tick.
/// </summary>
public sealed class TornadoTracker
{
    private const string TornadoPath = "Metadata/MiscellaneousObjects/TornadoShotTornado";

    private readonly GameController _gc;
    public TornadoTracker(GameController gc) => _gc = gc;

    /// <summary>
    /// True se há pelo menos um tornado vivo dentro de <paramref name="radius"/> do ponto dado. SÓ no
    /// range pedido — um tornado noutra sala do mapa NÃO conta (não bloqueia o lançamento aqui). O ponto
    /// é tipicamente o ALVO (onde os projéteis vão passar) ou o jogador; o raio é o teu attack range.
    /// </summary>
    public bool AnyNear(Vector2 gridCenter, float radius)
    {
        try
        {
            var list = _gc.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects];
            if (list == null) return false;
            foreach (var e in list)
            {
                if (e == null || !e.IsValid) continue;
                if (!string.Equals(e.Path, TornadoPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (Vector2.Distance(e.GridPos, gridCenter) <= radius) return true;
            }
        }
        catch { /* leitura defensiva: na dúvida assume que não há (deixa lançar) */ }
        return false;
    }
}
