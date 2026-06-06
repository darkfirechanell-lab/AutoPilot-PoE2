using System;
using System.Collections.Generic;
using ExileCore2.PoEMemory.Components;
using AutoPilot.Detection;

namespace AutoPilot.Combat;

/// <summary>
/// Deteta PERIGO à volta do jogador para o sistema de Dodge (Kiting). Sem base de dados de skills: o
/// sinal de perigo é um monstro PERTO com Actor.Action == "UsingAbility" (a lançar uma skill/atacar).
/// Quanto mais perto e mais elites a atacar, maior o perigo. Ver memória danger-detection-reference.
///
/// É só leitura (não age). O DodgeController decide o que fazer com o nível de perigo.
/// </summary>
public sealed class DangerDetector
{
    /// <summary>Distância (grid) até à qual um mob a atacar conta como ameaça.</summary>
    public float DangerRange { get; set; } = 25f;

    /// <summary>
    /// M3: ModRules de mods PERIGOSOS (regex + multiplicador). O peso de cada usa-se como MULTIPLICADOR
    /// do score de um mob que JÁ ESTÁ A ATACAR — amplifica o evento, NUNCA cria perigo de um mob parado.
    /// Vazio/null = ignora (o dodge funciona só com o sinal "mob a atacar"). Ver MODS_PLANO.md M3.
    /// </summary>
    public IReadOnlyList<General.ModRule> DangerModRules { get; set; }

    /// <summary>Diagnóstico da última avaliação.</summary>
    public string Debug { get; private set; } = "";

    /// <summary>
    /// Avalia o perigo no snapshot atual. Devolve um "score": 0 = sem perigo; >0 = nº (ponderado) de
    /// mobs perto a atacar (elites pesam mais). O DodgeController compara com o seu limiar.
    /// </summary>
    public float Evaluate(EntityCache entities)
    {
        if (entities == null) { Debug = "danger: (sem entities)"; return 0f; }

        float score = 0f;
        int attacking = 0;
        int amplified = 0;
        foreach (var m in entities.Monsters)
        {
            if (m.Distance > DangerRange) continue;
            if (!IsAttacking(m.Entity)) continue; // SÓ mobs A AGIR contam — mob parado = 0, sempre.

            attacking++;
            // Elites/bosses a atacar perto = mais perigoso.
            var weight = m.Rarity switch
            {
                ExileCore2.Shared.Enums.MonsterRarity.Unique => 3f,
                ExileCore2.Shared.Enums.MonsterRarity.Rare => 2f,
                ExileCore2.Shared.Enums.MonsterRarity.Magic => 1.3f,
                _ => 1f,
            };
            // Mais perto = mais perigoso (escala simples: dobra o peso a metade do alcance).
            var proximity = 1f + Math.Max(0f, (DangerRange - m.Distance) / DangerRange);

            // M3: o MOD amplifica o perigo deste mob que JÁ está a atacar (multiplicador). Aplicado só
            // aqui dentro (depois do IsAttacking) — um mob volatile PARADO nunca chega a este ponto.
            var modMultiplier = DangerModMultiplier(m.Entity);
            if (modMultiplier > 1f) amplified++;

            score += weight * proximity * modMultiplier;
        }

        Debug = $"danger: score={score:F1} atacam={attacking} amplif={amplified} range={DangerRange:F0}";
        return score;
    }

    /// <summary>
    /// M3: maior multiplicador de perigo dos mods perigosos que o mob casa (1 = nenhum). Defensivo.
    /// É só multiplicador — não soma nada por si só; só amplifica um mob que JÁ está a atacar.
    /// </summary>
    private float DangerModMultiplier(ExileCore2.PoEMemory.MemoryObjects.Entity entity)
    {
        var rules = DangerModRules;
        if (rules == null || rules.Count == 0 || entity == null) return 1f;
        var mult = 1f;
        foreach (var r in rules)
            if (r != null && r.Weight > mult && r.Matches(entity)) mult = r.Weight;
        return mult;
    }

    /// <summary>Um monstro está a usar uma habilidade/atacar agora? (Actor.Action == UsingAbility).</summary>
    private static bool IsAttacking(ExileCore2.PoEMemory.MemoryObjects.Entity entity)
    {
        try
        {
            if (entity != null && entity.TryGetComponent<Actor>(out var actor) && actor != null)
            {
                var action = actor.Action.ToString();
                // "UsingAbility" cobre a maioria dos ataques/casts. Comparação por Contains para apanhar
                // variantes (ex.: flags combinadas que incluem o uso de habilidade).
                return action != null && action.IndexOf("UsingAbility", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { }
        return false;
    }
}
