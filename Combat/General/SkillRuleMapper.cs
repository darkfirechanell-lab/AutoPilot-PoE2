using System.Collections.Generic;
using AutoPilot.Combat;

namespace AutoPilot.Combat.General;

/// <summary>
/// Fase 3.4: converte os <see cref="SkillSlot"/> (configuração da UI) em <see cref="SkillRule"/> (o que
/// o motor genérico lê). Liga a interface ao motor — cada campo "[Geral]" do SkillSlot vira um campo
/// da regra. Só inclui slots ATIVOS e com nome/tecla válidos.
/// </summary>
public static class SkillRuleMapper
{
    public static List<SkillRule> FromSlots(IEnumerable<SkillSlot> slots)
    {
        var rules = new List<SkillRule>();
        if (slots == null) return rules;

        foreach (var s in slots)
        {
            if (s == null || !s.Enabled.Value || string.IsNullOrEmpty(s.Name)) continue;

            rules.Add(new SkillRule
            {
                SkillName = s.Name,
                UseType = ParseUseType(s.UseType.Value),
                Priority = s.Priority.Value,
                CooldownMs = s.CooldownMs.Value,
                AttackInPlace = s.AttackInPlace.Value,

                MinRarity = ParseRarity(s.MinRarity.Value),
                MinHardness = ParseHardness(s.MinHardness.Value),
                IgnoreRangeForUnique = s.IgnoreRangeForUnique.Value,
                MinDistance = s.MinDistance.Value,
                MaxDistance = s.MaxDistance.Value,
                TargetHpMinPercent = s.TargetHpMin.Value,
                TargetHpMaxPercent = s.TargetHpMax.Value,
                CloseTargets = s.CloseTargets.Value,
                CloseTargetsRange = s.CloseTargetsRange.Value,

                GroundEntityPath = s.GroundEntityPath.Value?.Trim() ?? "",
                SkipIfGroundActive = s.SkipIfGroundActive.Value,

                TargetHasBuff = s.TargetHasBuff.Value?.Trim() ?? "",
                TargetMissingBuff = s.TargetMissingBuff.Value?.Trim() ?? "",
                PlayerHasBuff = s.PlayerHasBuff.Value?.Trim() ?? "",
                PlayerMissingBuff = s.PlayerMissingBuff.Value?.Trim() ?? "",
                BossIgnoresPlayerMissingBuff = s.BossIgnoresPlayerMissingBuff.Value,
                ChargeBuff = s.ChargeBuff.Value?.Trim() ?? "",
                ChargeMin = s.ChargeMin.Value,

                AfterSkill = s.AfterSkill.Value?.Trim() ?? "",
                AfterSkillDelayMs = s.AfterSkillDelayMs.Value,

                ReleaseWhen = ParseRelease(s.ReleaseWhen.Value),
                ReleaseBuffName = s.ReleaseBuffName.Value?.Trim() ?? "",
                ReleaseAnimationStage = s.ReleaseAnimationStage.Value,
                ReleaseTimeoutMs = s.ReleaseTimeoutMs.Value,
            });
        }
        return rules;
    }

    private static SkillUseType ParseUseType(string v) => v switch
    {
        "Hold" => SkillUseType.Hold,
        "Buff" => SkillUseType.Buff,
        "Persistent" => SkillUseType.Persistent,
        _ => SkillUseType.Tap,
    };

    private static TargetRarity ParseRarity(string v) => v switch
    {
        "Magic+" => TargetRarity.MagicPlus,
        "Rare+" => TargetRarity.RarePlus,
        "Só Unique" => TargetRarity.UniqueOnly,
        "Só Normal" => TargetRarity.NormalOnly,
        _ => TargetRarity.Any,
    };

    private static TargetHardness ParseHardness(string v) => v switch
    {
        "Medium" => TargetHardness.Medium,
        "Tank" => TargetHardness.Tank,
        _ => TargetHardness.Easy,
    };

    private static HoldReleaseCondition ParseRelease(string v) => v switch
    {
        "Buff no alvo" => HoldReleaseCondition.TargetBuffAppears,
        "Buff no player" => HoldReleaseCondition.PlayerBuffAppears,
        "Charges baixam" => HoldReleaseCondition.PlayerChargesDrop,
        "Skill usada" => HoldReleaseCondition.SkillUsed,
        "Stage animação" => HoldReleaseCondition.AnimationStage,
        _ => HoldReleaseCondition.Timeout,
    };
}
