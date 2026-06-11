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
    private const string BARRAGE_BUFF = "empower_barrage_visual"; // buff que o Barrage dá (confirmado nos logs).

    /// <summary>Constrói as regras de gelo no modelo genérico (ordem por prioridade decrescente).</summary>
    public static List<SkillRule> Build()
    {
        return new List<SkillRule>
        {
            // Tornado Shot (PoE2): dura 15s, MULTIPLICADOR DE PROJÉTEIS (Ice Shot/Snipe ATRAVÉS dele
            // cospem 3 cópias). Lógica = "manter 1 tornado ATIVO". MODELO DO AutoMyAim (LightningRodTracker):
            // controla o uptime por TEMPO, não por deteção da entidade no chão (que falhava — o tornado cai
            // perto da mira, fora do raio do alvo). CooldownMs = 14000 (~duração do tornado, re-lança ~1s
            // antes de expirar) -> não relança enquanto o tornado anterior está vivo. Sem gate de blind nem
            // de entidade. Prioridade ALTA (entra antes do combo p/ os projéteis passarem por ele).
            new()
            {
                SkillName = TORNADO, UseType = SkillUseType.Hold, Priority = 100,
                MinRarity = TargetRarity.RarePlus, MinHardness = TargetHardness.Easy,
                CooldownMs = 14000,          // uptime por TEMPO (modelo AutoMyAim) — 1 tornado de cada vez.
                ReleaseWhen = HoldReleaseCondition.SkillUsed, ReleaseTimeoutMs = 500,
            },
            // Barrage = BUFF (não dano): um clique curto puxa o arco e dá o buff 'empower_barrage_visual'
            // por uns segundos, que empodera os tiros seguintes. Logo: TAP (clique único — não Hold, que
            // o prendia e não completava, n=0). CommitMs protege a animação curta de ser cortada pela
            // skill seguinte. PlayerMissingBuff = só re-clica quando o buff JÁ NÃO existe (não spamma).
            //
            // Regra A — Barrage no MEDIUM (só Medium, NÃO Tank): sai SEM frozen contra rares médios.
            new()
            {
                SkillName = BARRAGE, UseType = SkillUseType.Tap, Priority = 90,
                MinRarity = TargetRarity.RarePlus,
                MinHardness = TargetHardness.Medium, MaxHardness = TargetHardness.Medium,
                PlayerMissingBuff = BARRAGE_BUFF, CommitMs = 400,
            },
            // Regra B — Barrage no TANK/BOSS: SÓ com frozen (parte do combo). O empower alimenta o Snipe.
            new()
            {
                SkillName = BARRAGE, UseType = SkillUseType.Tap, Priority = 90,
                MinRarity = TargetRarity.RarePlus, MinHardness = TargetHardness.Tank,
                TargetHasBuff = FROZEN, PlayerMissingBuff = BARRAGE_BUFF, CommitMs = 400,
            },
            // Snipe: só no TANK (ou boss) e SÓ quando o alvo está FROZEN — é o burst do combo congelado
            // (Barrage → Snipe). Entra durante o empower do Barrage (AfterSkill + delay).
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
