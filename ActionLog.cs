using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AutoPilot;

/// <summary>
/// Registo cronológico de TODAS as ações de input do plugin (TAP / HOLD / RELEASE), uma linha por
/// ação, com timestamp ao milissegundo. Ao contrário do <see cref="DebugLog"/> (que é um snapshot do
/// ESTADO e só escreve quando o estado muda), este log NÃO filtra repetidos — cada disparo aparece,
/// mesmo que seja a mesma tecla 10x seguidas. Serve para ver a sequência exata de teclas que o plugin
/// premiu (ex.: confirmar a ordem Barrage→Snipe, ou ver se o Salvo foi mesmo segurado).
///
/// Escreve em ficheiro próprio (não mistura com o snapshot de estado). Guarda as últimas N entradas.
/// </summary>
public static class ActionLog
{
    private const string FixedPath = @"C:\Users\clona\Desktop\GamePoe\TestePoE\AutoPilot_actions.txt";
    private const int MaxLines = 600;

    private static readonly Queue<string> _history = new();
    private static long _lastFlushTicks;
    private const int FlushMs = 250; // grava em disco no máx. 4x/s; acumula sempre na memória.

    public static bool Enabled { get; set; }

    /// <summary>Regista uma ação de input. <paramref name="kind"/> = TAP/HOLD/RELEASE, com a tecla e nota opcional.</summary>
    public static void Action(string kind, Keys key, string note = null)
    {
        if (!Enabled) return;

        var line = $"[{DateTime.Now:mm:ss.fff}] {kind,-11} {key}";
        if (!string.IsNullOrEmpty(note)) line += $"  ({note})";

        _history.Enqueue(line);
        while (_history.Count > MaxLines) _history.Dequeue();

        Flush(force: false);
    }

    /// <summary>Marca um evento não-input no mesmo fluxo (ex.: alvo perdido, combate parado).</summary>
    public static void Event(string text)
    {
        if (!Enabled) return;
        _history.Enqueue($"[{DateTime.Now:mm:ss.fff}] -- {text}");
        while (_history.Count > MaxLines) _history.Dequeue();
        Flush(force: false);
    }

    private static void Flush(bool force)
    {
        var now = DateTime.UtcNow.Ticks;
        if (!force && (now - _lastFlushTicks) / TimeSpan.TicksPerMillisecond < FlushMs) return;
        _lastFlushTicks = now;

        try
        {
            var sb = new StringBuilder();
            foreach (var l in _history) sb.AppendLine(l);
            File.WriteAllText(FixedPath, sb.ToString(), Encoding.UTF8);
        }
        catch { /* nunca rebenta o jogo por causa de um log */ }
    }
}
