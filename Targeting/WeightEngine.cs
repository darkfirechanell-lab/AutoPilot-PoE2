using System;
using System.Collections.Generic;
using AutoPilot.Detection;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Targeting;

/// <summary>
/// Calcula o peso base de cada alvo (distância, raridade, HP) e aplica o perfil do modo ativo.
///
/// Decisões face ao AutoMyAim:
///   • Cache de componentes (Life) com TTL por TEMPO REAL, não `TickCount % 100` (que em FPS alto
///     podia nunca disparar — era o problema M1). Aqui expira por ms decorridos, determinístico.
///   • Os pesos base são iguais para todos os modos; o ModeProfile só multiplica os componentes.
///     Assim a lógica dinâmica vive no ModeSelector e o cálculo é um só.
/// </summary>
public sealed class WeightEngine
{
    public float MaxDistance { get; set; } = 100f;
    public float BaseDistanceWeight { get; set; } = 2.0f;
    public float HpWeight { get; set; } = 1.0f;
    public bool PreferLowerHp { get; set; } = true;

    // Pesos base por raridade (antes do multiplicador do modo).
    public float RarityNormal { get; set; } = 1.0f;
    public float RarityMagic { get; set; } = 2.0f;
    public float RarityRare { get; set; } = 3.0f;
    public float RarityUnique { get; set; } = 4.0f;

    private const long LifeCacheTtlMs = 500;
    private readonly Dictionary<uint, (Life Life, long ReadAtTicks)> _lifeCache = new();

    // M2: regras de mod que ajustam o peso de targeting (+ prioriza, − desprioriza). Vazio = ignora.
    // LIMITE (auditoria): o delta total é clampado a ±MaxModDelta para NÃO dominar boss>rare>lixo —
    // os pesos de raridade vão ~1..4, por isso um delta de ±2 ajusta a ordem sem a inverter.
    public IReadOnlyList<Combat.General.ModRule> ModTargetingRules { get; set; }
    public float MaxModDelta { get; set; } = 2.0f;

    /// <summary>
    /// Atribui <see cref="TrackedEntity.Weight"/> a cada monstro, aplicando o perfil do modo.
    /// Alvos além de <see cref="MaxDistance"/> ficam com peso 0 (ignorados).
    /// </summary>
    public void Apply(IReadOnlyList<TrackedEntity> monsters, in ModeProfile profile)
    {
        var now = DateTime.UtcNow.Ticks;
        PruneCache(monsters, now);

        foreach (var m in monsters)
        {
            if (m.Distance > MaxDistance)
            {
                m.Weight = 0f;
                continue;
            }

            var distanceFactor = 1f - m.Distance / MaxDistance; // 1 perto, 0 longe
            var weight = distanceFactor * distanceFactor * BaseDistanceWeight * profile.DistanceMultiplier;

            weight += RarityBase(m.Rarity) * profile.RarityMultiplier * distanceFactor;

            var hp = ReadHpFraction(m.Entity, now);
            if (hp >= 0f)
            {
                var hpScore = PreferLowerHp ? 1f - hp : hp;
                weight += hpScore * HpWeight * distanceFactor;
            }

            // M2: delta por MOD (loot/perigo configurável), CLAMPADO para não dominar a raridade.
            weight += ModDelta(m.Entity);

            m.Weight = weight;
            // O bónus de cluster é aplicado depois, pelo ClusterEngine, escalado por profile.ClusterMultiplier.
        }
    }

    /// <summary>
    /// M2: soma o Peso das ModRules de targeting que casam o alvo, clampado a ±MaxModDelta (para
    /// ajustar a ordem sem inverter boss>rare>lixo). Sem regras = 0. Defensivo (nunca rebenta).
    /// </summary>
    private float ModDelta(Entity entity)
    {
        var rules = ModTargetingRules;
        if (rules == null || rules.Count == 0 || entity == null) return 0f;
        var delta = 0f;
        foreach (var r in rules)
            if (r != null && r.Matches(entity)) delta += r.Weight;
        return Math.Clamp(delta, -MaxModDelta, MaxModDelta);
    }

    private float RarityBase(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.White => RarityNormal,
        MonsterRarity.Magic => RarityMagic,
        MonsterRarity.Rare => RarityRare,
        MonsterRarity.Unique => RarityUnique,
        _ => 0f,
    };

    /// <summary>Fração de vida (0..1) incluindo ES, ou -1 se ilegível.</summary>
    private float ReadHpFraction(Entity entity, long now)
    {
        var id = entity.Id;
        if (!_lifeCache.TryGetValue(id, out var cached)
            || (now - cached.ReadAtTicks) / TimeSpan.TicksPerMillisecond > LifeCacheTtlMs)
        {
            Life life = null;
            try { life = entity.GetComponent<Life>(); } catch { }
            cached = (life, now);
            _lifeCache[id] = cached;
        }

        var l = cached.Life;
        if (l == null) return -1f;
        try { return l.HPPercentage + l.ESPercentage; }
        catch { return -1f; }
    }

    private void PruneCache(IReadOnlyList<TrackedEntity> monsters, long now)
    {
        if (_lifeCache.Count == 0) return;

        // Remove entradas de entidades que já não estão no snapshot (mortas/fora de alcance).
        var alive = new HashSet<uint>();
        foreach (var m in monsters) alive.Add(m.Entity.Id);

        List<uint> stale = null;
        foreach (var kv in _lifeCache)
            if (!alive.Contains(kv.Key))
                (stale ??= new List<uint>()).Add(kv.Key);

        if (stale == null) return;
        foreach (var id in stale) _lifeCache.Remove(id);
    }

    public void Clear() => _lifeCache.Clear();
}
