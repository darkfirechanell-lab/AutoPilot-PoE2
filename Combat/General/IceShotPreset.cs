using System.Collections.Generic;

namespace AutoPilot.Combat.General;

/// <summary>
/// Preset de regras da build de GELO, exprimindo a rotação do IceShotRoutine no modelo genérico
/// (SkillRule). Na Fase 1 serve só para OBSERVAÇÃO/verificação (o RuleEvaluator decide com estas
/// regras em paralelo e logamos o que decidiria vs o que o IceShot faz). Na Fase 4 vira o preset
/// oficial que o motor genérico carrega.
///
/// Nomes de skill/buff iguais aos do IceShotRoutine (confirmados nos logs). Prioridades refletem a
/// ordem atual: combo frozen (Tornado→Barrage→Snipe) primeiro, depois Mark/Ice-Tipped/Salvo, filler.
/// </summary>
public static class IceShotPreset
{
    // Nomes de memória (iguais ao IceShotRoutine).
    private const string ICE_SHOT = "IceShotPlayer";
    private const string SNIPE = "SnipePlayer";
    private const string SALVO = "FreezingSalvoPlayer";
    private const string MARK = "FreezingMarkPlayer";
    private const string ICE_TIPPED = "IceTippedArrowsPlayer";
    private const string TORNADO = "TornadoShotPlayer";
    private const string BARRAGE = "BarragePlayer";

    // Buffs (iguais ao IceShotRoutine).
    private const string FROZEN = "frozen";
    private const string ICE_TIPPED_BUFF = "shearing_bolts";
    private const string MARK_ON_ENEMY = "freezing_mark";
    private const string MARK_PLAYER_BUFF = "freezing_mark_damage_buff";
    private const string SALVO_SEALS = "skill_seals";
    private const string BLINDED = "blinded"; // debuff do Tornado (uptime, não spam)

    /// <summary>Constrói as regras de gelo no modelo genérico (ordem por prioridade decrescente).</summary>
    public static List<SkillRule> Build()
    {
        return new List<SkillRule>
        {
            // Tornado: UMA só regra (cabe num slot de UI, como a Mark). Prioridade ALTA (100) para
            // entrar ANTES do Barrage no burst. Só Rare+. NÃO exige FROZEN: mantém o uptime do blind
            // tanto no combo congelado como fora dele (boss). TargetMissingBuff=blinded → não reaplica
            // enquanto o debuff está ativo; só refresca quando cai. Quando o alvo já tem blinded, esta
            // regra não dispara e o motor segue para o Barrage. Tornado → Barrage → Snipe.
            new()
            {
                SkillName = TORNADO, UseType = SkillUseType.Hold, Priority = 100,
                MinRarity = TargetRarity.RarePlus, MinHardness = TargetHardness.Easy,
                TargetMissingBuff = BLINDED,
                CooldownMs = 2000,
                ReleaseWhen = HoldReleaseCondition.SkillUsed, ReleaseTimeoutMs = 500,
            },
            // Combo frozen: Barrage → Snipe. Só em Rare+ e alvo FROZEN.
            new()
            {
                SkillName = BARRAGE, UseType = SkillUseType.Hold, Priority = 90,
                MinRarity = TargetRarity.RarePlus, MinHardness = TargetHardness.Medium,
                TargetHasBuff = FROZEN, CooldownMs = 1540,
                ReleaseWhen = HoldReleaseCondition.SkillUsed, ReleaseTimeoutMs = 600,
            },
            new()
            {
                SkillName = SNIPE, UseType = SkillUseType.Hold, Priority = 80,
                MinRarity = TargetRarity.RarePlus, MinHardness = TargetHardness.Tank,
                TargetHasBuff = FROZEN,
                AfterSkill = BARRAGE, AfterSkillDelayMs = 400, // commit: Snipe entra DURANTE o empower
                ReleaseWhen = HoldReleaseCondition.AnimationStage, ReleaseAnimationStage = 21,
                ReleaseTimeoutMs = 2000,
            },
            // Mark: UMA regra que funciona em TODOS os alvos (cabe num slot de UI). Marca se o alvo não
            // tem o debuff. Fora do boss não remarca enquanto tens o buff de dano (PlayerMissingBuff);
            // no BOSS ignora esse gate (BossIgnoresPlayerMissingBuff) e remarca sempre — reproduz a
            // rotação do IceShot. Hold até o debuff freezing_mark aparecer no alvo.
            new()
            {
                SkillName = MARK, UseType = SkillUseType.Hold, Priority = 60,
                TargetMissingBuff = MARK_ON_ENEMY,
                PlayerMissingBuff = MARK_PLAYER_BUFF, BossIgnoresPlayerMissingBuff = true,
                CooldownMs = 1000,
                ReleaseWhen = HoldReleaseCondition.TargetBuffAppears, ReleaseBuffName = MARK_ON_ENEMY,
                ReleaseTimeoutMs = 500,
            },
            // Ice-Tipped: hold até o buff shearing_bolts aparecer no player.
            new()
            {
                SkillName = ICE_TIPPED, UseType = SkillUseType.Hold, Priority = 50,
                PlayerMissingBuff = ICE_TIPPED_BUFF, CooldownMs = 1200,
                ReleaseWhen = HoldReleaseCondition.PlayerBuffAppears, ReleaseBuffName = ICE_TIPPED_BUFF,
                ReleaseTimeoutMs = 500,
            },
            // Salvo: hold até os seals baixarem. Player precisa de >= 10 seals.
            new()
            {
                SkillName = SALVO, UseType = SkillUseType.Hold, Priority = 40,
                ChargeBuff = SALVO_SEALS, ChargeMin = 10, CooldownMs = 1500,
                ReleaseWhen = HoldReleaseCondition.PlayerChargesDrop, ReleaseBuffName = SALVO_SEALS,
                ReleaseTimeoutMs = 1200,
            },
            // Ice Shot: filler, tap, sem condições. Prioridade mais baixa.
            new()
            {
                SkillName = ICE_SHOT, UseType = SkillUseType.Tap, Priority = 10, CooldownMs = 50,
            },
        };
    }
}
