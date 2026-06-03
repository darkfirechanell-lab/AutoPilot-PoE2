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

    public SkillExecutor(InputQueue input)
    {
        _input = input;
    }

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
        _input.Tap(key, tapHoldMs);
    }

    /// <summary>Começa/mantém a segurar a tecla. Idempotente (ver <see cref="InputQueue.Hold"/>).</summary>
    public void Hold(Keys key)
    {
        if (key == Keys.None) return;
        _input.Hold(key);
    }

    /// <summary>
    /// Inicia/mantém uma canalização. Idêntico a <see cref="Hold"/> na mecânica; nome distinto
    /// para a intenção ser clara no código das routines (ex.: Snipe).
    /// </summary>
    public void Channel(Keys key) => Hold(key);

    /// <summary>Liberta a tecla segurada (fim de hold/channel). Seguro chamar sem nada segurado.</summary>
    public void Release() => _input.ReleaseHold();

    /// <summary>Larga tudo imediatamente. Usar ao parar combate / mudar de área / perder alvo.</summary>
    public void ReleaseAll() => _input.ReleaseAll();
}
