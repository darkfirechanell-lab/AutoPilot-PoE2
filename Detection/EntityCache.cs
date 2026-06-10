using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Detection;

/// <summary>
/// Faz UM scan de monstros por tick e serve todas as consultas a partir dessa lista já filtrada.
///
/// Porque existe (problemas reais do AutoMyAim que isto resolve):
///   • C3/C4 — lá, cada "quantos mobs perto?" / "quantos rods?" varria ValidEntitiesByType com
///     LINQ outra vez, várias vezes por tick. Aqui o varrimento é UM só (<see cref="Rebuild"/>),
///     e as contagens iteram a lista pequena já filtrada.
///   • M6 — sem locks nem ConcurrentDictionary: o ExileCore chama Tick() num só thread, portanto
///     coleções simples chegam e são mais rápidas.
///
/// Contrato: <see cref="Rebuild"/> é chamado uma vez no início do Tick (quando há processamento);
/// todas as outras consultas leem o snapshot desse tick.
/// </summary>
public sealed class EntityCache
{
    private readonly GameController _gc;
    private readonly List<TrackedEntity> _monsters = new(128);
    private Vector2 _playerGridPos;

    // R1 (REBUILD_PERF_PLANO): cache do IMUTÁVEL (Rarity) por id. A Rarity de um mob não muda enquanto
    // está vivo, mas era relida (ObjectMagicProperties) a CADA tick. Chave = id; guarda também o Address
    // como discriminador ANTI-RECICLAGEM (se o id reaparecer com Address diferente = mob NOVO → relê).
    // Validado pela micro-medição R1.0 (contadores abaixo). Limpo de ids mortos a cada Rebuild.
    private readonly Dictionary<uint, (MonsterRarity Rarity, long Address)> _rarityCache = new(128);
    private readonly HashSet<uint> _seenThisTick = new(128);

    // R1.5: cache do RESULTADO de BuffsBlockTarget (a leitura PATOLÓGICA — picou a 3.3ms num só mob).
    // Os buffs de imunidade/proximal mudam devagar, não precisam de re-leitura a cada frame. TTL 250ms
    // (re-lê 4x/s em vez de 60x/s → corta ~93% das leituras de buffs). Chave id+Address (anti-reciclagem);
    // re-lê quando o TTL expira OU o mob é novo. Guarda também o checkProximal usado (se mudar perto↔longe,
    // re-lê na hora — o proximal depende da distância).
    private readonly Dictionary<uint, (bool Blocked, long Address, long ReadAtTicks, bool CheckedProximal)> _buffCache = new(128);
    private const long BuffCacheTtlMs = 250;
    private int _buffReads, _buffCacheHits;
    // R1.0 — micro-medição: quantas vezes um id REAPAREEU com Address diferente (reciclagem detetada).
    // Se isto subir, a cache anti-reciclagem está a fazer o seu trabalho (e prova que era preciso).
    private int _recycleDetected;
    // R0.2 — medição FINA: onde estão os us do Rebuild? Tempo (us) em IsValidTarget vs CachedRarity, e o
    // nº de mobs no SOURCE bruto (antes do filtro). Picos com poucos mobs = leitura patológica, não O(n).
    private long _profValidUs, _profRarityUs, _profSourceUs, _profTotalUs, _profCleanupUs;
    private int _profSourceCount;
    private readonly System.Diagnostics.Stopwatch _totalSw = new();
    /// <summary>Diagnóstico R1/R0 para o debug.</summary>
    public string RebuildProfileLine() =>
        $"rebuildcache: TOTAL={_profTotalUs}us | source={_profSourceCount} src={_profSourceUs} valid={_profValidUs} " +
        $"(path={ProfPathUs} stats={ProfStatsUs} buffs={ProfBuffsUs}) rarity={_profRarityUs} cleanup={_profCleanupUs} | " +
        $"buff-hits={_buffCacheHits}/{_buffReads}";

    public EntityCache(GameController gameController)
    {
        _gc = gameController;
    }

    /// <summary>Os monstros hostis válidos do tick atual (já filtrados). Não modificar de fora.</summary>
    public IReadOnlyList<TrackedEntity> Monsters => _monsters;

    /// <summary>Posição (grid) do jogador no momento do último <see cref="Rebuild"/>.</summary>
    public Vector2 PlayerGridPos => _playerGridPos;

    /// <summary>
    /// Reconstrói o snapshot do tick: um único varrimento de EntityType.Monster, filtrando
    /// para alvos válidos e calculando distância+raridade já aqui. Limpa o tick anterior.
    /// </summary>
    public void Rebuild()
    {
        _totalSw.Restart();
        _monsters.Clear();

        var player = _gc?.Player;
        if (player == null) return;
        _playerGridPos = player.GridPos;

        List<Entity> source;
        var _srcSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            source = _gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster];
        }
        catch
        {
            return; // lista indisponível neste frame (transição de área, etc.)
        }
        _profSourceUs = _srcSw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
        if (source == null) return;

        _seenThisTick.Clear();
        _profSourceCount = source.Count; _profValidUs = 0; _profRarityUs = 0;
        ProfPathUs = 0; ProfStatsUs = 0; ProfBuffsUs = 0; // R0.2: reset das sub-medições do IsValidTarget.
        var _sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var entity in source)
        {
            var dist = Vector2.Distance(_playerGridPos, entity.GridPos);
            _sw.Restart();
            var valid = IsValidTarget(entity, dist);
            _profValidUs += _sw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
            if (!valid) continue;

            _seenThisTick.Add(entity.Id);
            _sw.Restart();
            var rarity = CachedRarity(entity);  // R1: reusa a Rarity cacheada.
            _profRarityUs += _sw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
            _monsters.Add(new TrackedEntity
            {
                Entity = entity,
                Distance = dist,
                Rarity = rarity,
            });
        }

        var _cleanSw = System.Diagnostics.Stopwatch.StartNew();
        // R1: limpa da cache os ids que já não estão no snapshot (mobs mortos/fora de alcance) — evita
        // crescer sem fim e libertar a entrada para um id reciclado.
        if (_rarityCache.Count > _seenThisTick.Count)
        {
            _staleIds.Clear();
            foreach (var kv in _rarityCache) if (!_seenThisTick.Contains(kv.Key)) _staleIds.Add(kv.Key);
            foreach (var id in _staleIds) _rarityCache.Remove(id);
        }
        // R1.5: limpa o buff-cache dos ids que saíram do snapshot.
        if (_buffCache.Count > _seenThisTick.Count)
        {
            _staleIds.Clear();
            foreach (var kv in _buffCache) if (!_seenThisTick.Contains(kv.Key)) _staleIds.Add(kv.Key);
            foreach (var id in _staleIds) _buffCache.Remove(id);
        }
        _profCleanupUs = _cleanSw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
        _profTotalUs = _totalSw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
    }

    private readonly List<uint> _staleIds = new(64);

    /// <summary>
    /// R1: Rarity cacheada por id, com discriminador Address (anti-reciclagem). Se o id é novo OU o mesmo
    /// id reaparece com Address diferente (= outro mob reutilizou o id) → relê e atualiza a cache.
    /// </summary>
    private MonsterRarity CachedRarity(Entity entity)
    {
        var id = entity.Id;
        long addr;
        try { addr = (long)entity.Address; } catch { return ReadRarity(entity); }

        if (_rarityCache.TryGetValue(id, out var cached))
        {
            if (cached.Address == addr) return cached.Rarity; // mesmo mob → reusa (sem leitura cara).
            _recycleDetected++;                                // id reciclado por outro mob → relê.
        }

        var rarity = ReadRarity(entity);
        _rarityCache[id] = (rarity, addr);
        return rarity;
    }

    /// <summary>
    /// Limiar (unidades de grid) do Proximal Tangibility: um alvo com esse mod só é tangível/atacável
    /// quando está MAIS PERTO que isto. De longe é imune → filtra-se (não desperdiça tiros). Ajustável
    /// pelo utilizador. Default conservador (perto) — se ele não atacar o boss à distância certa, sobe.
    /// </summary>
    public static float ProximalTangibilityRange { get; set; } = 25f;

    /// <summary>Conta monstros vivos dentro de <paramref name="radius"/> de um ponto do grid.</summary>
    public int CountWithin(Vector2 gridCenter, float radius)
    {
        var count = 0;
        foreach (var m in _monsters)
            if (Vector2.Distance(m.Entity.GridPos, gridCenter) <= radius)
                count++;
        return count;
    }

    /// <summary>Conta monstros de uma raridade dentro de <paramref name="radius"/> de um ponto.</summary>
    public int CountWithin(Vector2 gridCenter, float radius, MonsterRarity rarity)
    {
        var count = 0;
        foreach (var m in _monsters)
            if (m.Rarity == rarity && Vector2.Distance(m.Entity.GridPos, gridCenter) <= radius)
                count++;
        return count;
    }

    /// <summary>Conta monstros vivos dentro de <paramref name="radius"/> do jogador.</summary>
    public int CountNearPlayer(float radius) => CountWithin(_playerGridPos, radius);

    public void Clear()
    {
        _monsters.Clear();
    }

    // ── Filtros ──────────────────────────────────────────────────────────────────────────

    private static readonly string[] ExcludedPrefixes =
    {
        // Mods de monstro (auras/efeitos) e construtos não-alvejáveis que poluíam o scan no AutoMyAim.
        "Metadata/Monsters/MonsterMods/",
        "Metadata/Monsters/VaalConstructs/Cycloning/VaalCycloneConstructArmsSpawned",
    };

    // Buffs/debuffs que tornam um alvo imune A TUDO (não elemental) — bosses que clonam, Divine Shrine,
    // etc. Sem isto o aim desperdiçava-se neles. Usamos Contains, por isso "cannot_be_damaged" já
    // apanha as variantes cannot_be_damaged_by_things_outside_radius / _for_ / _by_enemies (Fase 0).
    //
    // CUIDADO (validado a pente fino): NÃO incluir fragmentos de imunidade ELEMENTAL (base_fire_immunity,
    // base_cold_immunity, etc.) — um mob imune a fogo não é imune a gelo; filtrá-lo seria errado e
    // depende da build. O "immune" genérico fica porque cobre invulnerabilidade total; os elementais
    // específicos serão tratados (se preciso) por filtros de elemento na Routine Geral, não aqui.
    private static readonly string[] InvulnBuffFragments =
    {
        "invulnerable", "cannot_die", "cannot_be_damaged", "immune", "damage_immunity", "untargetable",
        "divine_shrine", // Divine Shrine: imunidade total temporária enquanto o buff está ativo.
    };

    /// <summary>
    /// Um alvo só conta se for um monstro hostil, vivo, visível e ALVEJÁVEL E DANIFICÁVEL.
    /// Filtra invulnerabilidade por três vias (cobre clones de boss): a stat CannotBeDamaged,
    /// a flag IsTargetable, e buffs de imunidade. Atacar um alvo imune é desperdício de DPS.
    /// O <paramref name="distance"/> serve para o Proximal Tangibility (imune só à distância).
    /// </summary>
    // R0.2 medição fina DENTRO do IsValidTarget: tempo (us) em Path vs Stats vs Buffs. Acumula por tick;
    // o Rebuild faz reset no início e expõe na linha rebuildcache. Estático porque IsValidTarget é static.
    internal static long ProfPathUs, ProfStatsUs, ProfBuffsUs;
    private static readonly System.Diagnostics.Stopwatch _ivtSw = new();

    private bool IsValidTarget(Entity entity, float distance)
    {
        if (entity == null) return false;
        if (!entity.IsValid || !entity.IsAlive || entity.IsDead) return false;
        if (!entity.IsTargetable || entity.IsHidden || !entity.IsHostile) return false;

        _ivtSw.Restart();
        var path = entity.Path;
        if (path != null)
            foreach (var prefix in ExcludedPrefixes)
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                { ProfPathUs += _ivtSw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency; return false; }
        ProfPathUs += _ivtSw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;

        // Stat de invulnerabilidade (a via principal). Stats pode ser null → assume danificável.
        _ivtSw.Restart();
        var stats = entity.Stats;
        var blockedByStat = stats != null && stats.TryGetValue(GameStat.CannotBeDamaged, out var v) && v == 1;
        ProfStatsUs += _ivtSw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
        if (blockedByStat) return false;

        // PERF: lê o componente Buffs UMA SÓ VEZ e percorre a BuffsList UMA vez, verificando as duas
        // coisas (imunidade total + proximal intangibility) no mesmo varrimento. Antes liam-se os buffs
        // 2x por mob — num pack de 40+ mobs isso dobrava a leitura de memória mais cara, a cada tick
        // (causa provável do micro-stutter). O proximal só importa à distância.
        _ivtSw.Restart();
        var checkProximal = distance > ProximalTangibilityRange;
        var blockedByBuff = BuffsBlockCached(entity, checkProximal); // R1.5: cache TTL 250ms.
        ProfBuffsUs += _ivtSw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
        if (blockedByBuff) return false;

        return true;
    }

    /// <summary>
    /// R1.5: BuffsBlockTarget COM cache (TTL 250ms). Reusa o resultado se o mesmo mob (id+Address) foi
    /// lido há &lt;250ms COM o mesmo checkProximal. Re-lê se: mob novo, TTL expirou, ou o checkProximal
    /// mudou (perto↔longe — o proximal depende da distância, tem de reavaliar). Corta ~93% das leituras.
    /// </summary>
    private bool BuffsBlockCached(Entity entity, bool checkProximal)
    {
        var now = DateTime.UtcNow.Ticks;
        long addr; try { addr = (long)entity.Address; } catch { _buffReads++; return BuffsBlockTarget(entity, checkProximal); }
        var id = entity.Id;

        if (_buffCache.TryGetValue(id, out var c)
            && c.Address == addr
            && c.CheckedProximal == checkProximal
            && (now - c.ReadAtTicks) / TimeSpan.TicksPerMillisecond < BuffCacheTtlMs)
        {
            _buffCacheHits++;
            return c.Blocked; // dentro do TTL, mesmo mob, mesma condição → reusa.
        }

        _buffReads++;
        var blocked = BuffsBlockTarget(entity, checkProximal);
        _buffCache[id] = (blocked, addr, now, checkProximal);
        return blocked;
    }

    /// <summary>
    /// UMA leitura dos buffs do mob: devolve true se algum buff o torna inválido como alvo — imunidade
    /// total (InvulnBuffFragments) OU (se <paramref name="checkProximal"/>) proximal intangibility.
    /// </summary>
    private static bool BuffsBlockTarget(Entity entity, bool checkProximal)
    {
        try
        {
            if (!entity.TryGetComponent<Buffs>(out var buffs) || buffs?.BuffsList == null)
                return false;

            foreach (var b in buffs.BuffsList)
            {
                var name = b?.Name;
                if (string.IsNullOrEmpty(name)) continue;

                if (checkProximal && name.Contains("proximal_intangibility", StringComparison.OrdinalIgnoreCase))
                    return true;

                foreach (var frag in InvulnBuffFragments)
                    if (name.Contains(frag, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }
        catch { }
        return false;
    }

    private static MonsterRarity ReadRarity(Entity entity)
    {
        try
        {
            return entity.GetComponent<ObjectMagicProperties>()?.Rarity ?? MonsterRarity.White;
        }
        catch
        {
            return MonsterRarity.White;
        }
    }
}
