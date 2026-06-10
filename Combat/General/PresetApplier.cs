using System.Collections.Generic;
using AutoPilot.Combat;

namespace AutoPilot.Combat.General;

/// <summary>
/// Fase 4: aplica um preset (lista de <see cref="SkillRule"/>) aos <see cref="SkillSlot"/> da UI,
/// preenchendo os campos "[Geral]" automaticamente. É o inverso do <see cref="SkillRuleMapper"/>:
/// em vez de ler a UI, ESCREVE-A. Evita o utilizador ter de configurar 23 campos por skill à mão.
///
/// Liga cada regra ao slot pelo nome de memória (SkillName == SkillSlot.Name). Skills do slot que não
/// estão no preset ficam intocadas. Se um nome do preset não existe nos slots detetados, é ignorado.
/// </summary>
public static class PresetApplier
{
    public static int Apply(List<SkillRule> preset, IEnumerable<SkillSlot> slots)
    {
        if (preset == null || slots == null) return 0;

        var applied = 0;
        foreach (var slot in slots)
        {
            if (slot == null || string.IsNullOrEmpty(slot.Name)) continue;

            // Encontra a regra do preset para este slot (1ª que bate o nome). Algumas builds têm 2
            // regras para a mesma skill (ex.: Mark boss vs não-boss) — aqui aplicamos a de MAIOR
            // prioridade ao slot (o motor usa o preset embutido para a 2ª variante; ver nota).
            SkillRule best = null;
            foreach (var r in preset)
                if (r.SkillName == slot.Name && (best == null || r.Priority > best.Priority))
                    best = r;
            if (best == null) continue;

            WriteRuleToSlot(best, slot);
            applied++;
        }
        return applied;
    }

    private static void WriteRuleToSlot(SkillRule r, SkillSlot s)
    {
        s.UseType.Value = r.UseType switch
        {
            SkillUseType.Hold => "Hold",
            SkillUseType.Buff => "Buff",
            SkillUseType.Persistent => "Persistent",
            _ => "Tap",
        };
        s.Priority.Value = r.Priority;
        s.CooldownMs.Value = r.CooldownMs;
        s.AttackInPlace.Value = r.AttackInPlace;

        s.MinRarity.Value = r.MinRarity switch
        {
            TargetRarity.MagicPlus => "Magic+",
            TargetRarity.RarePlus => "Rare+",
            TargetRarity.UniqueOnly => "Só Unique",
            TargetRarity.NormalOnly => "Só Normal",
            _ => "Qualquer",
        };
        s.MinHardness.Value = r.MinHardness switch
        {
            TargetHardness.Medium => "Medium",
            TargetHardness.Tank => "Tank",
            _ => "Easy",
        };
        s.IgnoreRangeForUnique.Value = r.IgnoreRangeForUnique;
        s.MinDistance.Value = r.MinDistance;
        s.MaxDistance.Value = r.MaxDistance;
        s.TargetHpMin.Value = r.TargetHpMinPercent;
        s.TargetHpMax.Value = r.TargetHpMaxPercent;
        s.CloseTargets.Value = r.CloseTargets;
        s.CloseTargetsRange.Value = r.CloseTargetsRange;

        s.GroundEntityPath.Value = r.GroundEntityPath;
        s.SkipIfGroundActive.Value = r.SkipIfGroundActive;
        s.TargetHasBuff.Value = r.TargetHasBuff;
        s.TargetMissingBuff.Value = r.TargetMissingBuff;
        s.PlayerHasBuff.Value = r.PlayerHasBuff;
        s.PlayerMissingBuff.Value = r.PlayerMissingBuff;
        s.BossIgnoresPlayerMissingBuff.Value = r.BossIgnoresPlayerMissingBuff;
        s.ChargeBuff.Value = r.ChargeBuff;
        s.ChargeMin.Value = r.ChargeMin;

        s.AfterSkill.Value = r.AfterSkill;
        s.AfterSkillDelayMs.Value = r.AfterSkillDelayMs;

        s.ReleaseWhen.Value = r.ReleaseWhen switch
        {
            HoldReleaseCondition.TargetBuffAppears => "Buff no alvo",
            HoldReleaseCondition.PlayerBuffAppears => "Buff no player",
            HoldReleaseCondition.PlayerChargesDrop => "Charges baixam",
            HoldReleaseCondition.SkillUsed => "Skill usada",
            HoldReleaseCondition.AnimationStage => "Stage animação",
            _ => "Timeout",
        };
        s.ReleaseBuffName.Value = r.ReleaseBuffName;
        s.ReleaseAnimationStage.Value = r.ReleaseAnimationStage;
        s.ReleaseTimeoutMs.Value = r.ReleaseTimeoutMs;
    }
}
