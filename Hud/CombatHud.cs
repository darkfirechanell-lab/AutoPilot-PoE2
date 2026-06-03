using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using AutoPilot.Combat;
using AutoPilot.Detection;
using AutoPilot.Targeting;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.Shared.Enums;
using Graphics = ExileCore2.Graphics;

namespace AutoPilot.Hud;

/// <summary>
/// Desenho do plugin: marca o alvo atual e, em modo debug, mostra um painel com tudo o que é preciso
/// para AFINAR a routine sem adivinhar:
///   • o modo de combate ativo (Danger/Elite/Normal) e o alvo;
///   • a linha de animação ao vivo (id/stage/progress/action) — para descobrir que sinal do Barrage
///     é fiável (o stage fica 0; vê qual dos outros mexe);
///   • os BUFFS do alvo ao vivo — para confirmar nomes (ex.: o debuff do Tornado, provável "blind").
///
/// Tudo isto é leitura/desenho; não altera estado de combate.
/// </summary>
public sealed class CombatHud
{
    public void Render(
        GameController gc,
        Graphics graphics,
        bool showDebug,
        CombatMode mode,
        TrackedEntity target,
        AnimationReader animation,
        TargetSelector targets,
        string shortcutsDebug = null)
    {
        // Círculo no alvo atual (sempre que houver alvo).
        if (target?.Entity != null)
        {
            var screen = gc.IngameState.Camera.WorldToScreen(target.Entity.Pos);
            if (screen != Vector2.Zero)
                graphics.DrawCircle(screen, 14f, ModeColor(mode), 2f, 24);
        }

        if (!showDebug) return;

        // Painel de debug no canto superior esquerdo.
        var pos = new Vector2(12, 120);
        var lines = new List<string>
        {
            $"[CombatRoutine] modo: {mode}",
            target?.Entity != null
                ? $"alvo: {Rarity(target.Entity)}  dist={target.Distance:F0}  peso={target.Weight:F1}"
                : "alvo: (nenhum)",
            $"mobs: total={targets.DiagTotal} c/peso={targets.DiagWithWeight} visiveis={targets.DiagVisible} maisperto={targets.DiagNearestDist:F0}",
            $"player {animation.DebugLine()}",
        };

        // Buffs do alvo — confirmar nomes (blind do Tornado, frozen, mark, etc.).
        if (target?.Entity != null)
        {
            var buffs = ReadBuffNames(target.Entity);
            lines.Add(buffs.Count > 0 ? $"alvo buffs: {string.Join(", ", buffs)}" : "alvo buffs: (nenhum)");
        }

        // Buffs do jogador — seals do Salvo, ice-tipped, mark buff, etc.
        var playerBuffs = ReadBuffNames(gc.Player);
        if (playerBuffs.Count > 0)
            lines.Add($"player buffs: {string.Join(", ", playerBuffs)}");

        // Shortcuts crus + skills detetadas — para alinhar as teclas.
        if (!string.IsNullOrEmpty(shortcutsDebug))
            foreach (var dl in shortcutsDebug.Split('\n'))
                lines.Add(dl);

        foreach (var line in lines)
        {
            graphics.DrawTextWithBackground(line, pos, Color.White, FontAlign.Left, Color.FromArgb(180, 0, 0, 0));
            pos.Y += 18;
        }
    }

    private static List<string> ReadBuffNames(ExileCore2.PoEMemory.MemoryObjects.Entity entity)
    {
        var names = new List<string>();
        try
        {
            if (entity != null && entity.TryGetComponent<Buffs>(out var buffs) && buffs?.BuffsList != null)
                names = buffs.BuffsList
                    .Where(b => !string.IsNullOrEmpty(b?.Name))
                    .Select(b => b.Name)
                    .Distinct()
                    .Take(12)
                    .ToList();
        }
        catch { }
        return names;
    }

    private static string Rarity(ExileCore2.PoEMemory.MemoryObjects.Entity e)
    {
        try { return e.Rarity.ToString(); } catch { return "?"; }
    }

    private static Color ModeColor(CombatMode mode) => mode switch
    {
        CombatMode.Danger => Color.FromArgb(230, 255, 60, 60),
        CombatMode.Elite => Color.FromArgb(230, 255, 200, 0),
        _ => Color.FromArgb(230, 60, 200, 255),
    };
}
