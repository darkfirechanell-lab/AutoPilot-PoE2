using System;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Combat.General;

/// <summary>
/// FASE 1 da Routine Geral: avaliador PURO de uma <see cref="SkillRule"/>.
///
/// Decide se uma skill PODE disparar este tick, testando todas as condições da regra em AND. Cada
/// condição "desligada" (default que não filtra) é saltada → uma regra vazia deixa sempre passar.
///
/// É leitura/decisão, SEM efeitos: não preme teclas, não muda estado. Tolerante a falhas (qualquer
/// leitura de memória que rebente assume-se como "não bloqueia", para nunca travar o combate por um
/// erro de leitura — exceto onde bloquear é o mais seguro, documentado caso a caso).
///
/// Reusa o <see cref="BuffReader"/> (cache por-tick) para os gates de buff/charges. NÃO trata o
/// encadeamento temporal (AfterSkill) nem o cooldown — esses dependem de estado do motor (Fase 3),
/// não do snapshot atual; o motor chama-os à parte.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>
    /// A skill pode disparar agora, segundo as condições de SNAPSHOT da regra (raridade, distância,
    /// buffs, charges, hp, close_targets)? Não inclui cooldown nem encadeamento temporal (Fase 3).
    /// </summary>
    public static bool Evaluate(RoutineContext ctx, SkillRule rule, out string reason)
    {
        reason = "ok";
        if (rule == null) { reason = "rule=null"; return false; }

        var target = ctx?.Target?.Entity;
        var player = ctx?.Game?.Player;

        // ── Tipo BUFF: não precisa de alvo. Só checa gates de player (buffs/charges). ──
        if (rule.UseType == SkillUseType.Buff)
            return EvaluatePlayerGates(player, rule, false, out reason);

        // ── Tipos que precisam de alvo (Tap/Hold/Persistent contra mobs) ──
        // Persistent pode correr sem alvo (premido em movimento) — mas isso é decisão do motor;
        // aqui, se não há alvo e a regra tem gates de alvo, não passa.
        if (target == null)
        {
            // Sem alvo: só passa se a regra não tem NENHUM gate de alvo (ex.: persistente puro).
            if (HasAnyTargetGate(rule)) { reason = "sem alvo"; return false; }
            return EvaluatePlayerGates(player, rule, false, out reason);
        }

        // Raridade mínima do alvo.
        if (!RarityOk(target, rule.MinRarity)) { reason = $"raridade<{rule.MinRarity}"; return false; }

        // Distância (com isenção de Unique se configurada).
        var dist = ctx.Target.Distance;
        var isUnique = SafeRarity(target) == MonsterRarity.Unique;
        if (!(isUnique && rule.IgnoreRangeForUnique))
        {
            if (rule.MinDistance > 0f && dist < rule.MinDistance) { reason = "perto demais"; return false; }
            if (rule.MaxDistance > 0f && dist > rule.MaxDistance) { reason = "longe demais"; return false; }
        }

        // HP% do alvo (banda). Default 0..1 = não filtra. Leitura ilegível NÃO bloqueia.
        if (rule.TargetHpMinPercent > 0f || rule.TargetHpMaxPercent < 1f)
        {
            var hp = TargetHpPercent(target);
            if (hp >= 0f && (hp < rule.TargetHpMinPercent || hp > rule.TargetHpMaxPercent))
            { reason = "hp fora da banda"; return false; }
        }

        // close_targets: nº de mobs perto do alvo.
        if (rule.CloseTargets > 0 && ctx.Entities != null)
        {
            var near = ctx.Entities.CountWithin(target.GridPos, rule.CloseTargetsRange);
            if (near < rule.CloseTargets) { reason = "poucos mobs perto"; return false; }
        }

        // Gates de buff do ALVO.
        if (!string.IsNullOrEmpty(rule.TargetHasBuff) && !BuffReader.Has(target, rule.TargetHasBuff))
        { reason = $"alvo sem {rule.TargetHasBuff}"; return false; }
        if (!string.IsNullOrEmpty(rule.TargetMissingBuff) && BuffReader.Has(target, rule.TargetMissingBuff))
        { reason = $"alvo tem {rule.TargetMissingBuff}"; return false; }

        // Gates de player (buffs/charges). Passa se o alvo é Unique (p/ BossIgnoresPlayerMissingBuff).
        return EvaluatePlayerGates(player, rule, isUnique, out reason);
    }

    /// <summary>Sobrecarga sem o out reason (conveniência).</summary>
    public static bool Evaluate(RoutineContext ctx, SkillRule rule) => Evaluate(ctx, rule, out _);

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static bool EvaluatePlayerGates(Entity player, SkillRule rule, bool targetIsUnique, out string reason)
    {
        reason = "ok";
        if (!string.IsNullOrEmpty(rule.PlayerHasBuff) && !BuffReader.Has(player, rule.PlayerHasBuff))
        { reason = $"player sem {rule.PlayerHasBuff}"; return false; }

        // PlayerMissingBuff: a Mark ignora este gate no boss (remarca mesmo com o buff de dano ativo).
        var skipMissing = rule.BossIgnoresPlayerMissingBuff && targetIsUnique;
        if (!skipMissing && !string.IsNullOrEmpty(rule.PlayerMissingBuff) && BuffReader.Has(player, rule.PlayerMissingBuff))
        { reason = $"player tem {rule.PlayerMissingBuff}"; return false; }

        if (!string.IsNullOrEmpty(rule.ChargeBuff))
        {
            var charges = BuffReader.Charges(player, rule.ChargeBuff);
            // -1 (ilegível): bloqueia se a regra EXIGE um mínimo — sem baseline fiável não arrancamos.
            if (charges < rule.ChargeMin) { reason = $"{rule.ChargeBuff}<{rule.ChargeMin}"; return false; }
        }
        return true;
    }

    private static bool HasAnyTargetGate(SkillRule r) =>
        r.MinRarity != TargetRarity.Any
        || r.MinDistance > 0f || r.MaxDistance > 0f
        || r.TargetHpMinPercent > 0f || r.TargetHpMaxPercent < 1f
        || r.CloseTargets > 0
        || !string.IsNullOrEmpty(r.TargetHasBuff) || !string.IsNullOrEmpty(r.TargetMissingBuff);

    private static bool RarityOk(Entity target, TargetRarity min)
    {
        if (min == TargetRarity.Any) return true;
        var r = SafeRarity(target);
        return min switch
        {
            TargetRarity.MagicPlus => r is MonsterRarity.Magic or MonsterRarity.Rare or MonsterRarity.Unique,
            TargetRarity.RarePlus => r is MonsterRarity.Rare or MonsterRarity.Unique,
            TargetRarity.UniqueOnly => r == MonsterRarity.Unique,
            TargetRarity.NormalOnly => r == MonsterRarity.White,
            _ => true,
        };
    }

    private static MonsterRarity SafeRarity(Entity e)
    {
        try { return e.GetComponent<ObjectMagicProperties>()?.Rarity ?? MonsterRarity.White; }
        catch { return MonsterRarity.White; }
    }

    /// <summary>HP% do alvo (0..1), ou -1 se ilegível. Só HP (não soma ES) para "culling" ser preciso.</summary>
    private static float TargetHpPercent(Entity entity)
    {
        try
        {
            var life = entity.GetComponent<Life>();
            if (life == null) return -1f;
            return life.HPPercentage;
        }
        catch { return -1f; }
    }
}
