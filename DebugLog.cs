using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoPilot;

/// <summary>
/// Regista o estado de combate num ficheiro de texto para diagnóstico fora do jogo.
///
/// ACUMULA um histórico das últimas linhas (não sobrescreve) — assim os momentos de combate ficam
/// guardados mesmo que o estado mude logo a seguir. Só regista quando o conteúdo MUDA (evita encher
/// o histórico com a mesma linha repetida) e guarda as últimas N entradas com timestamp.
/// </summary>
public static class DebugLog
{
    private const string FixedPath = @"C:\Users\clona\Desktop\GamePoe\TestePoE\AutoPilot_debug.txt";
    private const int MaxLines = 400;

    private static readonly Queue<string> _history = new();
    private static string _lastContent = "";
    private static long _lastFlushTicks;
    private const int FlushMs = 2000; // grava em disco no máx. 1x/2s (PERF: menos I/O no thread do jogo)
    private static volatile bool _writing; // evita escritas concorrentes sobrepostas

    public static bool Enabled { get; set; }

    /// <summary>Regista uma entrada SE for diferente da anterior. Grava o histórico em disco (throttled).</summary>
    public static void Write(string content)
    {
        if (!Enabled) return;

        // Só acrescenta ao histórico quando o conteúdo muda (senão repetia a mesma linha 100x).
        if (content != _lastContent)
        {
            _lastContent = content;
            var stamped = $"[{DateTime.Now:mm:ss.fff}] {content.Replace("\n", " | ")}";
            _history.Enqueue(stamped);
            while (_history.Count > MaxLines) _history.Dequeue();
        }

        var now = DateTime.UtcNow.Ticks;
        if ((now - _lastFlushTicks) / TimeSpan.TicksPerMillisecond < FlushMs) return;
        if (_writing) return; // já há uma escrita em curso; espera o próximo flush
        _lastFlushTicks = now;

        // PERF: monta a string no thread do jogo (snapshot rápido) mas faz o I/O em background, para a
        // escrita em disco nunca bloquear um frame (causa de micro-stutter se o disco hesitar).
        var sb = new StringBuilder();
        foreach (var line in _history) sb.AppendLine(line);
        var snapshot = sb.ToString();
        _writing = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { File.WriteAllText(FixedPath, snapshot, Encoding.UTF8); }
            catch { /* nunca rebenta o jogo por causa de um log */ }
            finally { _writing = false; }
        });
    }
}
