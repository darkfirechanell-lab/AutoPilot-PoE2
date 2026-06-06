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
    // ADAPTATIVO: caminho dado pelo plugin (ConfigDirectory). Fallback ao dir atual se não definido.
    private static string FixedPath = "AutoPilot_actions.txt";
    public static void SetDir(string dir) => FixedPath = System.IO.Path.Combine(dir ?? ".", "AutoPilot_actions.txt");
    private const int MaxLines = 600;

    private static readonly Queue<string> _history = new();
    private static long _lastFlushTicks;
    private const int FlushMs = 1500; // grava em disco no máx. ~1x/1.5s (PERF: menos I/O no thread do jogo)
    private static volatile bool _writing; // evita escritas concorrentes sobrepostas

    public static bool Enabled { get; set; }

    /// <summary>
    /// Distância ao alvo atual (grid), atualizada pelo plugin a cada tick. O Action() inclui-a em cada
    /// TAP/HOLD, para se poder MEDIR a que distância cada skill dispara (descobrir o range real por
    /// skill, ex.: após trocar de arco). -1 = sem alvo.
    /// </summary>
    public static float CurrentTargetDistance { get; set; } = -1f;

    /// <summary>
    /// Hook opcional para reencaminhar cada ação (TAP/HOLD/RELEASE) a um observador externo — usado
    /// pelo BaselineRecorder (Fase 2). Independente do Enabled do log (o recorder tem o seu toggle).
    /// </summary>
    public static Action<string, Keys> OnAction;

    /// <summary>Regista uma ação de input. <paramref name="kind"/> = TAP/HOLD/RELEASE, com a tecla e nota opcional.</summary>
    public static void Action(string kind, Keys key, string note = null)
    {
        OnAction?.Invoke(kind, key); // reencaminha sempre (o recorder decide se grava)
        if (!Enabled) return;

        var line = $"[{DateTime.Now:mm:ss.fff}] {kind,-11} {key}";
        // Distância ao alvo no momento do disparo (só em TAP/HOLD; RELEASE não interessa para o range).
        if (CurrentTargetDistance >= 0f && (kind == "TAP" || kind == "HOLD"))
            line += $"  dist={CurrentTargetDistance:F0}";
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
        if (_writing && !force) return; // já há escrita em curso
        _lastFlushTicks = now;

        // PERF: snapshot rápido no thread do jogo, I/O em background (não bloqueia frames).
        var sb = new StringBuilder();
        foreach (var l in _history) sb.AppendLine(l);
        var content = sb.ToString();
        _writing = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { File.WriteAllText(FixedPath, content, Encoding.UTF8); }
            catch { /* nunca rebenta o jogo por causa de um log */ }
            finally { _writing = false; }
        });
    }
}
