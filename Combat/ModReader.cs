using System;
using System.Collections.Generic;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

namespace AutoPilot.Combat;

/// <summary>
/// M1 (plano dos mods): leitura dos MODS internos de um monstro (Archnemesis/raros), com cache por-tick.
/// Irmão do <see cref="BuffReader"/> — mesmo padrão: lê a memória UMA vez por entidade por tick e reusa
/// a snapshot nas chamadas seguintes do mesmo tick. Invalidada por <see cref="NewTick"/> (chamada uma
/// vez por Tick, ao lado do BuffReader.NewTick).
///
/// Fonte: <c>entity.GetComponent&lt;ObjectMagicProperties&gt;()?.Mods</c> → lista de strings com os nomes
/// internos (ex.: "MonsterCorpseExploder1", "MonsterVolatilePlants1"). Leitura defensiva: qualquer falha
/// = lista vazia / não casa (nunca rebenta o combate por um erro de leitura).
///
/// Validado pelo dump M0 (774 mods reais, nomes legíveis). Ver memórias monster-mods-reference e
/// archnemesis-mods-reference.
/// </summary>
public static class ModReader
{
    /// <summary>Snapshot dos mods de uma entidade lida neste tick.</summary>
    private sealed class Snapshot
    {
        public bool Readable;                       // false = Mods ilegível neste tick
        public readonly List<string> Mods = new();  // nomes internos crus (ordem do jogo)
    }

    private static readonly Dictionary<Entity, Snapshot> _cache = new();

    /// <summary>Invalida a cache no início de cada tick (chamar ao lado de BuffReader.NewTick).</summary>
    public static void NewTick() => _cache.Clear();

    /// <summary>Lista (cacheada) dos mods internos de uma entidade. Vazia se ilegível/sem mods.</summary>
    public static IReadOnlyList<string> GetMods(Entity entity)
    {
        if (entity == null) return Array.Empty<string>();
        return Get(entity).Mods;
    }

    /// <summary>True se algum mod da entidade casa o regex compilado (case-insensitive). Defensivo.</summary>
    public static bool HasModMatching(Entity entity, System.Text.RegularExpressions.Regex regex)
    {
        if (entity == null || regex == null) return false;
        var snap = Get(entity);
        if (!snap.Readable) return false;
        foreach (var m in snap.Mods)
        {
            if (string.IsNullOrEmpty(m)) continue;
            try { if (regex.IsMatch(m)) return true; } catch { }
        }
        return false;
    }

    private static Snapshot Get(Entity entity)
    {
        if (_cache.TryGetValue(entity, out var cached)) return cached;

        var snap = new Snapshot();
        try
        {
            var mods = entity.GetComponent<ObjectMagicProperties>()?.Mods;
            if (mods != null)
            {
                snap.Readable = true;
                foreach (var m in mods)
                    if (!string.IsNullOrEmpty(m)) snap.Mods.Add(m);
            }
        }
        catch
        {
            snap.Readable = false;
        }

        _cache[entity] = snap;
        return snap;
    }
}
