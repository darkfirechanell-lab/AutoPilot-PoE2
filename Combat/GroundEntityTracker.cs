using System;
using System.Numerics;
using ExileCore2;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Combat;

/// <summary>
/// Deteta ENTIDADES no chão criadas por skills (tornado, sino, totem, poça, rod…). Genérico: a skill diz
/// o path (ex.: "TornadoShotTornado") e isto procura nas <see cref="EntityType.MiscellaneousObjects"/>.
///
/// Serve qualquer build — é o equivalente "no chão" dos gates de buff/debuff: em vez de "o alvo tem o
/// buff X", é "já existe a entidade X no chão perto". Usa-se para uptime sem spam (não re-lançar enquanto
/// há um vivo). Match por SUBSTRING case-insensitive: basta a parte final do path (como os nomes de buff).
///
/// SÓ no range pedido (à volta do alvo/jogador) — um objeto noutra sala do mapa não conta. Reusa o padrão
/// do AutoMyAim (LightningRod). Leitura defensiva; na dúvida devolve "não há" (deixa a skill usar).
/// </summary>
public sealed class GroundEntityTracker
{
    private readonly GameController _gc;
    public GroundEntityTracker(GameController gc) => _gc = gc;

    /// <summary>
    /// True se há uma entidade cujo path contém <paramref name="pathFragment"/> viva dentro de
    /// <paramref name="radius"/> do ponto. Fragmento vazio = false (nada a detetar).
    /// </summary>
    public bool AnyNear(string pathFragment, Vector2 gridCenter, float radius)
    {
        if (string.IsNullOrEmpty(pathFragment)) return false;
        try
        {
            var list = _gc.EntityListWrapper.ValidEntitiesByType[EntityType.MiscellaneousObjects];
            if (list == null) return false;
            foreach (var e in list)
            {
                if (e == null || !e.IsValid) continue;
                var path = e.Path;
                if (string.IsNullOrEmpty(path)) continue;
                if (path.IndexOf(pathFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (Vector2.Distance(e.GridPos, gridCenter) <= radius) return true;
            }
        }
        catch { /* defensivo: na dúvida assume que não há (deixa a skill usar) */ }
        return false;
    }
}
