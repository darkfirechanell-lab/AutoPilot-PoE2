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

    [Menu("Tecla", "Tecla a premir para esta skill. Auto-detetada da barra; podes mudar.")]
    public HotkeyNodeV2 Key { get; set; } = new(Keys.None);

    [Menu("Prioridade", "Ordem de avaliação na rotação. MAIOR = avaliada primeiro. (Para a Routine Geral futura.)")]
    public RangeNode<int> Priority { get; set; } = new(0, 0, 100);

    [Menu("Tap Hold (ms)", "Gap entre KeyDown e KeyUp para esta skill. Para skills que precisam de mais tempo a registar.")]
    public RangeNode<int> TapHoldMs { get; set; } = new(12, 1, 200);

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
