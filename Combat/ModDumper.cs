using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using AutoPilot.Detection;

namespace AutoPilot.Combat;

/// <summary>
/// M0 (plano dos mods): DUMP dos mods internos dos monstros perto, para DESCOBRIR os nomes que se
/// podem matchar por regex — ANTES de construir qualquer lógica em cima deles (o dump é o GATE de
/// validação dos dados, ver MODS_PLANO.md). Espelha o "Dump nearby mods" do RareModScanner e o padrão
/// do nosso dump de buffs (AutoPilot_buffnames.txt).
///
/// Leitura DIRETA (sem cache — a cache só entra na M1, depois de o gate provar que vale a pena):
/// `entity.GetComponent&lt;ObjectMagicProperties&gt;()?.Mods`.
///
/// Escreve dois ficheiros:
///   • AutoPilot_mods_dump.txt   — snapshot do momento do clique: cada monstro perto + raridade + mods.
///   • AutoPilot_modnames.txt    — ACUMULA cada nome de mod único já visto (com a raridade onde apareceu).
/// É só leitura/escrita de ficheiro; NÃO toca no combate.
/// </summary>
public static class ModDumper
{
    private const string Dir = @"C:\Users\clona\Desktop\GamePoe\TestePoE";

    public static string LastMessage { get; private set; } = "";

    // Auto-dump: garante que não gravamos o ficheiro dezenas de vezes por segundo durante o combate.
    private static DateTime _lastAutoDump = DateTime.MinValue;

    /// <summary>
    /// AUTO-dump durante o combate: chamado todos os ticks com o AutoPilot ativo. Só faz o dump se
    /// houver pelo menos um monstro Rare ou Unique perto E se passou o intervalo mínimo (anti-spam de
    /// ficheiro). Assim, ao aparecer um raro, os mods ficam gravados sozinhos — sem carregar em nada.
    /// </summary>
    public static void AutoDump(EntityCache entities, int minIntervalMs = 600)
    {
        if (entities == null) return;
        // Há algum Rare/Unique no snapshot? (só esses interessam para o gate do M0)
        var hasRarePlus = false;
        foreach (var m in entities.Monsters)
            if (m.Rarity == MonsterRarity.Rare || m.Rarity == MonsterRarity.Unique) { hasRarePlus = true; break; }
        if (!hasRarePlus) return;

        if ((DateTime.UtcNow - _lastAutoDump).TotalMilliseconds < minIntervalMs) return;
        _lastAutoDump = DateTime.UtcNow;
        Dump(entities);
    }

    /// <summary>Faz o dump dos mods dos monstros do snapshot atual. Chamado pelo botão/hotkey/auto.</summary>
    public static void Dump(EntityCache entities)
    {
        try
        {
            var snapshot = new StringBuilder();
            snapshot.AppendLine($"; DUMP de mods — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            var accumulatedNew = new List<string>();
            int monsters = 0;

            foreach (var m in entities?.Monsters ?? new List<TrackedEntity>())
            {
                var e = m.Entity;
                if (e == null) continue;
                var mods = GetMods(e);
                monsters++;

                var name = Safe(() => e.RenderName) ?? "?";
                snapshot.AppendLine($"[{m.Rarity}] {name} dist={m.Distance:F0}  mods={(mods.Count == 0 ? "(nenhum)" : string.Join(", ", mods))}");

                // Acumula cada nome de mod novo (com a raridade onde apareceu).
                foreach (var mod in mods)
                    if (AccumulateModName(m.Rarity.ToString(), mod))
                        accumulatedNew.Add($"{m.Rarity}:{mod}");
            }

            File.WriteAllText(Path.Combine(Dir, "AutoPilot_mods_dump.txt"), snapshot.ToString(), Encoding.UTF8);
            LastMessage = $"Dump: {monsters} monstros, {accumulatedNew.Count} mods novos. Ver AutoPilot_mods_dump.txt / AutoPilot_modnames.txt";
        }
        catch (Exception ex)
        {
            LastMessage = $"Dump falhou: {ex.Message}";
        }
    }

    /// <summary>Leitura DIRETA dos mods internos de um monstro. Lista vazia em erro.</summary>
    private static List<string> GetMods(Entity entity)
    {
        try
        {
            return entity.GetComponent<ObjectMagicProperties>()?.Mods?.ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    // Acumula nomes de mod únicos (raridade:mod) num ficheiro à parte, com a 1ª hora a que apareceu.
    private static readonly HashSet<string> _seen = new();
    private static bool AccumulateModName(string rarity, string mod)
    {
        var key = $"{rarity}:{mod}";
        if (!_seen.Add(key)) return false;
        try
        {
            File.AppendAllText(Path.Combine(Dir, "AutoPilot_modnames.txt"),
                $"[{DateTime.Now:mm:ss.fff}] {key}\n", Encoding.UTF8);
        }
        catch { }
        return true;
    }

    private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
}
