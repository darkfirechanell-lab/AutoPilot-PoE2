using System;
using System.Windows.Forms;
using AutoPilot.Input;

namespace AutoPilot.Combat;

/// <summary>
/// Esquiva (dodge roll) quando o <see cref="DangerDetector"/> reporta perigo acima do limiar.
/// Quando dispara, TEM PRIORIDADE sobre o aim/rotação nesse instante (o plugin pergunta
/// <see cref="WantsControl"/> e, se true, esquiva em vez de mirar/atacar). SEM WASD — usa a tecla de
/// dodge do jogo (esquiva = move/roll na direção atual ou para longe, conforme a build do jogador).
///
/// Tudo desligado por defeito; só age com o toggle ligado. Cooldown anti-spam.
/// </summary>
public sealed class DodgeController
{
    private readonly InputQueue _input;

    public bool Enabled { get; set; }
    public Keys DodgeKey { get; set; } = Keys.Space;
    public float DangerThreshold { get; set; } = 3f; // score do DangerDetector acima do qual esquiva.
    public int CooldownMs { get; set; } = 1500;

    private long _lastDodgeTicks;
    public string Debug { get; private set; } = "";

    public DodgeController(InputQueue input)
    {
        _input = input;
    }

    /// <summary>True se o dodge quer agir AGORA (perigo acima do limiar e fora do cooldown).</summary>
    public bool WantsControl(float dangerScore)
    {
        if (!Enabled || DodgeKey == Keys.None) { Debug = "dodge: off"; return false; }

        var sinceMs = _lastDodgeTicks == 0
            ? long.MaxValue
            : (DateTime.UtcNow.Ticks - _lastDodgeTicks) / TimeSpan.TicksPerMillisecond;

        var onCooldown = sinceMs < CooldownMs;
        var danger = dangerScore >= DangerThreshold;

        Debug = $"dodge: score={dangerScore:F1}/{DangerThreshold:F0} cd={(onCooldown ? "sim" : "nao")} quer={(danger && !onCooldown)}";
        return danger && !onCooldown;
    }

    /// <summary>Executa a esquiva (tap na tecla de dodge). Chamado pelo plugin quando WantsControl=true.</summary>
    public void Dodge()
    {
        if (!Enabled || DodgeKey == Keys.None) return;
        _input.Tap(DodgeKey, InputQueue.DefaultTapHoldMs);
        _lastDodgeTicks = DateTime.UtcNow.Ticks;
    }
}
