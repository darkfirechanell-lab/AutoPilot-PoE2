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

    [Menu("Mostrar config", "Liga para ver/editar as regras desta skill. Desliga e desaparecem (menu " +
        "limpo). A configuração mantém-se guardada mesmo escondida.")]
    public ToggleNode ShowConfig { get; set; } = new(false);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("Mostrar avançado", "Liga para ver os campos AVANÇADOS (tempos finos, filtros de alvo, hold/" +
        "charges, entidade no chão). Desligado, vês só o ESSENCIAL (tipo, raridade, dureza, buffs). A " +
        "maioria das builds não precisa do avançado.")]
    public ToggleNode ShowAdvanced { get; set; } = new(false);

    /// <summary>ESSENCIAL: mostra os campos-chave quando 'Mostrar config' está ligado.</summary>
    public bool IsConfigVisible() => ShowConfig.Value;

    /// <summary>AVANÇADO: mostra os campos finos só com 'Mostrar config' E 'Mostrar avançado' ligados.</summary>
    public bool IsAdvancedVisible() => ShowConfig.Value && ShowAdvanced.Value;

    [Menu("Tecla", "Tecla a premir para esta skill. Auto-detetada da barra; podes mudar.")]
    public HotkeyNodeV2 Key { get; set; } = new(Keys.None);

    // ── REGRA EXTRA (F2): uma 2ª regra para a MESMA skill, para momentos diferentes (ex.: Barrage em
    // Medium sem frozen + Barrage no Tank/boss com frozen). Liga o toggle e aparece um 2º bloco LIMPO.
    // O motor corre as 2 regras sem colidir (RuleId da F1). A tecla é partilhada (é a mesma skill).
    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("Regra extra", "Ativa uma SEGUNDA regra para esta skill (momento diferente). Aparece um 2º bloco " +
        "'[Regra 2]' com condições próprias. Ex.: Barrage normal (Medium) + Barrage no combo do boss (frozen).")]
    public ToggleNode HasExtraRule { get; set; } = new(false);

    /// <summary>Mostra os campos [Regra 2] só quando ShowConfig E HasExtraRule estão ligados.</summary>
    public bool IsExtraVisible() => ShowConfig.Value && HasExtraRule.Value;

    // ── Regras (Routine Geral) — todos os campos da SkillRule expostos na UI (Fase 3.4) ───────
    // Estes só são usados pelo motor "Geral" (dropdown Rotina de combate). O IceShot/Staff ignoram-nos.
    // Prioridade e Tap Hold também ficam aqui (escondidos atrás de 'Mostrar config'): por defeito cada
    // skill mostra só Ativa/Tecla/Mostrar config — menu limpo (pedido do utilizador 2026-06-05).

    // ════════ ESSENCIAL — o que defines em quase todas as skills ════════

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Prioridade", "Ordem na rotação: MAIOR = avaliada PRIMEIRO. Ex.: Tornado=100 (abre o " +
        "combo), Barrage=90, Snipe=80, Ice Shot=10 (filler, último). Empate = ordem na lista.")]
    public RangeNode<int> Priority { get; set; } = new(0, 0, 100);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Tipo de uso", "COMO a skill é acionada. Tap = um toque (Ice Shot, Barrage). " +
        "Hold = segura a tecla até confirmar que saiu (Snipe canalizado, Mark até pegar). " +
        "Buff = usa sem precisar de alvo (auras). Persistente = premida em movimento.")]
    public ListNode UseType { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Tap", "Hold", "Buff", "Persistent" },
        Value = "Tap",
    };

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Cooldown (ms)", "Anti-spam: espera ESTE tempo antes de re-usar a skill. " +
        "Ex.: Mark=1000 (não remarca todo o tick); Ice Shot=50 (filler rápido). 0 = sem limite.")]
    public RangeNode<int> CooldownMs { get; set; } = new(0, 0, 10000);

    // ════════ AVANÇADO — TEMPOS finos (raro mexer) ════════

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Commit animação (ms)", "Após ESTA skill disparar, NENHUMA outra dispara durante este " +
        "tempo — protege a animação de ser cortada. Ex.: Barrage=400 (senão a skill seguinte corta a " +
        "puxada do arco e o buff falha). 0 = sem proteção (o normal).")]
    public RangeNode<int> CommitMs { get; set; } = new(0, 0, 2000);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Tap Hold (ms)", "Quanto tempo a tecla fica premida num Tap (gap KeyDown→KeyUp). " +
        "Default 12 chega quase sempre. Sobe só se uma skill não regista o toque.")]
    public RangeNode<int> TapHoldMs { get; set; } = new(12, 1, 200);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Cooldown POR ALVO (ms)", "Variante do Cooldown: conta por MOB (não global). Re-usa a " +
        "skill no MESMO mob só após este tempo, mas pode usá-la JÁ noutro mob. Substitui o Cooldown quando " +
        ">0. Raro precisar. 0 = usa o Cooldown normal (o que queres quase sempre).")]
    public RangeNode<int> PerTargetCooldownMs { get; set; } = new(0, 0, 30000);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Atacar parado (Shift)", "Segura Shift ao usar = ataca PARADO, sem andar para o cursor " +
        "(essencial nas builds de arco para não te moveres ao disparar).")]
    public ToggleNode AttackInPlace { get; set; } = new(false);

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Raridade mínima do alvo", "Só usa contra esta raridade e ACIMA. Ex.: 'Rare+' = só " +
        "rares/uniques (combo); 'Qualquer' = todos (filler como o Ice Shot).")]
    public ListNode MinRarity { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Qualquer", "Magic+", "Rare+", "Só Unique", "Só Normal" },
        Value = "Qualquer",
    };

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Dureza mínima do alvo", "Só usa contra alvos deste nível de dureza ou ACIMA. " +
        "Easy = sempre; Medium = só médios e tanks; Tank = só os mais duros (combo). " +
        "A dureza vem da vida do mob relativa à zona (ver sliders de Dureza).")]
    public ListNode MinHardness { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Easy", "Medium", "Tank" },
        Value = "Easy",
    };

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Dureza máxima do alvo", "Só usa contra alvos deste nível de dureza ou ABAIXO (teto). " +
        "Tank = sem teto (default). Serve para regras 'só este nível': ex.: Barrage SÓ em Medium " +
        "(mín=Medium, máx=Medium), separado da regra do Tank. Tem de ser >= dureza mínima.")]
    public ListNode MaxHardness { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Easy", "Medium", "Tank" },
        Value = "Tank",
    };

    // ── BUFFS / DEBUFFS (gates importantes — ficam no essencial) ──

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Alvo TEM buff", "Só usa se o ALVO tem este buff/debuff. Ex.: 'frozen' no Snipe (combo " +
        "só sai com o alvo congelado). Vazio = ignora. Nome interno do jogo (vê no plugin Dev/buffnames).")]
    public TextNode TargetHasBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Alvo SEM buff", "Só usa se o alvo NÃO tem este buff. Ex.: 'freezing_mark' na Mark (não " +
        "remarca quem já está marcado). Vazio = ignora.")]
    public TextNode TargetMissingBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Player TEM buff", "Só usa se TU tens este buff. Vazio = ignora.")]
    public TextNode PlayerHasBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsConfigVisible))]
    [Menu("[Geral] Player SEM buff", "Só usa se TU NÃO tens este buff (uptime sem spam). Ex.: " +
        "'empower_barrage_visual' no Barrage (só re-clica quando o buff cai); 'shearing_bolts' no " +
        "Ice-Tipped. Vazio = ignora.")]
    public TextNode PlayerMissingBuff { get; set; } = new("");

    // ════════ AVANÇADO — FILTROS DE ALVO finos ════════

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Unique ignora alcance", "Bosses/Uniques são alvo válido MESMO além do alcance configurado " +
        "(para o aim não desistir do boss quando ele se afasta).")]
    public ToggleNode IgnoreRangeForUnique { get; set; } = new(false);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Dist. mínima", "Só usa se o alvo está a ESTA distância ou MAIS (grid). 0 = sem mínimo. " +
        "Útil para skills que precisam de espaço.")]
    public RangeNode<float> MinDistance { get; set; } = new(0f, 0f, 200f);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Dist. máxima", "Só usa se o alvo está a ESTA distância ou MENOS (grid). 0 = sem máximo.")]
    public RangeNode<float> MaxDistance { get; set; } = new(0f, 0f, 200f);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] HP alvo mín %", "Só usa se o HP% do alvo é >= isto (0..1). 0 = ignora.")]
    public RangeNode<float> TargetHpMin { get; set; } = new(0f, 0f, 1f);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] HP alvo máx %", "Só usa se o HP% do alvo é <= isto (0..1). 1 = ignora. Ex.: 0.1 = só " +
        "remata mobs abaixo de 10% (culling/finisher).")]
    public RangeNode<float> TargetHpMax { get; set; } = new(1f, 0f, 1f);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Mobs perto (N)", "Só usa se há >= N mobs num raio do alvo (para AoE em packs). 0 = ignora.")]
    public RangeNode<int> CloseTargets { get; set; } = new(0, 0, 30);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Raio mobs perto", "O raio para contar os 'Mobs perto (N)' acima.")]
    public RangeNode<float> CloseTargetsRange { get; set; } = new(10f, 1f, 100f);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Boss ignora 'Player SEM buff'", "No BOSS, ignora o gate 'Player SEM buff' (usa mesmo com o " +
        "buff ativo); fora do boss respeita-o. Reproduz a regra da Mark (remarca sempre no boss).")]
    public ToggleNode BossIgnoresPlayerMissingBuff { get; set; } = new(false);

    // ════════ AVANÇADO — ENTIDADE NO CHÃO (tornado, sino, totem) ════════

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Entidade no chão (path)", "Path da entidade que esta skill cria no chão (ex.: " +
        "TornadoShotTornado). Basta a parte final (substring). Combina com 'Não usar se já no chão'. " +
        "Descobre o path no plugin Dev. Vazio = ignora.")]
    public TextNode GroundEntityPath { get; set; } = new("");

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Não usar se já no chão", "Não re-lança enquanto a 'Entidade no chão' acima já existe perto " +
        "do alvo (uptime sem spam — ex.: 1 tornado por raro). Precisa do path acima preenchido.")]
    public ToggleNode SkipIfGroundActive { get; set; } = new(false);

    // ════════ AVANÇADO — CHARGES e COMBO (encadeamento) ════════

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Buff de charges", "Nome do buff cujas CHARGES são contadas (ex.: skill_seals do Salvo). " +
        "Combina com 'Charges mín'. Vazio = ignora.")]
    public TextNode ChargeBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Charges mín", "Só usa se tens >= ESTE número de charges do buff acima (ex.: Salvo com >= 10 seals).")]
    public RangeNode<int> ChargeMin { get; set; } = new(0, 0, 30);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Depois da skill", "COMBO: só dispara DEPOIS de outra skill ter saído. Nome de memória da " +
        "skill-âncora (ex.: BarragePlayer no Snipe). Combina com 'Atraso após âncora'. Vazio = livre.")]
    public TextNode AfterSkill { get; set; } = new("");

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Atraso após âncora (ms)", "Quanto esperar após a skill-âncora antes de disparar. Ex.: 400 " +
        "no Snipe (entra durante a janela do empower do Barrage).")]
    public RangeNode<int> AfterSkillDelayMs { get; set; } = new(0, 0, 3000);

    // ════════ AVANÇADO — HOLD (como largar a tecla) ════════

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Soltar hold quando", "Só para Tipo de uso=Hold. COMO confirmar que a skill saiu, para " +
        "largar a tecla: Timeout (tempo), Buff no alvo/player (aparece o buff), Charges baixam, Skill usada " +
        "(o jogo confirma), Stage animação (ex.: Snipe chega ao stage de tiro).")]
    public ListNode ReleaseWhen { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string>
            { "Timeout", "Buff no alvo", "Buff no player", "Charges baixam", "Skill usada", "Stage animação" },
        Value = "Timeout",
    };

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Buff p/ soltar", "O nome do buff para 'Soltar hold quando' = Buff no alvo/player ou Charges baixam.")]
    public TextNode ReleaseBuffName { get; set; } = new("");

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Stage p/ soltar", "O stage de animação para 'Soltar hold quando' = Stage animação " +
        "(ex.: Snipe solta no stage 21 = o tiro). Só relevante nesse modo.")]
    public RangeNode<int> ReleaseAnimationStage { get; set; } = new(0, 0, 50);

    [ConditionalDisplay(nameof(IsAdvancedVisible))]
    [Menu("[Geral] Timeout do hold (ms)", "Rede de segurança: tempo MÁXIMO a segurar antes de largar à força " +
        "(se a condição de soltar nunca der). Default 500 chega; Snipe canalizado usa mais (2000).")]
    public RangeNode<int> ReleaseTimeoutMs { get; set; } = new(500, 50, 3000);

    // ── REGRA 2 (F2) — 2ª regra da MESMA skill, momento diferente. Só os campos que costumam MUDAR entre
    // momentos (raridade, dureza, frozen, tipo, prioridade, cooldown, encadeamento). A tecla, o release e
    // os restantes são partilhados com a regra 1. Visível só com 'Regra extra' ligado.
    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Tipo de uso", "Tap = um toque; Hold = segura até confirmar; Buff = sem alvo.")]
    public ListNode Extra_UseType { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Tap", "Hold", "Buff", "Persistent" },
        Value = "Tap",
    };

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Prioridade", "Ordem de avaliação. MAIOR = avaliada primeiro.")]
    public RangeNode<int> Extra_Priority { get; set; } = new(0, 0, 100);

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Cooldown (ms)", "Cooldown interno desta 2ª regra (independente da regra 1).")]
    public RangeNode<int> Extra_CooldownMs { get; set; } = new(0, 0, 10000);

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Raridade mínima do alvo", "Só usa contra esta raridade e acima.")]
    public ListNode Extra_MinRarity { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Qualquer", "Magic+", "Rare+", "Só Unique", "Só Normal" },
        Value = "Qualquer",
    };

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Dureza mínima do alvo", "Só usa contra alvos deste nível ou ACIMA (Easy/Medium/Tank).")]
    public ListNode Extra_MinHardness { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Easy", "Medium", "Tank" },
        Value = "Easy",
    };

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Dureza máxima do alvo", "Só usa contra alvos deste nível ou ABAIXO (teto). Tank = sem teto.")]
    public ListNode Extra_MaxHardness { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Easy", "Medium", "Tank" },
        Value = "Tank",
    };

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Alvo TEM buff", "Buff/debuff que o ALVO tem de ter (ex.: frozen). Vazio = ignora.")]
    public TextNode Extra_TargetHasBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Alvo SEM buff", "Buff/debuff que o alvo NÃO pode ter. Vazio = ignora.")]
    public TextNode Extra_TargetMissingBuff { get; set; } = new("");

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Depois da skill", "Nome de memória da skill-âncora: só dispara DEPOIS dela. Vazio = livre.")]
    public TextNode Extra_AfterSkill { get; set; } = new("");

    [ConditionalDisplay(nameof(IsExtraVisible))]
    [Menu("[Regra 2] Atraso após âncora (ms)", "Tempo a esperar após a âncora antes de disparar.")]
    public RangeNode<int> Extra_AfterSkillDelayMs { get; set; } = new(0, 0, 3000);

    // ── Identidade (persiste no JSON, mas NÃO é elemento de menu) ───────────────────────────
    // [IgnoreMenu]: o ExileCore varre as propriedades públicas para construir o menu e avisa
    // "is not a supported settings element" para tudo o que não seja um Node. Sem isto, esses
    // warnings sujavam o load das skills e as configs pareciam não persistir entre arranques.
    [IgnoreMenu] public string Name { get; set; } = "";          // nome de memória (ex.: "BarragePlayer")
    [IgnoreMenu] public string InternalName { get; set; } = "";  // id estável do jogo (ex.: "barrage")
    [IgnoreMenu] public string DisplayName { get; set; } = "";   // nome amigável p/ o menu

    // ── Estado vivo (re-ligado a cada tick; não persiste nem aparece no menu) ───────────────
    [JsonIgnore, IgnoreMenu] public ActorSkill Live { get; set; }

    /// <summary>True se a skill está pronta a usar (existe, ligada, e o jogo permite usá-la agora).</summary>
    [JsonIgnore, IgnoreMenu] public bool IsReady =>
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
    [JsonIgnore, IgnoreMenu] public int UseStage { get { try { return Live?.SkillUseStage ?? -1; } catch { return -1; } } }

    /// <summary>True se a skill está a ser usada agora (SkillUseStage > 1).</summary>
    [JsonIgnore, IgnoreMenu] public bool IsUsing { get { try { return Live?.IsUsing ?? false; } catch { return false; } } }

    /// <summary>True se a skill está a canalizar (CastType == 10).</summary>
    [JsonIgnore, IgnoreMenu] public bool IsChanneling { get { try { return Live?.IsChanneling ?? false; } catch { return false; } } }

    /// <summary>True se a skill está em cooldown (acabou de ser usada).</summary>
    [JsonIgnore, IgnoreMenu] public bool IsOnCooldown { get { try { return Live?.IsOnCooldown ?? false; } catch { return false; } } }

    /// <summary>Contador total de usos. -1 se ilegível. Um incremento = a skill saiu.</summary>
    [JsonIgnore, IgnoreMenu] public int TotalUses { get { try { return Live?.TotalUses ?? -1; } catch { return -1; } } }

    public override string ToString() =>
        !string.IsNullOrEmpty(DisplayName) ? DisplayName : (Name ?? "");
}
