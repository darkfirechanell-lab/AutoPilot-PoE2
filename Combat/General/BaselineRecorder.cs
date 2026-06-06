using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AutoPilot.Combat.General;

/// <summary>
/// FASE 2 da Routine Geral: grava a SEQUÊNCIA DE TECLAS do IceShot atual como BASELINE por cenário
/// (pack / rare / boss). É o critério binário contra o qual o motor genérico (Fase 4) será validado:
/// o motor passa se reproduzir a mesma sequência nos mesmos cenários.
///
/// Só grava quando a routine ATIVA é o IceShot (a referência verdadeira), não o motor. Grava num
/// ficheiro por cenário. A sequência é só a ordem das teclas (TAP/HOLD/RELEASE), sem os timestamps
/// absolutos (que variam) — guardamos deltas relativos em ms para se poder comparar timing ±1 tick.
///
/// NÃO afeta o combate: é só escrita de ficheiro, alimentada pelas mesmas ações que o ActionLog.
/// </summary>
public sealed class BaselineRecorder
{
    public enum Scenario { None, Pack, Rare, Boss }

    // ADAPTATIVO: caminho dado pelo plugin (ConfigDirectory). Fallback ao dir atual se não definido.
    private static string Dir = ".";
    public static void SetDir(string dir) => Dir = dir ?? ".";
    private const int MaxLinesPerScenario = 300;
    // Quanto tempo sem ações antes de considerar o "encontro" terminado e gravar em disco.
    private const int FlushIdleMs = 800;

    public bool Enabled { get; set; }

    private Scenario _current = Scenario.None;
    private readonly Dictionary<Scenario, List<string>> _buffers = new();
    private long _lastActionTicks;
    private long _scenarioStartTicks;

    public BaselineRecorder()
    {
        foreach (Scenario s in Enum.GetValues(typeof(Scenario)))
            if (s != Scenario.None) _buffers[s] = new List<string>();
    }

    /// <summary>
    /// Define o cenário atual (chamado pelo plugin a cada tick com o alvo). Ao MUDAR de cenário,
    /// grava em disco o que estava acumulado do cenário anterior (fim de um "encontro").
    /// </summary>
    public void SetScenario(Scenario scenario)
    {
        if (!Enabled) return;
        if (scenario == _current) return;

        // Mudou de cenário → o encontro anterior acabou; grava-o.
        FlushScenario(_current);
        _current = scenario;
        _scenarioStartTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>Regista uma ação de input no baseline do cenário atual (mesma fonte que o ActionLog).</summary>
    public void Record(string kind, Keys key)
    {
        if (!Enabled || _current == Scenario.None) return;
        if (!_buffers.TryGetValue(_current, out var buf)) return;

        var now = DateTime.UtcNow.Ticks;
        var deltaMs = _lastActionTicks == 0 ? 0 : (now - _lastActionTicks) / TimeSpan.TicksPerMillisecond;
        _lastActionTicks = now;

        buf.Add($"+{deltaMs,5}ms {kind,-11} {key}");
        if (buf.Count > MaxLinesPerScenario) buf.RemoveAt(0);
    }

    /// <summary>Grava em disco o baseline de um cenário (se tiver conteúdo).</summary>
    public void FlushScenario(Scenario s)
    {
        if (s == Scenario.None || !_buffers.TryGetValue(s, out var buf) || buf.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"; BASELINE {s} (IceShot) — gravado {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            foreach (var l in buf) sb.AppendLine(l);
            File.WriteAllText(Path.Combine(Dir, $"baseline_{s.ToString().ToLower()}.txt"), sb.ToString());
        }
        catch { /* nunca rebenta o jogo por causa de um baseline */ }
    }

    /// <summary>Grava tudo o que está pendente (ex.: ao parar o combate).</summary>
    public void FlushAll()
    {
        foreach (Scenario s in _buffers.Keys)
            FlushScenario(s);
    }
}
