using System.Collections.Generic;

namespace AutoPilot.Combat.General;

/// <summary>
/// Preset de regras da build de MONGE (leveling) no modelo genérico (SkillRule). Versão BASE — montada
/// aos poucos com o utilizador. A mecânica do Bell (Holo Focus → Culling gera power charges) ainda NÃO
/// está modelada (precisa de confirmar o buff-tracker do Bell no jogo); por agora o ciclo é:
/// sustain (Rend) → manter Charged Staff → gastar charges (Falling Thunder com ≥ charges) → filler.
///
/// Nomes de skill confirmados pela StaffRoutine (mesma build de cajado). Buffs: 'rend' (debuff no alvo),
/// 'power_charge' (charges no player). Ver BUILD_MONGE_LEVELING_PLANO.md.
/// </summary>
public static class MonkPreset
{
    // Nomes de memória (confirmados na StaffRoutine).
    private const string FALLING_THUNDER = "FallingThunderPlayer";
    private const string KILLING_PALM = "KillingPalmPlayer";   // o "Culling/Killing Palm" (gera charges no Bell)
    private const string CHARGED_STAFF = "ChargedStaffPlayer";
    private const string REND = "WyvernRendPlayer";
    private const string TEMPEST_BELL = "TempestBellPlayer";
    private const string HOLLOW_FORM = "HollowFormPlayer";

    // Buffs/debuffs.
    private const string REND_ON_ENEMY = "rend";       // debuff que o Rend deixa no alvo
    private const string POWER_CHARGE = "power_charge"; // charges no player (ChargeBuff)
    private const string CHARGED_STAFF_BUFF = "charged_staff"; // buff de manter o Charged Staff (a CONFIRMAR)

    /// <summary>Constrói as regras do Monge no modelo genérico (ordem por prioridade decrescente).</summary>
    public static List<SkillRule> Build()
    {
        return new List<SkillRule>
        {
            // Charged Staff: MANTER o buff (coração da build). Prioridade alta — reaplica se o buff cai.
            // (Nome do buff 'charged_staff' a CONFIRMAR no jogo; se diferir, ajustar PlayerMissingBuff.)
            new()
            {
                SkillName = CHARGED_STAFF, UseType = SkillUseType.Tap, Priority = 100,
                PlayerMissingBuff = CHARGED_STAFF_BUFF, CooldownMs = 500,
            },
            // Rend: sustain. Reaplica se o alvo não tem o debuff 'rend'. Rare+ (não desperdiça em lixo).
            new()
            {
                SkillName = REND, UseType = SkillUseType.Tap, Priority = 90,
                MinRarity = TargetRarity.RarePlus, TargetMissingBuff = REND_ON_ENEMY, CooldownMs = 1000,
            },
            // Killing/Culling Palm: gera power charges. POR AGORA mira o mob (o Bell vem depois). Só usa
            // enquanto NÃO tens as charges no máximo — para gerar antes de gastar. ChargeMin baixo (gera
            // até encher). NOTA: a mecânica do Bell (charges só no Bell) entra numa fase futura.
            new()
            {
                SkillName = KILLING_PALM, UseType = SkillUseType.Tap, Priority = 80,
                CooldownMs = 400,
            },
            // Falling Thunder: GASTA as charges. Só com >= 3 power charges (consumo de culminação).
            new()
            {
                SkillName = FALLING_THUNDER, UseType = SkillUseType.Tap, Priority = 70,
                MinRarity = TargetRarity.MagicPlus,
                ChargeBuff = POWER_CHARGE, ChargeMin = 3, CooldownMs = 300,
            },
            // Tempest Bell: abertura/burst em Rare+ (amplifica). Cooldown longo (não spam).
            new()
            {
                SkillName = TEMPEST_BELL, UseType = SkillUseType.Tap, Priority = 60,
                MinRarity = TargetRarity.RarePlus, CooldownMs = 4000,
            },
            // Hollow Form: burst de boss (abertura). Só Unique.
            new()
            {
                SkillName = HOLLOW_FORM, UseType = SkillUseType.Tap, Priority = 50,
                MinRarity = TargetRarity.UniqueOnly, CooldownMs = 8000,
            },
        };
    }
}
