using System;
using System.Windows.Forms;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace AutoPilot.Combat;

/// <summary>
/// Uma skill detectada na barra do jogador, com a sua configuração de uso.
///
/// Naming consistente (resolve a inconsistência L5 do AutoMyAim, onde havia enabled/key minúsculos
/// misturados com PascalCase). Tudo PascalCase aqui.
///
/// A referência <see cref="Live"/> ao ActorSkill é [JsonIgnore] porque o endereço de memória muda
/// entre frames/áreas — é re-ligada todos os ticks pelo detector. O que persiste no JSON é a
/// identidade estável (Name/InternalName) e a config do utilizador (tecla, prioridade, ms).
/// </summary>
public sealed class SkillSlot
{
    [Menu("Ativa", "Liga/desliga esta skill na rotação.")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Mostrar config", "Liga para ver/editar TODAS as regras '[Geral]' desta skill. Desliga e elas " +
        "desaparecem (menu limpo). A configuração mantém-se guardada mesmo escondida.")]
    public ToggleNode ShowConfig { get; set; } = new(false);

    /// <summary>Condição do ConditionalDisplay: mostra os campos [Geral] só quando ShowConfig está ligado.</summary>
    public bool IsConfigVisible() => ShowConfig.Value;

    [Menu("Tecla", "Tecla a premir para esta skill. Auto-detetada da barra; podes mudar.")]
    public HotkeyNodeV2 Key { get; set; } = new(Keys.None);

    [Menu("Prioridade", "Ordem de avaliação na rotação. MAIOR = avaliada primeiro. (Para a Routine Geral futura.)")]
    public RangeNode<int> Priority { get; set; } = new(0, 0, 100);

    [Menu("Tap Hold (ms)", "Gap entre KeyDown e KeyUp para esta skill. Para skills que precisam de mais tempo a registar.")]
    public RangeNode<int> TapHoldMs { get; set; } = new(12, 1, 200);

    // ── Regras (Routine Geral) — todos os campos da SkillRule expostos na UI (Fase 3.4) ───────
    // Estes só são usados pelo motor "Geral" (dropdown Rotina de combate). O IceShot/Staff ignoram-nos.

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Tipo de uso", "Tap = um toque; Hold = segura até confirmar; Buff = sem alvo; Persistente = em movimento.")]
    public ListNode UseType { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Tap", "Hold", "Buff", "Persistent" },
        Value = "Tap",
    };

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Cooldown (ms)", "Cooldown interno anti-spam desta skill no motor Geral.")]
    public RangeNode<int> CooldownMs { get; set; } = new(0, 0, 10000);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Atacar parado (Shift)", "Segura Shift ao usar = ataca sem andar para o cursor (build de arco).")]
    public ToggleNode AttackInPlace { get; set; } = new(false);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Raridade mínima do alvo", "Só usa contra esta raridade e acima.")]
    public ListNode MinRarity { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Qualquer", "Magic+", "Rare+", "Só Unique", "Só Normal" },
        Value = "Qualquer",
    };

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Unique ignora alcance", "Bosses/Uniques são sempre alvo válido mesmo além do alcance.")]
    public ToggleNode IgnoreRangeForUnique { get; set; } = new(false);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Dist. mínima", "Distância mínima ao alvo (grid). 0 = sem mínimo.")]
    public RangeNode<float> MinDistance { get; set; } = new(0f, 0f, 200f);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Dist. máxima", "Distância máxima ao alvo (grid). 0 = sem máximo.")]
    public RangeNode<float> MaxDistance { get; set; } = new(0f, 0f, 200f);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] HP alvo mín %", "Só usa se o HP% do alvo é >= isto. 0 = ignora.")]
    public RangeNode<float> TargetHpMin { get; set; } = new(0f, 0f, 1f);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] HP alvo máx %", "Só usa se o HP% do alvo é <= isto. 1 = ignora (ex.: 0.1 = culling).")]
    public RangeNode<float> TargetHpMax { get; set; } = new(1f, 0f, 1f);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Mobs perto (N)", "Só usa se há >= N mobs perto do alvo (AoE/packs). 0 = ignora.")]
    public RangeNode<int> CloseTargets { get; set; } = new(0, 0, 30);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Raio mobs perto", "Raio para contar mobs perto do alvo.")]
    public RangeNode<float> CloseTargetsRange { get; set; } = new(10f, 1f, 100f);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Alvo TEM buff", "Nome interno do buff/debuff que o ALVO tem de ter (ex.: frozen). Vazio = ignora.")]
    public TextNode TargetHasBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Alvo SEM buff", "Nome do buff/debuff que o alvo NÃO pode ter (ex.: freezing_mark). Vazio = ignora.")]
    public TextNode TargetMissingBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Player TEM buff", "Nome do buff que o PLAYER tem de ter. Vazio = ignora.")]
    public TextNode PlayerHasBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Player SEM buff", "Nome do buff que o player NÃO pode ter (ex.: shearing_bolts). Vazio = ignora.")]
    public TextNode PlayerMissingBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Boss ignora 'Player SEM buff'", "Para a Mark: no BOSS marca mesmo com o buff de dano ativo " +
        "(ignora 'Player SEM buff'); fora do boss respeita-o. Reproduz a regra da Mark.")]
    public ToggleNode BossIgnoresPlayerMissingBuff { get; set; } = new(false);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Buff de charges", "Nome do buff cujas charges são contadas (ex.: skill_seals). Vazio = ignora.")]
    public TextNode ChargeBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Charges mín", "Mínimo de charges do buff acima para usar a skill.")]
    public RangeNode<int> ChargeMin { get; set; } = new(0, 0, 30);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Depois da skill", "Nome de memória da skill-âncora: só dispara DEPOIS dela (combo). Vazio = livre.")]
    public TextNode AfterSkill { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Atraso após âncora (ms)", "Tempo a esperar após a skill-âncora antes de disparar (ex.: 400 p/ Barrage→Snipe).")]
    public RangeNode<int> AfterSkillDelayMs { get; set; } = new(0, 0, 3000);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Soltar hold quando", "Como confirmar que a skill (Hold) saiu, para largar a tecla.")]
    public ListNode ReleaseWhen { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string>
            { "Timeout", "Buff no alvo", "Buff no player", "Charges baixam", "Skill usada", "Stage animação" },
        Value = "Timeout",
    };

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Buff p/ soltar", "Nome do buff para 'Buff no alvo/player' ou 'Charges baixam'.")]
    public TextNode ReleaseBuffName { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Stage p/ soltar", "Stage de animação para 'Stage animação' (ex.: Snipe = 21).")]
    public RangeNode<int> ReleaseAnimationStage { get; set; } = new(0, 0, 50);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Timeout do hold (ms)", "Tempo máximo a segurar antes de soltar à força (rede de segurança).")]
    public RangeNode<int> ReleaseTimeoutMs { get; set; } = new(500, 50, 3000);

    // ── Identidade (persiste no JSON) ──────────────────────────────────────────────────────
    public string Name { get; set; } = "";          // nome de memória (ex.: "BarragePlayer")
    public string InternalName { get; set; } = "";  // id estável do jogo (ex.: "barrage")
    public string DisplayName { get; set; } = "";   // nome amigável p/ o menu

    // ── Estado vivo (re-ligado a cada tick; não persiste) ──────────────────────────────────
    [JsonIgnore] public ActorSkill Live { get; set; }

    /// <summary>True se a skill está pronta a usar (existe, ligada, e o jogo permite usá-la agora).</summary>
    public bool IsReady =>
        Enabled.Value
        && Key.Value.Key != Keys.None
        && Live != null
        && SafeCanBeUsed();

    private bool SafeCanBeUsed()
    {
        try { return Live.CanBeUsed; }
        catch { return false; }
    }

    // ── Confirmação de uso (do ActorSkill) — leitura defensiva para diagnóstico/rotação ────────
    // SkillUseStage: estágio cru do uso da skill. IsUsing = a usar; IsChanneling = a canalizar;
    // IsOnCooldown = entrou em cooldown (acabou de sair). Ver memória actorskill-use-confirmation.

    /// <summary>Estágio de uso cru (byte) do ActorSkill. -1 se ilegível.</summary>
    public int UseStage { get { try { return Live?.SkillUseStage ?? -1; } catch { return -1; } } }

    /// <summary>True se a skill está a ser usada agora (SkillUseStage > 1).</summary>
    public bool IsUsing { get { try { return Live?.IsUsing ?? false; } catch { return false; } } }

    /// <summary>True se a skill está a canalizar (CastType == 10).</summary>
    public bool IsChanneling { get { try { return Live?.IsChanneling ?? false; } catch { return false; } } }

    /// <summary>True se a skill está em cooldown (acabou de ser usada).</summary>
    public bool IsOnCooldown { get { try { return Live?.IsOnCooldown ?? false; } catch { return false; } } }

    /// <summary>Contador total de usos. -1 se ilegível. Um incremento = a skill saiu.</summary>
    public int TotalUses { get { try { return Live?.TotalUses ?? -1; } catch { return -1; } } }

    public override string ToString() =>
        !string.IsNullOrEmpty(DisplayName) ? DisplayName : (Name ?? "");
}
