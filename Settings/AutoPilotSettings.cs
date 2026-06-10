using System.Windows.Forms;
using AutoPilot.Combat;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace AutoPilot.Settings;

/// <summary>
/// Raiz das definições do AutoPilot. Tudo dividido por SECÇÕES (submenus): Aim, Combate, Kiting, Mods,
/// Logs, Perfil, Skills + (condicionais) IceShot/Staff. Os campos vivem nos submenus; a raiz tem
/// ATALHOS [IgnoreMenu, JsonIgnore] para o resto do código aceder sem reescrever o plugin
/// (Settings.AttackRange continua a funcionar e aponta para Combat.AttackRange).
/// </summary>
public class AutoPilotSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);

    // ── NO TOPO (sempre visíveis, fora de secção): as escolhas principais — teclas de aim + routine.
    // Aim Key/Toggle usam o HotkeyNode ANTIGO de propósito (aceita botões do rato; o V2 bloqueia-os).
#pragma warning disable CS0618
    [Menu("Aim Key", "Tecla a manter premida para ativar o aim/combate. Aceita botões do rato (LB/RB/MB).")]
    public HotkeyNode AimKey { get; set; } = new(Keys.None);

    [Menu("Aim Toggle Key", "Tecla que liga/desliga o aim em modo toggle. Aceita botões do rato.")]
    public HotkeyNode AimToggleKey { get; set; } = new(Keys.None);
#pragma warning restore CS0618

    [Menu("Rotina de combate", "Qual rotação de skills usar. 'Ice Shot' = arco; 'Staff' = cajado; " +
        "'Geral' = motor configurável pela UI (regras por skill em 'Skills').")]
    public ListNode Routine { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Ice Shot", "Staff", "Geral" },
        Value = "Geral",
    };

    // Ordem das secções escolhida pelo utilizador (literal). O ExileCore renderiza pela ordem de
    // declaração das propriedades.

    // 1 ── LOGS
    [Submenu(CollapsedByDefault = true)]
    public LogsSettings Logs { get; set; } = new();

    // 2 ── PERFIL — ImGui custom (estilo PickIt): [JsonIgnore] na propriedade, RenderMethod na classe.
    [JsonIgnore]
    public ProfileSettings Perfil { get; set; } = new();

    // 3 ── STAFF (condicional: routine Staff)
    [ConditionalDisplay(nameof(IsStaffRoutine))]
    [Submenu(CollapsedByDefault = true)]
    public StaffSettings Staff { get; set; } = new();

    // 4 ── MODS
    [Submenu(CollapsedByDefault = true)]
    public ModsSettings Mods { get; set; } = new();

    // 5 ── AIM
    [Submenu(CollapsedByDefault = true)]
    public AimSettings Aim { get; set; } = new();

    // 6 ── KITING (dodge)
    [Submenu(CollapsedByDefault = true)]
    public KitingSettings Kiting { get; set; } = new();

    // 7 ── ICE SHOT (condicional: routine Ice Shot)
    [ConditionalDisplay(nameof(IsIceShotRoutine))]
    [Submenu(CollapsedByDefault = true)]
    public IceShotSettings IceShot { get; set; } = new();

    // 8 ── COMBATE
    [Submenu(CollapsedByDefault = true)]
    public CombatSettings Combat { get; set; } = new();

    // 8.5 ── DUREZA (HP_ROTATION): calibração da classificação de dureza do alvo (só afeta o motor Geral).
    [Submenu(CollapsedByDefault = true)]
    public HardnessSettings Dureza { get; set; } = new();

    // 9 ── SKILLS (com o botão Re-detetar Teclas lá dentro)
    [Menu("Skills")]
    [Submenu(CollapsedByDefault = true)]
    public SkillsSettings SkillsSection { get; set; } = new();

    // Mostra os settings de cada routine SÓ quando essa routine está selecionada.
    public bool IsIceShotRoutine() => Routine?.Value == "Ice Shot";
    public bool IsStaffRoutine() => Routine?.Value == "Staff";

    // ── ATALHOS (escondidos do menu): o resto do código usa estes; apontam para o campo no submenu.
    [IgnoreMenu, JsonIgnore] public RangeNode<float> CursorJitter => Aim.CursorJitter;
    [IgnoreMenu, JsonIgnore] public RangeNode<float> CursorSmoothing => Aim.CursorSmoothing;
    [IgnoreMenu, JsonIgnore] public ToggleNode UseVisibility => Aim.UseVisibility;

    [IgnoreMenu, JsonIgnore] public RangeNode<float> AttackRange => Combat.AttackRange;
    [IgnoreMenu, JsonIgnore] public RangeNode<float> ProximalRange => Combat.ProximalRange;
    [IgnoreMenu, JsonIgnore] public ToggleNode PauseOnPanels => Combat.PauseOnPanels;
    [IgnoreMenu, JsonIgnore] public ToggleNode GeneralUseUiRules => Combat.GeneralUseUiRules;
    [IgnoreMenu, JsonIgnore] public ButtonNode LoadIceShotPreset => Combat.LoadIceShotPreset;

    [IgnoreMenu, JsonIgnore] public ToggleNode ModTargeting => Mods.ModTargeting;
    [IgnoreMenu, JsonIgnore] public ButtonNode DumpMods => Mods.DumpMods;
#pragma warning disable CS0618 // HotkeyNode obsoleto, mas aceita botões do rato.
    [IgnoreMenu, JsonIgnore] public HotkeyNode DumpModsKey => Mods.DumpModsKey;
#pragma warning restore CS0618
    [IgnoreMenu, JsonIgnore] public ToggleNode AutoDumpMods => Mods.AutoDumpMods;

    [IgnoreMenu, JsonIgnore] public ToggleNode ShowDebug => Logs.ShowDebug;
    [IgnoreMenu, JsonIgnore] public ToggleNode WriteLogs => Logs.WriteLogs;
    [IgnoreMenu, JsonIgnore] public ToggleNode RecordBaseline => Logs.RecordBaseline;

    [IgnoreMenu, JsonIgnore] public ContentNode<SkillSlot> Skills => SkillsSection.Content;
    [IgnoreMenu, JsonIgnore] public ButtonNode RedetectKeys => SkillsSection.RedetectKeys;
}

// ── SECÇÃO AIM ─────────────────────────────────────────────────────────────────────────────
[Submenu(CollapsedByDefault = true)]
public class AimSettings
{
    // (Aim Key / Aim Toggle Key foram movidas para o TOPO da raiz — são as escolhas principais.)

    [Menu("Filtrar por Visibilidade (raycast)", "Ignora mobs atrás de paredes. Se o aim não foca nada, DESLIGA isto para testar se é o raycast.")]
    public ToggleNode UseVisibility { get; set; } = new(true);

    [Menu("Randomização do Cursor (px)", "Offset aleatório pequeno ao cursor (anti-robótico). 0 = exato no " +
        "centro do mob. Valores altos podem falhar mobs pequenos.")]
    public RangeNode<float> CursorJitter { get; set; } = new(4f, 0f, 20f);

    [Menu("Suavização do Cursor", "Movimento HUMANO: o cursor desliza para o mob em vez de teleportar. " +
        "0 = teleporte (mais preciso). Maior = mais suave, mas pode ficar atrás de mobs rápidos. ~0.4 bom meio-termo.")]
    public RangeNode<float> CursorSmoothing { get; set; } = new(0.4f, 0f, 1f);
}

// ── SECÇÃO COMBATE ─────────────────────────────────────────────────────────────────────────
[Submenu(CollapsedByDefault = true)]
public class CombatSettings
{
    [Menu("Combate ativo", "Liga/desliga o uso de skills (o aim continua a funcionar).")]
    public ToggleNode Enabled { get; set; } = new(true);

    // (Rotina de combate foi movida para o TOPO da raiz — é uma escolha principal.)

    [Menu("Attack Range", "Distância máxima ao alvo (grid) para o mirar/atacar. Ajusta ao alcance real da arma.")]
    public RangeNode<float> AttackRange { get; set; } = new(60f, 5f, 600f);

    [Menu("Proximal Tangibility: alcance", "Mobs com o mod 'Proximal Tangibility' são imunes à distância. " +
        "Só são mirados quando estás MAIS PERTO que isto. Se o boss não for atacado já perto, AUMENTA.")]
    public RangeNode<float> ProximalRange { get; set; } = new(25f, 5f, 100f);

    [Menu("Cursor Range", "Quão perto o cursor tem de estar do alvo antes de usar skills.")]
    public RangeNode<float> CursorRange { get; set; } = new(20f, 1f, 100f);

    [Menu("C1: Só atacar com cursor no alvo", "Não dispara skills de dano (Ice Shot/Barrage) se o cursor não " +
        "estiver em cima do alvo. Usa a posição que o AIM calcula (sem lag). Não afeta Mark nem canais a decorrer.")]
    public ToggleNode RequireCursorOnTarget { get; set; } = new(false);

    [Menu("C1: Tolerância (px)", "Distância máxima (px) entre o cursor e o centro do alvo para considerar que " +
        "vai acertar. Menor = mais apertado. Só conta se 'Só atacar com cursor no alvo' estiver ligado.")]
    public RangeNode<float> CursorOnTargetTolerance { get; set; } = new(35f, 5f, 150f);

    [Menu("Parar com painéis abertos", "Pausa o combate quando abres o inventário/loja/skill tree. Ao fechar, retoma.")]
    public ToggleNode PauseOnPanels { get; set; } = new(true);

    [Menu("[Geral] Usar regras da UI", "LIGADO: o motor Geral usa as regras '[Geral]' de cada skill. " +
        "DESLIGADO: usa o preset de gelo embutido.")]
    public ToggleNode GeneralUseUiRules { get; set; } = new(false);

    [Menu("[Geral] Carregar preset Ice Shot", "Preenche os campos '[Geral]' de cada skill com a rotação de " +
        "gelo já afinada. Depois liga 'Usar regras da UI' e testa.")]
    public ButtonNode LoadIceShotPreset { get; set; } = new();
}

// ── SECÇÃO KITING (dodge) ──────────────────────────────────────────────────────────────────
[Submenu(CollapsedByDefault = true)]
public class KitingSettings
{
    [Menu("Usar Dodge", "LIGADO: esquiva automaticamente (dodge roll) quando deteta perigo (mobs perto a atacar). " +
        "NÃO usa WASD. Quando esquiva, tem prioridade sobre o auto-aim nesse instante. Desligado por defeito.")]
    public ToggleNode UseDodge { get; set; } = new(false);

#pragma warning disable CS0618
    [Menu("Tecla de Dodge", "Tecla de esquiva do jogo (ex.: Espaço). Aceita botões do rato.")]
    public HotkeyNode DodgeKey { get; set; } = new(Keys.Space);
#pragma warning restore CS0618

    [Menu("Alcance de perigo", "Distância (grid) até à qual um mob a atacar conta como ameaça.")]
    public RangeNode<float> DangerRange { get; set; } = new(25f, 5f, 80f);

    [Menu("Limiar de perigo", "Nível de perigo (score) acima do qual esquiva. Maior = esquiva menos. " +
        "1 mob normal a atacar ≈ 2; um Rare ≈ 4; um Boss ≈ 6.")]
    public RangeNode<float> DangerThreshold { get; set; } = new(3f, 1f, 15f);

    [Menu("Cooldown do Dodge (ms)", "Tempo mínimo entre esquivas (anti-spam).")]
    public RangeNode<int> DodgeCooldownMs { get; set; } = new(1500, 300, 5000);

    [Menu("Perigo por mods (M3)", "Mobs com mods perigosos (explosão/volatile/trilho/aura) que ESTÃO A ATACAR " +
        "contam como mais perigo → esquiva mais cedo. Só amplifica um mob que já está a agir; NUNCA foge de um " +
        "mob parado. Desligado por defeito.")]
    public ToggleNode DangerByMods { get; set; } = new(false);
}

// ── SECÇÃO MODS ────────────────────────────────────────────────────────────────────────────
[Submenu(CollapsedByDefault = true)]
public class ModsSettings
{
    [Menu("Targeting por mod (loot)", "Ao escolher alvo, prioriza raros com bom loot (Unique Exile Drops) e " +
        "despriorize os 'sem loot/xp'. Ajuste LIMITADO — nunca inverte boss>raro>lixo. Desligado por defeito.")]
    public ToggleNode ModTargeting { get; set; } = new(false);

    [Menu("Dump mods perto", "DIAGNÓSTICO: escreve os mods internos dos monstros perto para ficheiro " +
        "(AutoPilot_mods_dump.txt / AutoPilot_modnames.txt). Descobrir os nomes dos mods. Não afeta o combate.")]
    public ButtonNode DumpMods { get; set; } = new();

    [Menu("Tecla do dump de mods", "Faz o dump SEM abrir o menu — carrega esta tecla com o jogo a correr. " +
        "É lida antes da pausa do plugin, por isso o overlay do Core2 não precisa de estar aberto.")]
#pragma warning disable CS0618
    public HotkeyNode DumpModsKey { get; set; } = new(Keys.None);
#pragma warning restore CS0618

    [Menu("Dump automático de mods", "Com o AutoPilot ATIVO, grava os mods sozinho ao aparecer um Rare/Unique perto " +
        "(intervalo ~1.5s). Desliga quando já tiveres os nomes.")]
    public ToggleNode AutoDumpMods { get; set; } = new(false);
}

// ── SECÇÃO LOGS ────────────────────────────────────────────────────────────────────────────
[Submenu(CollapsedByDefault = true)]
public class LogsSettings
{
    [Menu("Mostrar texto no ecrã", "Desenha no ecrã o modo de combate, alvo, animação e buffs (afinar timings). " +
        "Desliga para tirar o texto de cima do jogo — os logs em ficheiro continuam se 'Gravar logs' estiver ligado.")]
    public ToggleNode ShowDebug { get; set; } = new(false);

    [Menu("Gravar logs (ficheiro)", "Escreve o diagnóstico para AutoPilot_debug.txt / AutoPilot_actions.txt, " +
        "SEM desenhar nada no ecrã. Deixa ligado para apanhar erros sem o texto a tapar o jogo.")]
    public ToggleNode WriteLogs { get; set; } = new(false);

    [Menu("Gravar Baseline (Fase 2)", "SÓ com a rotina Ice Shot: grava a sequência de teclas por cenário " +
        "(pack/rare/boss). Liga, combate cada cenário, desliga. Não afeta o combate.")]
    public ToggleNode RecordBaseline { get; set; } = new(false);
}

// ── SECÇÃO PERFIL — ImGui custom ───────────────────────────────────────────────────────────
[Submenu(RenderMethod = nameof(Render))]
public class ProfileSettings
{
    // Conteúdo desenhado em ImGui custom (estilo PickIt). Ver AutoPilotPlugin.DrawProfilePanel.
    public void Render() => AutoPilotPlugin.Main?.DrawProfilePanel();
}

// ── SECÇÃO SKILLS ──────────────────────────────────────────────────────────────────────────
[Submenu(CollapsedByDefault = true)]
public class SkillsSettings
{
    [Menu("Re-detetar Teclas", "Limpa e volta a atribuir automaticamente as teclas de todas as skills.")]
    public ButtonNode RedetectKeys { get; set; } = new();

    [Menu("Skills detetadas", "Skills da barra. Cada uma tem tecla (auto), prioridade e ms de uso.")]
    public ContentNode<SkillSlot> Content { get; set; } = new()
    {
        EnableItemCollapsing = true,
        EnableControls = false,
    };
}

// ── SECÇÃO ICE SHOT (condicional) ──────────────────────────────────────────────────────────
[Submenu(CollapsedByDefault = true)]
public class IceShotSettings
{
    [Menu("Min Salvo Seals", "Seals mínimos (BuffCharges de skill_seals) antes de usar Freezing Salvo.")]
    public RangeNode<int> MinSalvoSeals { get; set; } = new(10, 1, 20);

    [Menu("Snipe em Raros (quando frozen)", "Usa o combo Barrage→Snipe em Raros frozen.")]
    public ToggleNode UseSnipeOnRares { get; set; } = new(true);

    [Menu("Tornado CD no Boss (ms)", "Cooldown interno do Tornado Shot durante combate com Boss.")]
    public RangeNode<int> TornadoBossCooldownMs { get; set; } = new(4000, 1000, 15000);

    [Menu("Barrage Commit (ms)", "Tempo que o Barrage tem para acabar a animação ANTES de o Snipe entrar. " +
        "Se o Snipe corta o Barrage (sem dano), AUMENTA. Se o Snipe demora, reduz.")]
    public RangeNode<int> BarrageCommitMs { get; set; } = new(400, 0, 1500);
}

// ── SECÇÃO STAFF (condicional) ─────────────────────────────────────────────────────────────
[Submenu(CollapsedByDefault = true)]
public class StaffSettings
{
    [Menu("Manter Charged Staff", "Reaplica o Charged Staff sempre que o buff cai (coração da build).")]
    public ToggleNode MaintainChargedStaff { get; set; } = new(true);

    [Menu("Min Power Charges", "Killing Palm dispara para repor cargas quando estás ABAIXO deste número.")]
    public RangeNode<int> MinPowerCharges { get; set; } = new(3, 0, 10);

    [Menu("Usar Rend (burst)", "Reaplica Rend em boss/raros para o buff de dano. Desliga para rares rápidos.")]
    public ToggleNode UseRend { get; set; } = new(true);

    [Menu("Usar Falling Thunder", "Dispara o Falling Thunder na janela ótima: Charged Staff ATIVO e Power Charges cheias.")]
    public ToggleNode UseFallingThunder { get; set; } = new(true);

    [Menu("Falling Thunder: charges", "Quantas Power Charges (cheias) para o Falling Thunder disparar. Ex.: 5.")]
    public RangeNode<int> FallingThunderCharges { get; set; } = new(5, 1, 10);

    [Menu("Usar Hollow Form (boss)", "Ativa Hollow Form na abertura do boss.")]
    public ToggleNode UseHollowForm { get; set; } = new(true);

    [Menu("Duração do sino (ms)", "Tempo estimado que o Tempest Bell dura antes de ser reposto (o 'loop' do boss).")]
    public RangeNode<int> TempestBellDurationMs { get; set; } = new(6000, 1000, 20000);
}

// ── SECÇÃO DUREZA (HP_ROTATION) ─────────────────────────────────────────────────────────────
// Calibração da classificação de dureza do alvo. score = (MaxHP+MaxES) / mediana-de-Rares-da-zona.
// Um Rare MEDIANO dá score ~1.0 (a mediana é a vida típica). 2 limiares separam Easy/Medium/Tank.
// Só afeta o motor "Geral" (cada skill tem 'Dureza mínima'). Afina com a linha 'dureza:' do debug log.
[Submenu(CollapsedByDefault = true)]
public class HardnessSettings
{
    [Menu("Limiar TANK", "score >= isto → TANK (o mais duro; combo). Um Rare mediano = ~1.0, por isso " +
        "2.5 = só Rares ~2.5x mais gordos que o típico. Baixa para apanhar mais como Tank.")]
    public RangeNode<float> LimiarTank { get; set; } = new(2.5f, 1.1f, 8f);

    [Menu("Limiar MEDIUM", "score >= isto → MEDIUM (+Barrage/Tornado). 1.5 = Rares ~1.5x o típico. " +
        "Abaixo disto = EASY. Tem de ser < Limiar TANK.")]
    public RangeNode<float> LimiarMedium { get; set; } = new(1.5f, 0.5f, 5f);

    [Menu("Min amostras p/ mediana real", "Quantos Rares amostrar numa área antes de usar a mediana REAL. " +
        "Abaixo disto usa a mediana SINTÉTICA (sliders abaixo). Mais = baseline mais fiável, arranque mais lento.")]
    public RangeNode<int> MinAmostras { get; set; } = new(8, 1, 30);

    [Menu("Cold-start: ajuste da estimativa", "ARRANQUE (antes de haver Rares amostrados): a vida típica é " +
        "ESTIMADA pelo NÍVEL DA ÁREA (fórmula PoE2: vida-base × 8 do Rare). Adapta-se ao tier sozinha. " +
        "Este fator multiplica a estimativa para cobrir o juice/ES da TUA zona (1.0 = fórmula pura; sobe se " +
        "os teus mapas são mais 'gordos' que o normal). Compara 'pool=' com 'med=' no log e afina.")]
    public RangeNode<float> ColdStartFactor { get; set; } = new(1.0f, 0.3f, 4f);

    [Menu("Ajuste por mod chato", "Quanto SOMA ao score se o alvo tem um mod que o torna mais duro de matar " +
        "(regenera, revive, reduz dano). Empurra-o para um nível acima sem mudar a vida.")]
    public RangeNode<float> AjusteModPorMatch { get; set; } = new(0.5f, 0f, 3f);
}
