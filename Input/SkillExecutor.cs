using System;
using System.Windows.Forms;

namespace AutoPilot.Input;

/// <summary>
/// Camada de intenção de skill por cima da <see cref="InputQueue"/>. As routines não tocam
/// na fila diretamente — pedem ao executor "dá um tap nesta tecla" ou "começa a segurar esta".
/// Mantém o vocabulário do combate (tap / hold / channel) separado da mecânica de teclas.
///
/// Os três modos cobrem tudo o que as skills do PoE2 precisam:
///   • Tap     — skills instantâneas (Ice Shot, Barrage, Mark). KeyDown+KeyUp com gap curto.
///   • Hold    — segurar até confirmação externa (Freezing Salvo até os seals baixarem).
///   • Channel — caso especial de hold para canalização longa (Snipe até ao stage de release).
///               Tecnicamente igual a Hold; o nome existe para a routine ler melhor.
/// </summary>
public sealed class SkillExecutor
{
    private readonly InputQueue _input;

    // FREIO GLOBAL DE INPUT (modelo ReAgent: GlobalKeyPressCooldown): o motor não INICIA uma ação de tecla
    // (tap ou começo de um hold novo) mais de 1x a cada GlobalCooldownMs. Mata o spam de raiz — ex.: o
    // Tornado tentava re-iniciar o hold a cada ~30ms; com o freio, no máximo a cada GlobalCooldownMs.
    // NÃO afeta MANTER um hold já a decorrer (a mesma tecla continua premida sem re-iniciar).
    private long _lastStartTicks;
    public int GlobalCooldownMs { get; set; } = 150;

    public SkillExecutor(InputQueue input)
    {
        _input = input;
    }

    /// <summary>True se ainda não passou o freio global desde a última ação de início de tecla.</summary>
    private bool GlobalCooldownActive()
    {
        var sinceMs = (DateTime.UtcNow.Ticks - _lastStartTicks) / TimeSpan.TicksPerMillisecond;
        return _lastStartTicks != 0 && sinceMs < GlobalCooldownMs;
    }

    private void MarkStart() => _lastStartTicks = DateTime.UtcNow.Ticks;

    /// <summary>Há uma tecla a ser segurada (hold ou channel) agora?</summary>
    public bool IsHolding => _input.IsHolding;

    /// <summary>A tecla atualmente segurada, ou Keys.None.</summary>
    public Keys HeldKey => _input.HeldKey;

    /// <summary>
    /// Dispara um tap discreto. <paramref name="tapHoldMs"/> permite ajustar o gap por skill —
    /// é exatamente o "ms de uso por skill" que a Routine Geral vai expor na UI mais tarde.
    /// </summary>
    public void Tap(Keys key, int tapHoldMs = InputQueue.DefaultTapHoldMs)
    {
        if (key == Keys.None) return;
        if (GlobalCooldownActive()) return; // freio global: não preme cedo demais (anti-spam).
        MarkStart();
        ActionLog.Action("TAP", key, $"hold={tapHoldMs}ms");
        _input.Tap(key, tapHoldMs);
    }

    /// <summary>Começa/mantém a segurar a tecla. Idempotente (ver <see cref="InputQueue.Hold"/>).</summary>
    public void Hold(Keys key)
    {
        if (key == Keys.None) return;
        var arrancar = _input.HeldKey != key; // só é "início" se a tecla MUDA (não ao manter a mesma).
        if (arrancar)
        {
            if (GlobalCooldownActive()) return; // freio global: não INICIA um hold novo cedo demais.
            MarkStart();
            ActionLog.Action("HOLD", key);
        }
        _input.Hold(key);
    }

    /// <summary>
    /// Inicia/mantém uma canalização. Idêntico a <see cref="Hold"/> na mecânica; nome distinto
    /// para a intenção ser clara no código das routines (ex.: Snipe).
    /// </summary>
    public void Channel(Keys key) => Hold(key);

    /// <summary>Liberta a tecla segurada (fim de hold/channel). Seguro chamar sem nada segurado.</summary>
    public void Release()
    {
        if (_input.IsHolding) ActionLog.Action("RELEASE", _input.HeldKey);
        _input.ReleaseHold();
    }

    /// <summary>Larga tudo imediatamente. Usar ao parar combate / mudar de área / perder alvo.</summary>
    public void ReleaseAll()
    {
        if (_input.IsHolding) ActionLog.Action("RELEASE-ALL", _input.HeldKey);
        _input.ReleaseAll();
    }
}
