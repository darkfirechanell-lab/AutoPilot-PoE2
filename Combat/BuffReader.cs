using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

namespace AutoPilot.Combat;

/// <summary>
/// Leitura de buffs/debuffs de uma entidade (jogador ou alvo), de forma limpa e tolerante a falhas.
///
/// No AutoMyAim, cada routine reimplementava "tem este buff?" / "quantas charges?" com try/catch
/// próprios — código duplicado e fácil de divergir. Aqui está num só sítio. Métodos estáticos
/// porque a interface não tem estado próprio (é só ler a memória da entidade no momento).
///
/// B2 — CACHE POR-TICK: dentro de um mesmo tick, a rotação consulta os buffs ~8x (frozen? mark?
/// seals?). Em vez de reler a BuffsList da memória a cada chamada, lemos UMA vez por entidade por
/// tick e guardamos uma snapshot; as chamadas seguintes nesse tick reusam-na. O resultado lógico é
/// IDÊNTICO ao de reler sempre (a rotação já decide uma vez por tick) — não há TTL nem atraso de
/// timing: a cache é invalidada no início de cada tick por <see cref="NewTick"/>. É puramente menos
/// leituras de memória, zero mudança de comportamento.
///
/// Convenção de retorno das charges: ausente = 0; ilegível = -1 (a routine decide o que fazer com
/// cada caso — ex.: Salvo não arranca com leitura ilegível, mas trata ausente como 0 seals).
/// </summary>
public static class BuffReader
{
    /// <summary>Snapshot dos buffs de uma entidade lida neste tick.</summary>
    private sealed class Snapshot
    {
        public bool Readable;                                   // false = BuffsList ilegível neste tick
        public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Charges = new(StringComparer.OrdinalIgnoreCase);
    }

    // Chave = entidade; valor = snapshot do tick atual. Limpo a cada NewTick.
    private static readonly Dictionary<Entity, Snapshot> _cache = new();
    private static long _tick;

    /// <summary>
    /// Marca o início de um novo tick: invalida a cache. O plugin chama isto uma vez por Tick, antes
    /// de a rotação correr. Se nunca for chamado, a cache cresce dentro de um tick e nunca limpa —
    /// por isso o plugin DEVE chamar; ainda assim, é defensivo (só limpa um dicionário pequeno).
    /// </summary>
    public static void NewTick()
    {
        _tick++;
        _cache.Clear();
    }

    /// <summary>True se a entidade tem um buff/debuff com este nome interno (case-insensitive).</summary>
    public static bool Has(Entity entity, string buffName)
    {
        if (entity == null || string.IsNullOrEmpty(buffName)) return false;
        var snap = Get(entity);
        return snap.Readable && snap.Names.Contains(buffName);
    }

    /// <summary>True se a entidade tem um buff cujo nome CONTÉM o fragmento (ex.: "frozen").</summary>
    public static bool HasContaining(Entity entity, string fragment)
    {
        if (entity == null || string.IsNullOrEmpty(fragment)) return false;
        var snap = Get(entity);
        if (!snap.Readable) return false;
        foreach (var name in snap.Names)
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Número de charges de um buff. Ausente = 0; ilegível = -1.</summary>
    public static int Charges(Entity entity, string buffName)
    {
        if (entity == null || string.IsNullOrEmpty(buffName)) return -1;
        var snap = Get(entity);
        if (!snap.Readable) return -1;
        return snap.Charges.TryGetValue(buffName, out var c) ? c : 0;
    }

    /// <summary>
    /// Devolve a snapshot da entidade para o tick atual, lendo a memória só na primeira chamada deste
    /// tick. Em qualquer falha de leitura, marca Readable=false (igual ao comportamento antigo: Has
    /// devolvia false, Charges devolvia -1).
    /// </summary>
    private static Snapshot Get(Entity entity)
    {
        if (_cache.TryGetValue(entity, out var cached)) return cached;

        var snap = new Snapshot();
        try
        {
            if (entity.TryGetComponent<Buffs>(out var buffs) && buffs?.BuffsList != null)
            {
                snap.Readable = true;
                foreach (var b in buffs.BuffsList)
                {
                    var name = b?.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    snap.Names.Add(name);
                    // Preserva o comportamento antigo (FirstOrDefault): guarda as charges do PRIMEIRO
                    // buff com este nome; ignora ocorrências seguintes do mesmo nome.
                    if (!snap.Charges.ContainsKey(name))
                        snap.Charges[name] = b.BuffCharges;
                }
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
