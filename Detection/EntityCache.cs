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
        _monsters.Clear();

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

        foreach (var entity in source)
        {
            if (!IsValidTarget(entity)) continue;

            _monsters.Add(new TrackedEntity
            {
                Entity = entity,
                Distance = Vector2.Distance(_playerGridPos, entity.GridPos),
                Rarity = ReadRarity(entity),
            });
        }
    }

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

    // Buffs/debuffs que tornam um alvo imune — bosses que clonam (ex.: o que triplica) deixam
    // os clones invulneráveis assim. Sem isto, o aim desperdiçava-se nos clones em vez do boss
    // real que dá para matar. Cobrimos várias variantes de nome porque o jogo não é consistente.
    private static readonly string[] InvulnBuffFragments =
    {
        "invulnerable", "cannot_die", "cannot_be_damaged", "immune", "damage_immunity", "untargetable",
    };

    /// <summary>
    /// Um alvo só conta se for um monstro hostil, vivo, visível e ALVEJÁVEL E DANIFICÁVEL.
    /// Filtra invulnerabilidade por três vias (cobre clones de boss): a stat CannotBeDamaged,
    /// a flag IsTargetable, e buffs de imunidade. Atacar um alvo imune é desperdício de DPS.
    /// </summary>
    private static bool IsValidTarget(Entity entity)
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
        if (stats != null && stats.TryGetValue(GameStat.CannotBeDamaged, out var v) && v == 1)
            return false;

        // Buffs de imunidade (apanha clones de boss que ficam imunes sem mexer na stat).
        if (HasInvulnBuff(entity))
            return false;

        return true;
    }

    private static bool HasInvulnBuff(Entity entity)
    {
        try
        {
            if (!entity.TryGetComponent<Buffs>(out var buffs) || buffs?.BuffsList == null)
                return false;

            foreach (var b in buffs.BuffsList)
            {
                var name = b?.Name;
                if (string.IsNullOrEmpty(name)) continue;
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
