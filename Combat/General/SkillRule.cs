using System.Windows.Forms;

namespace AutoPilot.Combat.General;

/// <summary>
/// FASE 1 da Routine Geral (ver ROUTINE_GERAL_PLANO.md / memória autopilot-master-plan-v2).
///
/// Modelo de dados PURO de uma regra de skill — todas as condições que decidem SE e COMO uma skill é
/// usada, sem qualquer lógica de jogo. É a fundação: o motor genérico (Fase 3) lê isto; o avaliador
/// (RuleEvaluator) decide. Baseado no modelo do ExiledBot skills.ini (memória
/// exiledbot-combat-model-reference) + a confirmação de uso via ActorSkill (actorskill-use-confirmation).
///
/// PRINCÍPIO: todos os defaults NÃO FILTRAM. Uma regra acabada de criar deixa a skill disparar em
/// qualquer condição — só restringe o que o utilizador configurar. Assim adicionar este modelo não
/// muda comportamento nenhum (é 100% aditivo; nada o usa ainda nesta fase).
///
/// NOTA: esta classe é só dados (POCO). Não lê memória, não tem efeitos. Os campos de UI (ToggleNode
/// etc.) entram só na Fase 3.4 (UI); por agora são tipos simples para o avaliador poder ser testado.
/// </summary>
public sealed class SkillRule
{
    // ── Identidade / ação ──────────────────────────────────────────────────────────────────
    /// <summary>Nome de memória da skill (ex.: "BarragePlayer"). Liga a regra ao SkillSlot detetado.</summary>
    public string SkillName { get; set; } = "";

    /// <summary>Tipo de uso da skill.</summary>
    public SkillUseType UseType { get; set; } = SkillUseType.Tap;

    /// <summary>Maior = avaliada primeiro. Empates resolvidos pela ordem na lista.</summary>
    public int Priority { get; set; } = 0;

    /// <summary>Cooldown interno anti-spam (ms). O cooldown REAL do jogo é lido por IsReady à parte.</summary>
    public int CooldownMs { get; set; } = 0;

    /// <summary>Segurar Shift (ou tecla equivalente) ao usar = atacar PARADO (build de arco).</summary>
    public bool AttackInPlace { get; set; } = false;

    // ── Gates de ALVO ──────────────────────────────────────────────────────────────────────
    /// <summary>Raridade mínima do alvo para usar a skill. Default: qualquer.</summary>
    public TargetRarity MinRarity { get; set; } = TargetRarity.Any;

    /// <summary>
    /// Dureza MÍNIMA do alvo para usar a skill (HP_ROTATION). A skill só sai se o nível do alvo é >= isto:
    /// Easy = sempre (não filtra); Medium = só médios e tanks; Tank = só os mais duros (combo).
    /// O nível vem já calculado em <c>RoutineContext.TargetHardness</c> (1x/tick). Default Easy = aditivo.
    /// </summary>
    public TargetHardness MinHardness { get; set; } = TargetHardness.Easy;

    /// <summary>Uniques/bosses são sempre alvo válido mesmo além do alcance configurado.</summary>
    public bool IgnoreRangeForUnique { get; set; } = false;

    /// <summary>
    /// GENÉRICO (qualquer skill): nome/path da ENTIDADE no chão que esta skill cria (ex.: o tornado
    /// "TornadoShotTornado", um sino, um totem, uma poça). O plugin deteta a entidade por este path nas
    /// MiscellaneousObjects. Vazio = ignora. Combina com <see cref="SkipIfGroundActive"/>. Substring
    /// case-insensitive (basta a parte final do path, como os nomes de buff).
    /// </summary>
    public string GroundEntityPath { get; set; } = "";

    /// <summary>
    /// Se true, a skill NÃO é usada enquanto já existir a entidade <see cref="GroundEntityPath"/> viva no
    /// chão perto do alvo (no teu range) — uptime sem spam (ex.: não re-lançar o tornado/sino enquanto há
    /// um). Sem efeito se GroundEntityPath estiver vazio.
    /// </summary>
    public bool SkipIfGroundActive { get; set; } = false;

    /// <summary>Distância mínima ao alvo (grid). 0 = sem mínimo.</summary>
    public float MinDistance { get; set; } = 0f;

    /// <summary>Distância máxima ao alvo (grid). 0 = sem máximo (não filtra).</summary>
    public float MaxDistance { get; set; } = 0f;

    /// <summary>HP do alvo: só usa se o HP% está NESTA banda. Min 0 / Max 1 = não filtra.</summary>
    public float TargetHpMinPercent { get; set; } = 0f;
    public float TargetHpMaxPercent { get; set; } = 1f;

    /// <summary>Nº mínimo de mobs num raio do alvo para usar (AoE/packs). 0 = não filtra.</summary>
    public int CloseTargets { get; set; } = 0;
    public float CloseTargetsRange { get; set; } = 10f;

    // ── Gate de MOD do alvo (M2; regex sobre os mods internos; vazio = ignora) ─────────────────
    /// <summary>
    /// Só usa a skill se o ALVO (que o targeting JÁ escolheu) casa este regex de mod (ex.:
    /// "CorpseExploder|Volatile"). Vazio = ignora. Hierarquia: o targeting decide QUEM; este gate
    /// decide se a skill sai contra esse alvo. Avaliado via ModReader/ModRule (cache por-tick).
    /// </summary>
    public string TargetMatchesMod { get; set; } = "";

    // ── Gates de BUFF (nome interno do jogo; vazio = ignora) ───────────────────────────────
    /// <summary>Só usa se o ALVO TEM este buff/debuff (ex.: "frozen").</summary>
    public string TargetHasBuff { get; set; } = "";
    /// <summary>Só usa se o ALVO NÃO TEM este buff/debuff (ex.: "freezing_mark" → só marca se não marcado).</summary>
    public string TargetMissingBuff { get; set; } = "";
    /// <summary>Só usa se o PLAYER TEM este buff.</summary>
    public string PlayerHasBuff { get; set; } = "";
    /// <summary>Só usa se o PLAYER NÃO TEM este buff (ex.: Ice-Tipped só se não tens "shearing_bolts").</summary>
    public string PlayerMissingBuff { get; set; } = "";

    /// <summary>
    /// Se true, a condição PlayerMissingBuff é IGNORADA quando o alvo é Unique/boss. Reproduz a regra
    /// da Mark: fora do boss não remarca com o buff de dano ativo (respeita PlayerMissingBuff), mas no
    /// BOSS remarca sempre (ignora o buff) para manter o debuff de freeze. Só afeta o gate PlayerMissingBuff.
    /// </summary>
    public bool BossIgnoresPlayerMissingBuff { get; set; } = false;

    /// <summary>Só usa se o player tem >= N charges deste buff (ex.: "skill_seals" >= 10). Vazio = ignora.</summary>
    public string ChargeBuff { get; set; } = "";
    public int ChargeMin { get; set; } = 0;

    // ── Encadeamento temporal entre skills (CRÍTICO p/ combos tipo Barrage→Snipe) ──────────
    /// <summary>
    /// Só dispara DEPOIS de a skill nomeada ter saído (vazio = ignora). NÃO é "esperar a animação
    /// acabar" — é "X ms DEPOIS de a skill X ter sido usada", para o Snipe entrar DURANTE a janela de
    /// empower do Barrage. Reproduz o BarrageCommitMs. (Correção #2 da auditoria.)
    /// </summary>
    public string AfterSkill { get; set; } = "";
    public int AfterSkillDelayMs { get; set; } = 0;

    // ── Condição de soltar (só para UseType.Hold) ──────────────────────────────────────────
    /// <summary>Como confirmar que a skill saiu e o hold pode soltar.</summary>
    public HoldReleaseCondition ReleaseWhen { get; set; } = HoldReleaseCondition.Timeout;
    /// <summary>Nome do buff para as condições de release baseadas em buff (alvo/player).</summary>
    public string ReleaseBuffName { get; set; } = "";
    /// <summary>Stage de animação para ReleaseWhen.AnimationStage (ex.: Snipe = 21).</summary>
    public int ReleaseAnimationStage { get; set; } = 0;
    /// <summary>Tempo máximo a segurar o hold antes de soltar à força (rede de segurança).</summary>
    public int ReleaseTimeoutMs { get; set; } = 500;
}

/// <summary>Como a skill é acionada.</summary>
public enum SkillUseType
{
    /// <summary>Um toque (KeyDown→KeyUp curto). Ex.: Ice Shot.</summary>
    Tap,
    /// <summary>Segura até a condição de release. Ex.: Mark até pegar, Snipe até stage 21.</summary>
    Hold,
    /// <summary>Buff: usa sem checks de alvo (não precisa de mob). Ex.: aura/buff de combate.</summary>
    Buff,
    /// <summary>Persistente: premido periodicamente mesmo em movimento/exploração.</summary>
    Persistent,
}

/// <summary>Raridade mínima do alvo (ordem crescente; "+" = essa raridade e acima).</summary>
public enum TargetRarity
{
    Any,        // qualquer monstro
    MagicPlus,  // magic e acima
    RarePlus,   // rare e acima
    UniqueOnly, // só unique/boss
    NormalOnly, // só normal (branco)
}

/// <summary>Quando soltar um hold (confirmação de que a skill saiu).</summary>
public enum HoldReleaseCondition
{
    /// <summary>Solta ao fim do ReleaseTimeoutMs (rede de segurança; sempre presente como fallback).</summary>
    Timeout,
    /// <summary>Solta quando o buff aparece no ALVO (ex.: Mark → freezing_mark).</summary>
    TargetBuffAppears,
    /// <summary>Solta quando o buff aparece no PLAYER (ex.: Ice-Tipped → shearing_bolts).</summary>
    PlayerBuffAppears,
    /// <summary>Solta quando as charges do ReleaseBuffName BAIXAM (ex.: Salvo → seals consumidos).</summary>
    PlayerChargesDrop,
    /// <summary>Solta quando o ActorSkill confirma uso/cooldown (ex.: Tornado).</summary>
    SkillUsed,
    /// <summary>Solta quando o stage de animação chega a ReleaseAnimationStage (ex.: Snipe = 21).</summary>
    AnimationStage,
}
