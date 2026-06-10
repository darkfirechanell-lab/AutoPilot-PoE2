using System;
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
/// SUB-FASES:
///   3.1 — motor base: ordena, avalia, TAP + cooldown. [FEITO]
///   3.2 (esta) — máquina de HOLD genérica: skills do tipo Hold seguram a tecla até a condição de
///                release (do SkillRule.ReleaseWhen) — Mark até buff no alvo, Salvo até charges baixarem,
///                Snipe até stage, Tornado/Barrage até ActorSkill confirmar, ou timeout. Reusa o padrão
///                exato do IceShot (uma máquina partilhada), mas parametrizada pelas regras.
///   3.3 — encadeamento temporal (AfterSkill/AfterSkillDelayMs) p/ combo Barrage→Snipe.
///   3.4 — UI.
/// </summary>
public sealed class GeneralRoutine : IRoutine
{
    public string Name => "Geral";

    private readonly CooldownTracker _cd = new();
    private List<SkillRule> _rules = new();
    private List<SkillRule> _ordered = new();

    // Estado de hold (uma máquina partilhada, como no IceShot).
    private SkillRule _holdRule;          // a regra que está a segurar (null = nenhuma).
    private Keys _holdKey;
    private long _holdStartTicks;
    private uint _holdTargetId;
    private int _holdSnapshotCharges;     // charges no início (p/ ReleaseWhen.PlayerChargesDrop).
    private bool _holdSawChannelStage;    // viu um stage de canal antes do release (p/ AnimationStage).

    public void SetRules(List<SkillRule> rules)
    {
        _rules = rules ?? new List<SkillRule>();

        // F1: atribui um RuleId ÚNICO a cada regra (SkillName#N por ocorrência da mesma skill). Distingue
        // 2+ regras da mesma skill no cooldown/hold sem colisão. 1 regra/skill → "Nome#0" (estável). A
        // tecla e o chaining (AfterSkill) continuam por SkillName.
        var seen = new Dictionary<string, int>();
        foreach (var r in _rules)
        {
            if (r == null) continue;
            var n = seen.TryGetValue(r.SkillName, out var c) ? c : 0;
            seen[r.SkillName] = n + 1;
            r.RuleId = $"{r.SkillName}#{n}";
        }

        _ordered = _rules.OrderByDescending(r => r.Priority).ToList();
    }

    public bool IsBusy => _holdRule != null;

    public string Debug { get; private set; } = "";

    public void Execute(RoutineContext ctx)
    {
        if (ctx == null) return;

        // 1. Se está a segurar uma skill, continua a máquina de hold (mesmo sem alvo, p/ soltar bem).
        if (_holdRule != null)
        {
            ContinueHold(ctx);
            return;
        }

        // 2. Avalia as skills por prioridade. A primeira que passa tudo, dispara.
        foreach (var rule in _ordered)
        {
            if (rule.UseType == SkillUseType.Persistent) continue; // Persistente: fora do âmbito (3.x+).

            if (!_cd.Ready(rule.EffectiveRuleId, rule.CooldownMs)) continue;
            if (!RuleEvaluator.Evaluate(ctx, rule)) continue;

            // 3.3: encadeamento temporal. Se a regra exige sair DEPOIS de outra skill (AfterSkill):
            var chain = ChainState(rule);
            if (chain == Chain.NotYetUsed) continue;       // a skill-âncora ainda não saiu → esta nem entra.
            if (chain == Chain.Waiting)
            {
                // A âncora saiu mas o delay ainda não passou. ESPERA — não dispara esta nem nada de menor
                // prioridade (senão um filler entrava no meio e estragava o combo). Pára o tick aqui.
                Debug = $"geral: aguarda {rule.AfterSkillDelayMs}ms apos {rule.AfterSkill} p/ {rule.SkillName}";
                return;
            }

            var slot = ctx.Find(rule.SkillName);
            if (slot == null || !slot.IsReady) continue;
            var key = slot.Key.Value.Key;
            if (key == Keys.None) continue;

            // C1: skills de dano só com cursor no alvo. Buff não precisa (não mira mob).
            if (rule.UseType != SkillUseType.Buff && !ctx.CanHit) continue;

            if (rule.UseType == SkillUseType.Hold)
            {
                BeginHold(ctx, rule, key);
                Debug = $"geral: HOLD {rule.SkillName} (p{rule.Priority})";
                return;
            }

            // Tap (e Buff tratado como tap simples por agora).
            ctx.Skills.Tap(key, slot.TapHoldMs.Value);
            MarkUsed(rule);
            Debug = $"geral: TAP {rule.SkillName} (p{rule.Priority})";
            return;
        }

        Debug = "geral: (nada)";
    }

    private enum Chain { None, NotYetUsed, Waiting, Ready }

    /// <summary>
    /// Estado do encadeamento temporal de uma regra (AfterSkill/AfterSkillDelayMs):
    ///  • None       — sem encadeamento (regra livre).
    ///  • NotYetUsed — a âncora ainda não saiu, OU JÁ FOI CONSUMIDA por esta skill (1 disparo por âncora).
    ///  • Waiting    — a âncora saiu mas ainda não passaram AfterSkillDelayMs → ESPERAR (não estragar o combo).
    ///  • Ready      — a âncora saiu, o delay passou, e esta skill ainda não a consumiu → pode disparar.
    ///
    /// "1 disparo por âncora" (corrige o bug do 2º Snipe órfão): a skill só dispara se a âncora saiu
    /// MAIS RECENTEMENTE do que a própria skill. Depois de a skill disparar, sinceSkill < sinceAnchor,
    /// logo não repete até a âncora (Barrage) sair de novo.
    /// </summary>
    private Chain ChainState(SkillRule rule)
    {
        if (string.IsNullOrEmpty(rule.AfterSkill)) return Chain.None;

        // Âncora por NOME (o Snipe espera "qualquer Barrage", não uma regra específica). O MarkUsed marca
        // o SkillName além do RuleId, por isso o SinceMs(nome) encontra a âncora venha de que regra vier.
        var sinceAnchor = _cd.SinceMs(rule.AfterSkill);
        if (sinceAnchor > 99999) return Chain.NotYetUsed;              // âncora nunca usada.
        if (sinceAnchor < rule.AfterSkillDelayMs) return Chain.Waiting; // ainda no commit.

        // 1 disparo por âncora: esta REGRA específica (RuleId) já saiu depois da âncora atual? Se sim,
        // consumida. Por RuleId (não nome) → 2 regras da mesma skill não se "consomem" uma à outra.
        var sinceSelf = _cd.SinceMs(rule.EffectiveRuleId);
        if (sinceSelf <= sinceAnchor) return Chain.NotYetUsed;          // já consumiu esta âncora.

        return Chain.Ready;
    }

    /// <summary>Marca a regra como usada: o RuleId (cooldown desta regra) E o SkillName (âncora do chaining,
    /// p/ um AfterSkill que refere o nome encontrar o uso venha de que regra da skill vier).</summary>
    private void MarkUsed(SkillRule rule)
    {
        _cd.Mark(rule.EffectiveRuleId);
        if (rule.EffectiveRuleId != rule.SkillName) _cd.Mark(rule.SkillName);
    }

    // ── Máquina de HOLD genérica (parametrizada pela regra) ─────────────────────────────────

    private void BeginHold(RoutineContext ctx, SkillRule rule, Keys key)
    {
        _holdRule = rule;
        _holdKey = key;
        _holdTargetId = ctx.Target?.Entity?.Id ?? 0;
        _holdStartTicks = DateTime.UtcNow.Ticks;
        _holdSawChannelStage = false;
        _holdSnapshotCharges = string.IsNullOrEmpty(rule.ReleaseBuffName)
            ? 0 : BuffReader.Charges(ctx.Game?.Player, rule.ReleaseBuffName);
        ctx.Skills.Channel(key); // KeyDown contínuo (mantém premida).
    }

    private void ContinueHold(RoutineContext ctx)
    {
        var rule = _holdRule;
        var elapsed = (DateTime.UtcNow.Ticks - _holdStartTicks) / TimeSpan.TicksPerMillisecond;
        var target = ctx.Target?.Entity;
        var targetGone = target == null || !IsAlive(target) || target.Id != _holdTargetId;

        var release = false;

        switch (rule.ReleaseWhen)
        {
            case HoldReleaseCondition.TargetBuffAppears:
                // Confirma quando o buff aparece no ALVO (ex.: Mark → freezing_mark). targetGone também solta.
                release = (target != null && BuffReader.Has(target, rule.ReleaseBuffName)) || targetGone;
                break;

            case HoldReleaseCondition.PlayerBuffAppears:
                // Buff no PLAYER (ex.: Ice-Tipped). NÃO depende do alvo (é buff próprio).
                release = BuffReader.Has(ctx.Game?.Player, rule.ReleaseBuffName);
                break;

            case HoldReleaseCondition.PlayerChargesDrop:
                // Charges baixaram = a skill disparou (ex.: Salvo → seals). targetGone também solta.
                var ch = BuffReader.Charges(ctx.Game?.Player, rule.ReleaseBuffName);
                release = (ch >= 0 && ch < _holdSnapshotCharges) || targetGone;
                break;

            case HoldReleaseCondition.SkillUsed:
                // ActorSkill confirma uso/cooldown (ex.: Tornado, Barrage). targetGone também solta.
                var s = ctx.Find(rule.SkillName);
                release = (s != null && (s.IsUsing || s.IsOnCooldown)) || targetGone;
                break;

            case HoldReleaseCondition.AnimationStage:
                // Stage de animação (ex.: Snipe = 21). Precisa de ter visto um stage de canal antes.
                if (targetGone) { release = true; break; }
                var stage = ctx.Animation.Stage;
                if (stage >= 0 && stage < rule.ReleaseAnimationStage) _holdSawChannelStage = true;
                release = elapsed >= 200 && _holdSawChannelStage && stage >= rule.ReleaseAnimationStage;
                break;

            case HoldReleaseCondition.Timeout:
            default:
                release = targetGone; // só o timeout (abaixo) ou alvo perdido.
                break;
        }

        // Timeout de segurança — sempre presente, solta à força.
        if (elapsed >= rule.ReleaseTimeoutMs) release = true;

        if (release)
        {
            ctx.Skills.Release();        // KeyUp.
            MarkUsed(rule);              // marca cooldown (RuleId) + âncora (SkillName) só ao soltar.
            Debug = $"geral: RELEASE {rule.SkillName}";
            _holdRule = null;
            _holdKey = Keys.None;
            _holdTargetId = 0;
            _holdSawChannelStage = false;
        }
    }

    private static bool IsAlive(ExileCore2.PoEMemory.MemoryObjects.Entity e)
    {
        try { return e is { IsValid: true, IsAlive: true, IsDead: false }; }
        catch { return false; }
    }

    public void Reset()
    {
        if (_holdRule != null) { /* a tecla é largada pelo ReleaseAll do plugin */ }
        _holdRule = null;
        _holdKey = Keys.None;
        _holdTargetId = 0;
        _holdSawChannelStage = false;
        _cd.Clear();
    }
}
