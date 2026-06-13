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
    private readonly List<ProximalMark> _proximal = new(16);
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

    public EntityCache(GameController gameController)
    {
        _gc = gameController;
    }

    /// <summary>Os monstros hostis válidos do tick atual (já filtrados). Não modificar de fora.</summary>
    public IReadOnlyList<TrackedEntity> Monsters => _monsters;

    /// <summary>
    /// Mobs com o buff Proximal Tangibility neste tick — INCLUSIVE os que o filtro de validade exclui
    /// (imunes à distância). Serve só ao HUD para desenhar a marca. <see cref="ProximalMark.Immune"/>
    /// diz se está imune AGORA (mais longe que <see cref="ProximalTangibilityRange"/>).
    /// </summary>
    public IReadOnlyList<ProximalMark> ProximalEntities => _proximal;

    /// <summary>Posição (grid) do jogador no momento do último <see cref="Rebuild"/>.</summary>
    public Vector2 PlayerGridPos => _playerGridPos;

    /// <summary>
    /// Reconstrói o snapshot do tick: um único varrimento de EntityType.Monster, filtrando
    /// para alvos válidos e calculando distância+raridade já aqui. Limpa o tick anterior.
    /// </summary>
    public void Rebuild()
    {
        _monsters.Clear();
        _proximal.Clear();

        var player = _gc?.Player;
        if (player == null) return;
        _playerGridPos = player.GridPos;

        List<Entity> source;
        try
        {
            source = _gc.EntityListWrapper.ValidEntitiesByType[EntityType.Monster];
        }
        catch
        {
            return; // lista indisponível neste frame (transição de área, etc.)
        }
        if (source == null) return;

        _seenThisTick.Clear();
        foreach (var entity in source)
        {
            var dist = Vector2.Distance(_playerGridPos, entity.GridPos);

            // HUD: marca mobs com Proximal Tangibility ANTES do filtro (o filtro exclui-os à distância,
            // mas queremos a marca mesmo enquanto imunes). Só mobs vivos/visíveis, para não marcar lixo.
            if (entity != null && entity.IsValid && entity.IsAlive && !entity.IsDead
                && entity.IsTargetable && !entity.IsHidden && entity.IsHostile
                && HasProximalBuff(entity))
                _proximal.Add(new ProximalMark { Entity = entity, Immune = dist > ProximalTangibilityRange });

            if (!IsValidTarget(entity, dist)) continue;

            _seenThisTick.Add(entity.Id);
            _monsters.Add(new TrackedEntity
            {
                Entity = entity,
                Distance = dist,
                Rarity = CachedRarity(entity),  // R1: reusa a Rarity cacheada (não relê ObjectMagicProperties).
            });
        }

        // R1: limpa da cache os ids que já não estão no snapshot (mobs mortos/fora de alcance).
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

        if (_rarityCache.TryGetValue(id, out var cached) && cached.Address == addr)
            return cached.Rarity; // mesmo mob → reusa (sem leitura cara); Address diferente = reciclado → relê.

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
        _proximal.Clear();
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

    // EXCEÇÕES ao fragmento "immune": buffs que CONTÊM "immune" no nome mas NÃO são, por si só, prova de
    // imunidade do PRÓPRIO mob — apanhados por engano pelo Contains("immune").
    //   monster_mod_abyss_immune_aura: aura de Abyss presente DENTRO e FORA da névoa (confirmado por probes
    //   do mesmo Lightless Moray) → não marca presença-na-névoa, logo não deve bloquear sozinho. A
    //   imunidade REAL do raro dentro da névoa é a stat BaseCannotBeDamagedByEnemies (ver IsValidTarget),
    //   que dentro=1 / fora=0. Por isso excecionamos o buff aqui E filtramos a stat lá em baixo.
    private static readonly string[] InvulnBuffExceptions =
    {
        "abyss_immune_aura",
    };

    /// <summary>
    /// Um alvo só conta se for um monstro hostil, vivo, visível e ALVEJÁVEL E DANIFICÁVEL.
    /// Filtra invulnerabilidade por três vias (cobre clones de boss): a stat CannotBeDamaged,
    /// a flag IsTargetable, e buffs de imunidade. Atacar um alvo imune é desperdício de DPS.
    /// O <paramref name="distance"/> serve para o Proximal Tangibility (imune só à distância).
    /// </summary>
    private bool IsValidTarget(Entity entity, float distance)
    {
        if (entity == null) return false;
        if (!entity.IsValid || !entity.IsAlive || entity.IsDead) return false;
        if (!entity.IsTargetable || entity.IsHidden || !entity.IsHostile) return false;

        var path = entity.Path;
        if (path != null)
            foreach (var prefix in ExcludedPrefixes)
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                    return false;

        // Stat de invulnerabilidade (a via principal). Stats pode ser null → assume danificável.
        var stats = entity.Stats;
        if (stats != null)
        {
            // CannotBeDamaged: invulnerabilidade resolvida (clones de boss, Divine Shrine, etc.).
            if (stats.TryGetValue(GameStat.CannotBeDamaged, out var v) && v == 1)
                return false;
            // BaseCannotBeDamaged: monstros presos no Monolith / hidden (essências, Blackjaw) — imunes
            // ATÉ o jogador interagir (clicar). Confirmado por probe: têm BaseCannotBeDamaged=1 +
            // IsHiddenMonster=1 + MonsterInsideMonolith=1, mas IsTargetable=true e SEM CannotBeDamaged
            // resolvido — por isso passavam o filtro e a routine disparava neles em vão. Ver essencias-fechadas.
            if (stats.TryGetValue(GameStat.BaseCannotBeDamaged, out var bv) && bv == 1)
                return false;
            // BaseCannotBeDamagedByEnemies: raros (e fodder) Abyss DENTRO da névoa (Lightless Well) ficam
            // imunes a inimigos até serem atraídos para fora. Confirmado por par de probes do MESMO Lightless
            // Moray: DENTRO = stat 1 + buffs abyss_lightless_well(_immune), HP não baixava; FORA = stat 0,
            // buffs sumiam, levava DoT. Sem este filtro o aim desperdiçava-se num alvo imune. Ver
            // abyss-immune-aura-targeting-bug. (O buff _immune_aura é excecionado acima por aparecer dentro E fora.)
            if (stats.TryGetValue(GameStat.BaseCannotBeDamagedByEnemies, out var bve) && bve == 1)
                return false;
        }

        // PERF: lê o componente Buffs UMA SÓ VEZ e percorre a BuffsList UMA vez, verificando as duas
        // coisas (imunidade total + proximal intangibility) no mesmo varrimento. Antes liam-se os buffs
        // 2x por mob — num pack de 40+ mobs isso dobrava a leitura de memória mais cara, a cada tick
        // (causa provável do micro-stutter). O proximal só importa à distância.
        var checkProximal = distance > ProximalTangibilityRange;
        if (BuffsBlockCached(entity, checkProximal)) return false; // R1.5: cache TTL 250ms.

        return true;
    }

    /// <summary>DIAGNÓSTICO: a razão pela qual uma entidade falha o IsValidTarget (para o log do boss).</summary>
    /// <summary>
    /// R1.5: BuffsBlockTarget COM cache (TTL 250ms). Reusa o resultado se o mesmo mob (id+Address) foi
    /// lido há &lt;250ms COM o mesmo checkProximal. Re-lê se: mob novo, TTL expirou, ou o checkProximal
    /// mudou (perto↔longe — o proximal depende da distância, tem de reavaliar). Corta ~93% das leituras.
    /// </summary>
    private bool BuffsBlockCached(Entity entity, bool checkProximal)
    {
        var now = DateTime.UtcNow.Ticks;
        long addr; try { addr = (long)entity.Address; } catch { return BuffsBlockTarget(entity, checkProximal); }
        var id = entity.Id;

        if (_buffCache.TryGetValue(id, out var c)
            && c.Address == addr
            && c.CheckedProximal == checkProximal
            && (now - c.ReadAtTicks) / TimeSpan.TicksPerMillisecond < BuffCacheTtlMs)
            return c.Blocked; // dentro do TTL, mesmo mob, mesma condição → reusa.

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

                // Exceções: buffs com "immune" no nome que não dão imunidade ao próprio mob (ex: aura de
                // Abyss que protege os fodder, não o raro). Saltam o bloqueio por fragmento.
                var isException = false;
                foreach (var ex in InvulnBuffExceptions)
                    if (name.Contains(ex, StringComparison.OrdinalIgnoreCase)) { isException = true; break; }
                if (isException) continue;

                foreach (var frag in InvulnBuffFragments)
                    if (name.Contains(frag, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Lê os buffs do mob UMA vez e devolve true se tem o Proximal Tangibility. Independente da distância
    /// (o "imune agora" calcula-se à parte). Só para o HUD. Tolerante a falhas (sem buffs → false).
    /// </summary>
    private static bool HasProximalBuff(Entity entity)
    {
        try
        {
            if (!entity.TryGetComponent<Buffs>(out var buffs) || buffs?.BuffsList == null)
                return false;

            foreach (var b in buffs.BuffsList)
            {
                var name = b?.Name;
                if (!string.IsNullOrEmpty(name)
                    && name.Contains("proximal_intangibility", StringComparison.OrdinalIgnoreCase))
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
