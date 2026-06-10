using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Nodes;
using static ExileCore2.Shared.Nodes.HotkeyNodeV2;

namespace AutoPilot.Combat;

/// <summary>
/// Deteta as skills equipadas na barra e atribui a tecla automaticamente, lendo o componente Actor
/// do jogador (o SkillBar da UI está partido nesta patch — ver memória skill-detection-actorskill-signals).
///
/// Sinais nativos usados:
///   • ActorSkill.IsOnSkillBar   → quais as skills realmente na barra (tier 1).
///   • ActorSkill.SkillSlotIndex → o slot (0-12) de cada skill.
///   • ShortcutSettings.Shortcuts→ a tecla (Usage "Skill{n}" → MainKey) de cada slot.
///
/// Com fallbacks em cascata se a patch partir IsOnSkillBar. Re-liga a ref viva (Live) todos os ticks
/// porque o endereço da ActorSkill muda; só preenche a tecla se o utilizador ainda não a definiu.
/// </summary>
public sealed class SkillDetector
{
    private readonly GameController _gc;
    private int _lastHash;


    // Skills inerentes (todo o personagem tem) a esconder da lista de combate.
    private static readonly HashSet<string> Inherent = new(StringComparer.OrdinalIgnoreCase)
    {
        "enforced_walking", "dodge_roll", "player_melee_bow", "player_melee_unarmed", "do_nothing",
    };

    public SkillDetector(GameController gameController)
    {
        _gc = gameController;
    }

    // S1 (SKILL_SYNC_PLANO): CACHE da lista 'equipped' (validada pela S0: objetos sobrevivem entre ticks,
    // trocas-com-invalidos=0). O ResolveEquipped (caro, ~0.5ms) corria 2x/tick (Sync + RelinkLive). Agora
    // só corre quando um HASH BARATO das ActorSkills cruas muda (count + Id2 dos slots), que cobre
    // G/weapon-swap/troca de gema. Entre mudanças, reusa-se a lista cacheada → 0 ResolveEquipped no
    // hot-path quando nada mudou.
    private List<ActorSkill> _equippedCache;
    private int _cheapHash = int.MinValue;

    /// <summary>
    /// Devolve a lista de skills equipadas, REUSANDO a cache se as skills cruas não mudaram (hash barato).
    /// Só faz o ResolveEquipped (caro) quando o hash barato muda. É a fonte única de 'equipped' por tick.
    /// </summary>
    private List<ActorSkill> GetEquippedCached(IList<ActorSkill> actorSkills)
    {
        if (actorSkills == null) return _equippedCache ?? new List<ActorSkill>();

        var cheap = CheapHash(actorSkills);
        if (_equippedCache != null && cheap == _cheapHash)
            return _equippedCache; // nada mudou → reusa (sem ResolveEquipped).

        _cheapHash = cheap;
        _equippedCache = ResolveEquipped(actorSkills); // mudou → re-resolve (caro, mas raro).
        return _equippedCache;
    }

    /// <summary>
    /// Hash BARATO das ActorSkills cruas: nº de skills + Id2 de cada (sem LINQ pesado, sem GroupBy/OrderBy).
    /// Muda quando uma skill é adicionada/removida/trocada (G, weapon-swap, troca de gema). É o gatilho do
    /// re-resolve. NÃO inclui shortcuts — a mudança de tecla é tratada à parte no Sync (force).
    /// </summary>
    private static int CheapHash(IList<ActorSkill> actorSkills)
    {
        var h = 17;
        foreach (var ak in actorSkills)
        {
            try
            {
                if (ak == null || ak.Address == 0) continue;
                h = h * 31 + (int)ak.Id2 + (SlotOf(ak) << 8);
            }
            catch { }
        }
        return h;
    }

    /// <summary>
    /// Sincroniza a lista de <see cref="SkillSlot"/> com as skills equipadas. Adiciona novas,
    /// remove as que saíram, re-liga a ref viva. Idempotente via hash — só trabalha se algo mudou.
    /// </summary>
    public void Sync(List<SkillSlot> slots, bool force = false)
    {
        var actor = _gc?.Player?.GetComponent<Actor>();
        var actorSkills = actor?.ActorSkills;
        if (actorSkills == null) return;

        // S1: o botão "Re-detetar Teclas" (force) invalida a cache → força o re-resolve do zero.
        if (force) _cheapHash = int.MinValue;

        var shortcuts = _gc?.IngameState?.ShortcutSettings?.Shortcuts;
        var equipped = GetEquippedCached(actorSkills); // S1: reusa a cache se as skills cruas não mudaram.

        var hash = ComputeHash(equipped, shortcuts);
        if (!force && hash == _lastHash) { RelinkLive(slots, equipped); return; }
        _lastHash = hash;

        // Remove slots cuja skill já não existe; re-liga ref e preenche dados em falta.
        for (var i = slots.Count - 1; i >= 0; i--)
        {
            var match = equipped.FirstOrDefault(ak => ak.Name == slots[i].Name);
            if (match == null) { slots.RemoveAt(i); continue; }

            slots[i].Live = match;
            if (string.IsNullOrEmpty(slots[i].InternalName)) slots[i].InternalName = match.InternalName ?? "";
            if (string.IsNullOrEmpty(slots[i].DisplayName)) slots[i].DisplayName = DisplayNameOf(match);
            if (slots[i].Key.Value.Key == Keys.None)
            {
                var auto = AutoKey(shortcuts, match);
                if (auto != Keys.None) slots[i].Key.Value = new HotkeyNodeValue(auto);
            }
        }

        // Adiciona skills novas com a tecla já atribuída.
        foreach (var ak in equipped)
        {
            if (slots.Any(s => s.Name == ak.Name)) continue;
            var auto = AutoKey(shortcuts, ak);
            slots.Add(new SkillSlot
            {
                Live = ak,
                Name = ak.Name,
                InternalName = ak.InternalName ?? "",
                DisplayName = DisplayNameOf(ak),
                Key = { Value = new HotkeyNodeValue(auto) },
            });
        }
    }

    /// <summary>Re-liga só a ref viva (rápido, todos os ticks) sem mexer na lista.</summary>
    public void RelinkLive(List<SkillSlot> slots, List<ActorSkill> equipped = null)
    {
        // S1: usa a cache (só re-resolve se as skills cruas mudaram) em vez de re-resolver a cada tick.
        equipped ??= GetEquippedCached(_gc?.Player?.GetComponent<Actor>()?.ActorSkills);
        if (equipped == null) return;

        foreach (var slot in slots)
        {
            if (string.IsNullOrEmpty(slot.Name)) continue;
            // Entre duplicadas (mesmo nome, weapon-set 64/65), prefere a usável.
            ActorSkill best = null;
            foreach (var ak in equipped)
            {
                if (ak.Name != slot.Name) continue;
                best ??= ak;
                try { if (ak.CanBeUsed) { best = ak; break; } } catch { }
            }
            if (best != null) slot.Live = best;
        }
    }

    // ── Internos ───────────────────────────────────────────────────────────────────────────

    private List<ActorSkill> ResolveEquipped(IList<ActorSkill> actorSkills)
    {
        if (actorSkills == null) return new List<ActorSkill>();

        bool Valid(ActorSkill ak) => ak != null && ak.Address != 0 && !string.IsNullOrEmpty(ak.Name);

        // Tier 1: o core diz quais estão na barra.
        var bar = actorSkills.Where(ak => Valid(ak) && SafeOnBar(ak)).ToList();

        // Tier 2: IsOnSkillBar partido → slot válido + nome "...Player" + não inerente.
        if (bar.Count == 0)
            bar = actorSkills.Where(ak => Valid(ak)
                && ak.Name.EndsWith("Player", StringComparison.Ordinal)
                && !Inherent.Contains(ak.InternalName)
                && SlotOf(ak) >= 0).ToList();

        // Colapsa duplicadas por Id2 (os 2 weapon sets partilham Id2). CRÍTICO: preferir a instância
        // com SLOT VÁLIDO (>=0) — senão apanhávamos a cópia sem slot (SkillSlotIndex=-1) e a tecla
        // saía errada/lixo (era o bug do "Ice Shot slot-1 -> RButton"). Depois desempata por usável.
        return bar
            .GroupBy(ak => ak.Id2 != 0 ? $"id2:{ak.Id2}" : $"name:{ak.Name}")
            .Select(g => g
                .OrderByDescending(a => SlotOf(a) >= 0)   // 1º: tem slot válido
                .ThenByDescending(SafeCanBeUsed)          // 2º: usável
                .ThenBy(a => a.Name.Length)               // 3º: nome mais curto (a mãe, não o proc)
                .First())
            .ToList();
    }

    /// <summary>
    /// Listagem crua dos shortcuts de skill para debug: Usage → MainKey (nome e valor int).
    /// Serve para descobrir como o jogo guarda os botões do rato (LMB/MMB/RMB) nesta patch.
    /// </summary>
    public string DebugShortcuts()
    {
        var shortcuts = _gc?.IngameState?.ShortcutSettings?.Shortcuts;
        if (shortcuts == null) return "shortcuts: null";
        var parts = new List<string>();
        foreach (var sc in shortcuts)
        {
            var u = sc.Usage.ToString();
            if (!u.StartsWith("Skill")) continue;
            parts.Add($"{u}={sc.MainKey}({(int)sc.MainKey})");
        }
        return parts.Count > 0 ? string.Join("  ", parts) : "sem skill shortcuts";
    }

    /// <summary>
    /// Por skill detetada: nome curto + slot que reporta + tecla resolvida. Revela se o desalinhamento
    /// é do slot (cada skill reporta um slot errado) ou da conversão de tecla.
    /// </summary>
    public string DebugSkillSlots(List<SkillSlot> slots)
    {
        if (slots == null || slots.Count == 0) return "skills: (nenhuma)";
        var actorSkills = _gc?.Player?.GetComponent<Actor>()?.ActorSkills;
        var parts = new List<string>();
        foreach (var s in slots)
        {
            // Mesma escolha do ResolveEquipped: a instância com slot válido (não qualquer uma).
            var ak = actorSkills?
                .Where(a => a != null && a.Name == s.Name)
                .OrderByDescending(a => SlotOf(a) >= 0)
                .FirstOrDefault();
            var slot = ak != null ? SlotOf(ak) : -1;
            var shortName = (s.DisplayName ?? s.Name);
            if (shortName.Length > 10) shortName = shortName.Substring(0, 10);
            parts.Add($"{shortName}:slot{slot}->{s.Key.Value.Key}");
        }
        return string.Join("  ", parts);
    }

    /// <summary>
    /// Mapa cru SLOT → SKILL para cada índice 0..12, como o jogo o reporta. Cruza-se com os
    /// shortcuts Skill{n} para descobrir o offset/correspondência real (a chave do alinhamento).
    /// </summary>
    public string DebugSlotMap()
    {
        var actorSkills = _gc?.Player?.GetComponent<Actor>()?.ActorSkills;
        if (actorSkills == null) return "slotmap: null";
        var parts = new List<string>();
        for (var slot = 0; slot <= 12; slot++)
        {
            var ak = actorSkills.FirstOrDefault(a => a != null && SlotOf(a) == slot && !string.IsNullOrEmpty(a.Name));
            if (ak == null) continue;
            var n = ak.Name ?? "";
            // Tira o sufixo "Player" para caber e ler melhor.
            if (n.EndsWith("Player")) n = n.Substring(0, n.Length - 6);
            if (n.Length > 9) n = n.Substring(0, 9);
            parts.Add($"s{slot}={n}");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : "slotmap vazio";
    }

    private static bool SafeOnBar(ActorSkill ak) { try { return ak.IsOnSkillBar; } catch { return false; } }
    private static bool SafeCanBeUsed(ActorSkill ak) { try { return ak.CanBeUsed; } catch { return false; } }

    private static int SlotOf(ActorSkill ak)
    {
        try { var s = ak.SkillSlotIndex; return s is >= 0 and <= 12 ? s : -1; }
        catch { return -1; }
    }

    // Desfasamento entre o ActorSkill.SkillSlotIndex e o número "Skill{n}" dos shortcuts.
    // Confirmado empiricamente no PoE2 (slot map vs teclas reais): slot 3 → Skill5, slot 4 → Skill6...
    // ou seja Skill{slot + 2}. Os 2 primeiros índices de slot são skills temporárias/utility que não
    // entram na numeração Skill{n} da barra de teclas. (Antes usava-se +1 e as teclas saíam trocadas.)
    private const int SlotToShortcutOffset = 2;

    private static Keys AutoKey(IList<GameOffsets2.Shortcut> shortcuts, ActorSkill ak)
    {
        try
        {
            if (shortcuts == null) return Keys.None;
            var slot = SlotOf(ak);
            if (slot < 0) return Keys.None;

            var usage = $"Skill{slot + SlotToShortcutOffset}";
            foreach (var sc in shortcuts)
            {
                if (sc.Usage.ToString() != usage) continue;
                return ConsoleKeyToKeys(sc.MainKey);
            }
        }
        catch { }
        return Keys.None;
    }

    /// <summary>
    /// Converte o ConsoleKey do jogo (campo MainKey do Shortcut) para System.Windows.Forms.Keys.
    /// O ConsoleKey NÃO tem valores para botões do rato — o jogo guarda-os como valores fora do enum
    /// (ConsoleKey é um int por baixo). Tratamos os botões do rato pelo valor numérico cru e o resto
    /// por nome (Q, D1, ...). Sem isto, skills no rato (LMB/MMB/RMB) ficavam com a tecla errada.
    /// </summary>
    public static Keys ConsoleKeyToKeys(ConsoleKey mainKey)
    {
        // Botões do rato: o jogo usa valores numéricos próprios (observados nos shortcuts do PoE2).
        // O ConsoleKey não os nomeia, por isso comparamos pelo int cru.
        switch ((int)mainKey)
        {
            case 1: return Keys.LButton;   // Left mouse
            case 2: return Keys.RButton;   // Right mouse
            case 4: return Keys.MButton;   // Middle mouse
            case 5: return Keys.XButton1;  // Mouse 4
            case 6: return Keys.XButton2;  // Mouse 5
        }

        // Teclado: o nome do ConsoleKey costuma bater com o de Keys (Q→Q, D1→D1, F1→F1...).
        if (Enum.TryParse(typeof(Keys), mainKey.ToString(), out var parsed) && parsed is Keys k)
            return k;

        return Keys.None;
    }

    private static string DisplayNameOf(ActorSkill ak)
    {
        try
        {
            var d = ak?.EffectsPerLevel?.GrantedEffect?.ActiveSkill?.DisplayName;
            if (!string.IsNullOrWhiteSpace(d)) return d;
        }
        catch { }

        var internalName = ak?.InternalName;
        if (!string.IsNullOrWhiteSpace(internalName))
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(internalName.Replace('_', ' '));

        return ak?.Name ?? "";
    }

    private static int ComputeHash(List<ActorSkill> equipped, IList<GameOffsets2.Shortcut> shortcuts)
    {
        var hash = equipped.Count;
        foreach (var ak in equipped) hash = hash * 31 + ak.Id2 + (SlotOf(ak) << 8);
        if (shortcuts != null) foreach (var s in shortcuts) hash = hash * 17 + (int)s.MainKey;
        return hash;
    }
}
