using System;
using System.Collections.Generic;
using System.Numerics;
using AutoPilot.Detection;

namespace AutoPilot.Targeting;

/// <summary>
/// Dá um bónus de peso aos mobs que estão em packs densos, usando uma GRELHA ESPACIAL.
///
/// Porquê grelha (resolve M2): o AutoMyAim comparava cada mob com todos os outros (O(n²)) e ainda
/// fazia um merge em loop por cima. Aqui dividimos o espaço em células de lado = raio do cluster;
/// para contar vizinhos de um mob só olhamos a sua célula e as 8 adjacentes. Isso é O(n) na prática
/// porque cada mob só compara com os poucos que partilham vizinhança — escala em Breach/packs grandes.
///
/// O bónus é proporcional à densidade local e é escalado pelo multiplicador de cluster do modo
/// (ex.: em Danger o cluster quase não conta; em Normal conta a 100%).
/// </summary>
public sealed class ClusterEngine
{
    public float ClusterRadius { get; set; } = 25f;
    public int MinNeighbours { get; set; } = 2;     // além do próprio, para contar como "em pack"
    public float BonusPerNeighbour { get; set; } = 0.1f;
    public float MaxBonusMultiplier { get; set; } = 2.0f;

    // Reutilizados entre ticks para não alocar (plugin single-thread, sem locks).
    private readonly Dictionary<(int, int), List<int>> _grid = new();

    /// <summary>
    /// Multiplica o peso de cada mob por um bónus de densidade local. Deve correr DEPOIS do
    /// <see cref="WeightEngine"/> (precisa dos pesos base já atribuídos).
    /// </summary>
    public void Apply(IReadOnlyList<TrackedEntity> monsters, float clusterMultiplier)
    {
        if (monsters.Count == 0 || clusterMultiplier <= 0f) return;

        BuildGrid(monsters);

        var radiusSq = ClusterRadius * ClusterRadius;

        for (var i = 0; i < monsters.Count; i++)
        {
            var pos = monsters[i].Entity.GridPos;
            var neighbours = CountNeighbours(monsters, i, pos, radiusSq);
            if (neighbours < MinNeighbours) continue;

            // Bónus cresce com a densidade, limitado pelo teto, e escalado pelo modo.
            var rawBonus = 1f + (neighbours - MinNeighbours + 1) * BonusPerNeighbour;
            if (rawBonus > MaxBonusMultiplier) rawBonus = MaxBonusMultiplier;

            // Mistura o bónus pelo multiplicador do modo: cluster=1 → bónus total; cluster=0 → sem bónus.
            var effective = 1f + (rawBonus - 1f) * clusterMultiplier;
            monsters[i].Weight *= effective;
        }
    }

    private void BuildGrid(IReadOnlyList<TrackedEntity> monsters)
    {
        _grid.Clear();
        for (var i = 0; i < monsters.Count; i++)
        {
            var cell = CellOf(monsters[i].Entity.GridPos);
            if (!_grid.TryGetValue(cell, out var list))
            {
                list = new List<int>(8);
                _grid[cell] = list;
            }
            list.Add(i);
        }
    }

    private int CountNeighbours(IReadOnlyList<TrackedEntity> monsters, int self, Vector2 pos, float radiusSq)
    {
        var (cx, cy) = CellOf(pos);
        var count = 0;

        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        {
            if (!_grid.TryGetValue((cx + dx, cy + dy), out var bucket)) continue;
            foreach (var j in bucket)
            {
                if (j == self) continue;
                if (Vector2.DistanceSquared(pos, monsters[j].Entity.GridPos) <= radiusSq)
                    count++;
            }
        }

        return count;
    }

    private (int, int) CellOf(Vector2 pos)
    {
        // Lado da célula = raio do cluster → vizinhos relevantes caem na própria célula + adjacentes.
        var size = MathF.Max(1f, ClusterRadius);
        return ((int)MathF.Floor(pos.X / size), (int)MathF.Floor(pos.Y / size));
    }

    public void Clear() => _grid.Clear();
}
