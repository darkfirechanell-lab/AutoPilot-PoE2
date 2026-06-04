using System.Windows.Forms;
using AutoPilot.Combat;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace AutoPilot.Settings;

/// <summary>
/// Raiz das definições do CombatRoutine. Cada área vive no seu submenu; as skills detetadas
/// aparecem em <see cref="Skills"/> com tecla/prioridade/ms por skill (base da Routine Geral futura).
/// </summary>
public class AutoPilotSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);

    [Menu("Aim Key", "Tecla a manter premida para ativar o aim/combate.")]
    public HotkeyNodeV2 AimKey { get; set; } = new(Keys.None);

    [Menu("Aim Toggle Key", "Tecla que liga/desliga o aim em modo toggle.")]
    public HotkeyNodeV2 AimToggleKey { get; set; } = new(Keys.None);

    [Menu("Mostrar Debug", "Mostra no ecrã o modo de combate, alvo, animação e buffs do alvo (para afinar timings).")]
    public ToggleNode ShowDebug { get; set; } = new(false);

    [Menu("Filtrar por Visibilidade (raycast)", "Ignora mobs atrás de paredes. Se o aim não foca nada, DESLIGA isto para testar se é o raycast.")]
    public ToggleNode UseVisibility { get; set; } = new(true);

    [Menu("Parar com painéis abertos", "Pausa o combate quando abres o inventário/loja/skill tree. Ao fechar, retoma. " +
        "Igual ao AutoMyAim. Se causar problemas ao retomar, desliga.")]
    public ToggleNode PauseOnPanels { get; set; } = new(true);

    [Menu("Attack Range", "Distância máxima ao alvo (unidades de grid).")]
    public RangeNode<float> AttackRange { get; set; } = new(100f, 5f, 600f);

    [Menu("Proximal Tangibility: alcance", "Mobs com o mod 'Proximal Tangibility' são imunes à distância. " +
        "Só são mirados quando estás MAIS PERTO que isto. Se o boss não for atacado quando já estás perto, AUMENTA.")]
    public RangeNode<float> ProximalRange { get; set; } = new(25f, 5f, 100f);

    [Menu("Randomização do Cursor (px)", "Adiciona um offset aleatório pequeno ao cursor (anti-robótico). " +
        "0 = desligado (cursor exato no centro do mob). Valores altos podem fazer falhar mobs pequenos.")]
    public RangeNode<float> CursorJitter { get; set; } = new(0f, 0f, 20f);

    [Menu("Rotina de combate", "Qual rotação de skills usar. 'Ice Shot' = build de arco; 'Staff' = " +
        "build de cajado (Whirling Assault / Tempest Bell / Killing Palm / Charged Staff / Rend).")]
    public ListNode Routine { get; set; } = new()
    {
        Values = new System.Collections.Generic.List<string> { "Ice Shot", "Staff" },
        Value = "Ice Shot",
    };

    [Submenu]
    public CombatSettings Combat { get; set; } = new();

    [Submenu]
    public IceShotSettings IceShot { get; set; } = new();

    [Submenu]
    public StaffSettings Staff { get; set; } = new();

    [Menu("Re-detetar Teclas", "Limpa e volta a atribuir automaticamente as teclas de todas as skills (usa se ficaram erradas).")]
    public ButtonNode RedetectKeys { get; set; } = new();

    [Menu("Skills detetadas", "Skills da barra. Cada uma tem tecla (auto), prioridade e ms de uso.")]
    public ContentNode<SkillSlot> Skills { get; set; } = new()
    {
        EnableItemCollapsing = true,
        EnableControls = false,
    };
}

[Submenu(CollapsedByDefault = true)]
public class CombatSettings
{
    [Menu("Combate ativo", "Liga/desliga o uso de skills (o aim continua a funcionar).")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Cursor Range", "Quão perto o cursor tem de estar do alvo antes de usar skills.")]
    public RangeNode<float> CursorRange { get; set; } = new(20f, 1f, 100f);

    [Menu("C1: Só atacar com cursor no alvo", "Não dispara skills de dano (Ice Shot/Barrage) se o cursor não estiver " +
        "em cima do alvo (ex.: alvo fora do ecrã). Usa a posição que o AIM calcula (sem lag), por isso a tolerância " +
        "pode ser apertada. Não afeta Mark nem canais já a decorrer. Desligado por defeito.")]
    public ToggleNode RequireCursorOnTarget { get; set; } = new(false);

    [Menu("C1: Tolerância (px)", "Distância máxima (px no ecrã) entre o cursor e o centro do alvo para considerar que " +
        "vai acertar. Menor = mais apertado. Só conta se 'Só atacar com cursor no alvo' estiver ligado.")]
    public RangeNode<float> CursorOnTargetTolerance { get; set; } = new(35f, 5f, 150f);
}

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
        "Se o Snipe corta o Barrage (sem dano), AUMENTA. Se o Snipe demora a entrar, reduz. Rede de segurança " +
        "enquanto a deteção automática de fim de animação não está afinada.")]
    public RangeNode<int> BarrageCommitMs { get; set; } = new(400, 0, 1500);
}

[Submenu(CollapsedByDefault = true)]
public class StaffSettings
{
    [Menu("Manter Charged Staff", "Reaplica o Charged Staff sempre que o buff cai (coração da build). " +
        "Desliga só para testar.")]
    public ToggleNode MaintainChargedStaff { get; set; } = new(true);

    [Menu("Min Power Charges", "Killing Palm dispara para repor cargas quando estás ABAIXO deste número. " +
        "(Se as cargas forem ilegíveis, o Killing Palm cai num modo por cooldown — vê a nota A CONFIRMAR no código.)")]
    public RangeNode<int> MinPowerCharges { get; set; } = new(3, 0, 10);

    [Menu("Usar Rend (burst)", "Reaplica Rend em boss/raros para o buff de dano. Desliga para rares rápidos.")]
    public ToggleNode UseRend { get; set; } = new(true);

    [Menu("Usar Hollow Form (boss)", "Ativa Hollow Form na abertura do boss.")]
    public ToggleNode UseHollowForm { get; set; } = new(true);

    [Menu("Duração do sino (ms)", "Tempo estimado que o Tempest Bell dura antes de ser reposto (o 'loop' do boss). " +
        "Ajusta à duração real do teu sino.")]
    public RangeNode<int> TempestBellDurationMs { get; set; } = new(6000, 1000, 20000);
}
