using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using AutoPilot.Combat.General;

namespace AutoPilot.Combat.Routines;

/// <summary>
/// Motor de rotação GENÉRICO e configurável (Fase 3 do plano v2). Lê uma lista de <see cref="SkillRule"/>
/// (qualquer build), ordena por prioridade, e usa o <see cref="RuleEvaluator"/> (Fase 1, já validado)
/// para decidir que skill disparar. Substitui o switch fixo do IceShotRoutine por dados.
///
/// Corre LADO A LADO com o IceShot (dropdown "Geral"); o IceShot NÃO se toca. É opt-in.
///
/// CONSTRUÇÃO POR SUB-FASES:
///   3.1 (esta) — motor base: ordena por prioridade, avalia, dispara TAP + cooldown interno. SEM hold,
///                SEM encadeamento temporal ainda. Já reproduz a parte "tap" da rotação (ex.: Ice Shot).
///   3.2 — máquina de hold (Mark/Salvo/Snipe/etc. seguram até confirmar).
///   3.3 — encadeamento temporal (AfterSkill/AfterSkillDelayMs) para o combo Barrage→Snipe.
///   3.4 — UI.
///
/// As regras vêm de fora (Set via Rules). Por agora o plugin passa o preset de gelo; na Fase 4 a UI.
/// </summary>
public sealed class GeneralRoutine : IRoutine
{
    public string Name => "Geral";

    private readonly CooldownTracker _cd = new();
    private List<SkillRule> _rules = new();
    private List<SkillRule> _ordered = new(); // _rules ordenadas por prioridade desc (cache).

    /// <summary>Define/atualiza as regras (do preset ou da UI). Re-ordena por prioridade.</summary>
    public void SetRules(List<SkillRule> rules)
    {
        _rules = rules ?? new List<SkillRule>();
        _ordered = _rules.OrderByDescending(r => r.Priority).ToList();
    }

    // 3.1: sem holds ainda, por isso nunca está "busy". (Muda em 3.2.)
    public bool IsBusy => false;

    public string Debug { get; private set; } = "";

    public void Execute(RoutineContext ctx)
    {
        if (ctx == null) return;

        // Avalia as skills por ordem de prioridade. A primeira que passa TODAS as condições E o
        // cooldown interno E está pronta no jogo, dispara — e paramos (uma ação por tick, como o IceShot).
        foreach (var rule in _ordered)
        {
            // 3.1: nesta sub-fase só tratamos TAP. Hold/Buff/Persistent entram em 3.2+ (saltados por agora).
            if (rule.UseType != SkillUseType.Tap) continue;

            if (!_cd.Ready(rule.SkillName, rule.CooldownMs)) continue;

            // C1: skills de dano só com o cursor no alvo (o IceShot aplica isto ao Ice Shot/Barrage).
            // Por agora, como só temos TAP (filler de dano), respeitamos o CanHit.
            if (!ctx.CanHit) continue;

            if (!RuleEvaluator.Evaluate(ctx, rule)) continue;

            var slot = ctx.Find(rule.SkillName);
            if (slot == null || !slot.IsReady) continue;

            var key = slot.Key.Value.Key;
            if (key == Keys.None) continue;

            ctx.Skills.Tap(key, slot.TapHoldMs.Value);
            _cd.Mark(rule.SkillName);
            Debug = $"geral: TAP {rule.SkillName} (p{rule.Priority})";
            return;
        }

        Debug = "geral: (nada)";
    }

    public void Reset()
    {
        _cd.Clear();
    }
}
