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

            // Recolhe TODAS as regras do preset para esta skill (algumas têm 2: ex. Barrage Medium +
            // Barrage Tank/frozen). Ordena por prioridade decrescente.
            var forSkill = new List<SkillRule>();
            foreach (var r in preset)
                if (r.SkillName == slot.Name) forSkill.Add(r);
            if (forSkill.Count == 0) continue;
            forSkill.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            // Regra 1 = a de maior prioridade → config principal do slot.
            WriteRuleToSlot(forSkill[0], slot);

            // Regra 2 (se houver) → campos [Regra 2] + liga o toggle. F2: a UI passa a refletir as 2
            // regras (antes a 2ª perdia-se). Regras 3+ (raras) ficam só no preset embutido por agora.
            if (forSkill.Count >= 2)
            {
                WriteExtraRuleToSlot(forSkill[1], slot);
                slot.HasExtraRule.Value = true;
            }
            else
            {
                slot.HasExtraRule.Value = false;
            }
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
        s.MinHardness.Value = HardnessName(r.MinHardness);
        s.MaxHardness.Value = HardnessName(r.MaxHardness);
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

    private static string HardnessName(TargetHardness h) => h switch
    {
        TargetHardness.Medium => "Medium",
        TargetHardness.Tank => "Tank",
        _ => "Easy",
    };

    /// <summary>Escreve a 2ª regra de uma skill nos campos [Regra 2] do slot (F2).</summary>
    private static void WriteExtraRuleToSlot(SkillRule r, SkillSlot s)
    {
        s.Extra_UseType.Value = UseTypeName(r.UseType);
        s.Extra_Priority.Value = r.Priority;
        s.Extra_CooldownMs.Value = r.CooldownMs;
        s.Extra_MinRarity.Value = RarityName(r.MinRarity);
        s.Extra_MinHardness.Value = HardnessName(r.MinHardness);
        s.Extra_MaxHardness.Value = HardnessName(r.MaxHardness);
        s.Extra_TargetHasBuff.Value = r.TargetHasBuff;
        s.Extra_TargetMissingBuff.Value = r.TargetMissingBuff;
        s.Extra_AfterSkill.Value = r.AfterSkill;
        s.Extra_AfterSkillDelayMs.Value = r.AfterSkillDelayMs;
    }

    private static string UseTypeName(SkillUseType t) => t switch
    {
        SkillUseType.Hold => "Hold",
        SkillUseType.Buff => "Buff",
        SkillUseType.Persistent => "Persistent",
        _ => "Tap",
    };

    private static string RarityName(TargetRarity r) => r switch
    {
        TargetRarity.MagicPlus => "Magic+",
        TargetRarity.RarePlus => "Rare+",
        TargetRarity.UniqueOnly => "Só Unique",
        TargetRarity.NormalOnly => "Só Normal",
        _ => "Qualquer",
    };
}
