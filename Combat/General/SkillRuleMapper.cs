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

            // Regra 1 — a config principal do slot.
            rules.Add(new SkillRule
            {
                SkillName = s.Name,
                UseType = ParseUseType(s.UseType.Value),
                Priority = s.Priority.Value,
                CooldownMs = s.CooldownMs.Value,
                CommitMs = s.CommitMs.Value,
                PerTargetCooldownMs = s.PerTargetCooldownMs.Value,
                AttackInPlace = s.AttackInPlace.Value,

                MinRarity = ParseRarity(s.MinRarity.Value),
                MinHardness = ParseHardness(s.MinHardness.Value),
                MaxHardness = ParseHardness(s.MaxHardness.Value),
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

            // F2: Regra 2 — 2ª regra da mesma skill (momento diferente). Só os campos [Regra 2] mudam;
            // o resto (release, distância, etc.) herda da regra 1 para não obrigar a reconfigurar tudo.
            if (s.HasExtraRule.Value)
            {
                rules.Add(new SkillRule
                {
                    SkillName = s.Name,
                    UseType = ParseUseType(s.Extra_UseType.Value),
                    Priority = s.Extra_Priority.Value,
                    CooldownMs = s.Extra_CooldownMs.Value,
                    CommitMs = s.CommitMs.Value, // commit é da skill (animação), partilhado pelas 2 regras.
                    PerTargetCooldownMs = s.PerTargetCooldownMs.Value, // idem (é da skill).
                    AttackInPlace = s.AttackInPlace.Value,

                    MinRarity = ParseRarity(s.Extra_MinRarity.Value),
                    MinHardness = ParseHardness(s.Extra_MinHardness.Value),
                    MaxHardness = ParseHardness(s.Extra_MaxHardness.Value),
                    IgnoreRangeForUnique = s.IgnoreRangeForUnique.Value,
                    MinDistance = s.MinDistance.Value,
                    MaxDistance = s.MaxDistance.Value,
                    TargetHpMinPercent = s.TargetHpMin.Value,
                    TargetHpMaxPercent = s.TargetHpMax.Value,
                    CloseTargets = s.CloseTargets.Value,
                    CloseTargetsRange = s.CloseTargetsRange.Value,

                    GroundEntityPath = s.GroundEntityPath.Value?.Trim() ?? "",
                    SkipIfGroundActive = s.SkipIfGroundActive.Value,

                    TargetHasBuff = s.Extra_TargetHasBuff.Value?.Trim() ?? "",
                    TargetMissingBuff = s.Extra_TargetMissingBuff.Value?.Trim() ?? "",
                    PlayerHasBuff = s.PlayerHasBuff.Value?.Trim() ?? "",
                    PlayerMissingBuff = s.PlayerMissingBuff.Value?.Trim() ?? "",
                    BossIgnoresPlayerMissingBuff = s.BossIgnoresPlayerMissingBuff.Value,
                    ChargeBuff = s.ChargeBuff.Value?.Trim() ?? "",
                    ChargeMin = s.ChargeMin.Value,

                    AfterSkill = s.Extra_AfterSkill.Value?.Trim() ?? "",
                    AfterSkillDelayMs = s.Extra_AfterSkillDelayMs.Value,

                    ReleaseWhen = ParseRelease(s.ReleaseWhen.Value),
                    ReleaseBuffName = s.ReleaseBuffName.Value?.Trim() ?? "",
                    ReleaseAnimationStage = s.ReleaseAnimationStage.Value,
                    ReleaseTimeoutMs = s.ReleaseTimeoutMs.Value,
                });
            }
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
