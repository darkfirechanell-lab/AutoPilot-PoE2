using System;
using System.Collections.Generic;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

namespace AutoPilot.Detection;

/// <summary>
/// Cache PARTILHADA do componente <see cref="Life"/> por entidade, com TTL por tempo real.
///
/// Extraída do WeightEngine (que tinha o seu cache privado) para que QUALQUER consumidor — o cálculo de
/// peso E o classificador de dureza — leia o MESMO objeto Life uma só vez por id por janela de TTL. Sem
/// isto, cada consumidor fazia o seu próprio <c>GetComponent&lt;Life&gt;()</c> = leituras de memória
/// duplicadas no hot-path (o que a auditoria da feature de dureza mandou evitar).
///
/// TTL por ms decorridos (determinístico em qualquer FPS), não por <c>TickCount % N</c>. Guarda a REF ao
/// objeto Life (não os valores): HPPercentage/MaxHP/MaxES lêem-se da ref a pedido, sempre frescos dentro
/// da janela. Leitura defensiva — qualquer falha = Life null (o consumidor decide o fallback).
/// </summary>
public sealed class LifeCache
{
    private const long TtlMs = 500;
    private readonly Dictionary<uint, (Life Life, long ReadAtTicks)> _cache = new();

    /// <summary>Devolve o Life cacheado da entidade (re-lê se expirou). null se ilegível.</summary>
    public Life Get(Entity entity, long nowTicks)
    {
        if (entity == null) return null;
        var id = entity.Id;
        if (!_cache.TryGetValue(id, out var cached)
            || (nowTicks - cached.ReadAtTicks) / TimeSpan.TicksPerMillisecond > TtlMs)
        {
            Life life = null;
            try { life = entity.GetComponent<Life>(); } catch { }
            cached = (life, nowTicks);
            _cache[id] = cached;
        }
        return cached.Life;
    }

    /// <summary>Fração de vida (0..1) incluindo ES, ou -1 se ilegível. (Padrão antigo do WeightEngine.)</summary>
    public float HpFraction(Entity entity, long nowTicks)
    {
        var l = Get(entity, nowTicks);
        if (l == null) return -1f;
        try { return l.HPPercentage + l.ESPercentage; }
        catch { return -1f; }
    }

    /// <summary>
    /// Pool efetiva MÁXIMA do mob = MaxHP + MaxES (vida + escudo, peso 1:1). false se ilegível / ≤ 0.
    /// MaxHP/MaxES confirmados em uso real (CharacterData-PoE2, AutoMyAim/TargetAnalyzer).
    /// </summary>
    public bool TryGetPool(Entity entity, long nowTicks, out float pool)
        => TryGetPool(entity, nowTicks, out pool, out _, out _);

    /// <summary>Como <see cref="TryGetPool(Entity,long,out float)"/> mas devolve MaxHP e MaxES separados (diagnóstico).</summary>
    public bool TryGetPool(Entity entity, long nowTicks, out float pool, out float maxHp, out float maxEs)
    {
        pool = 0f; maxHp = 0f; maxEs = 0f;
        var l = Get(entity, nowTicks);
        if (l == null) return false;
        try
        {
            maxHp = l.MaxHP;
            maxEs = l.MaxES;
            pool = maxHp + maxEs;
            return pool > 0f;
        }
        catch { return false; }
    }

    /// <summary>Remove entradas de entidades que já não estão no snapshot (mortas/fora de alcance).</summary>
    public void Prune(IReadOnlyList<TrackedEntity> monsters, long nowTicks)
    {
        if (_cache.Count == 0 || monsters == null) return;

        var alive = new HashSet<uint>();
        foreach (var m in monsters)
            if (m.Entity != null) alive.Add(m.Entity.Id);

        List<uint> stale = null;
        foreach (var kv in _cache)
            if (!alive.Contains(kv.Key))
                (stale ??= new List<uint>()).Add(kv.Key);

        if (stale == null) return;
        foreach (var id in stale) _cache.Remove(id);
    }

    public void Clear() => _cache.Clear();
}
