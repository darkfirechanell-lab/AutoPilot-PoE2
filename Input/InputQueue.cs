using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore2;

namespace AutoPilot.Input;

/// <summary>
/// Gere todo o estado de teclas do plugin sem NUNCA bloquear o thread do jogo.
///
/// Porquê isto existe: o método ingénuo de fazer um "tap" é KeyDown → Thread.Sleep(5) → KeyUp,
/// mas o Sleep congela o Tick inteiro do ExileCore (rouba FPS), e sem QUALQUER gap o jogo
/// às vezes perde o tap. A solução: KeyDown imediato + KeyUp AGENDADO para um instante futuro,
/// libertado por <see cref="Pump"/> quando o relógio real chega lá. O gap existe, mas medido
/// entre ticks em vez de num Sleep — zero bloqueio.
///
/// Regras invioláveis (lições do AutoMyAim):
///   • Cada KeyDown tem de ter sempre o seu KeyUp — senão a tecla fica presa (loot bloqueado, spam).
///   • Nunca usar Input.KeyPressRelease (deixava a tecla efetivamente presa).
///   • Uma só tecla "hold" de cada vez para skills; mudar de hold liberta a anterior primeiro.
/// </summary>
public sealed class InputQueue : IDisposable
{
    /// <summary>Gap mínimo (ms) entre o KeyDown e o KeyUp de um tap, para o jogo o registar.</summary>
    public const int DefaultTapHoldMs = 12;

    /// <summary>
    /// REDE DE SEGURANÇA ANTI-KICK: gap mínimo (ms) entre QUALQUER duas ações de input (tap/hold),
    /// independentemente da config. O servidor expulsa por "too many actions too fast" acima de ~20-30
    /// ações/seg; 60ms = no máximo ~16/seg, bem dentro do seguro. NENHUMA routine/config pode furar isto.
    /// (Equivalente ao AI_clicks_per_second do ExiledBot.) Se uma skill quer disparar antes do gap, é
    /// SILENCIOSAMENTE saltada nesse tick — tenta no próximo. Configurável via MinGapMs.
    /// </summary>
    public int MinGapMs { get; set; } = 60;
    private long _lastActionTicks;

    // Taps pendentes de libertação: tecla → instante (UTC ticks) em que o KeyUp deve ocorrer.
    private readonly Dictionary<Keys, long> _pendingTapRelease = new();

    // A tecla atualmente em "hold" contínuo (ex.: canalizar Snipe, segurar Salvo). None = nenhuma.
    private Keys _heldKey = Keys.None;

    private bool _disposed;

    /// <summary>True se há uma tecla em hold contínuo neste momento.</summary>
    public bool IsHolding => _heldKey != Keys.None;

    /// <summary>A tecla em hold, ou Keys.None.</summary>
    public Keys HeldKey => _heldKey;

    /// <summary>
    /// Tap: prime e agenda a libertação para daqui a <paramref name="holdMs"/>. Não bloqueia.
    /// Se a mesma tecla já estiver em hold, liberta-a primeiro (um tap é um evento discreto).
    /// </summary>
    public void Tap(Keys key, int holdMs = DefaultTapHoldMs)
    {
        if (_disposed || key == Keys.None) return;

        // REDE DE SEGURANÇA: se a última ação foi há menos de MinGapMs, SALTA este tap (evita o kick
        // "too many actions too fast"). A skill tenta de novo no próximo tick.
        if (!GapElapsed()) return;

        // Se esta tecla está presa como hold, termina o hold — o tap é uma intenção diferente.
        if (_heldKey == key) ReleaseHold();

        // Tap atómico: KeyDown, pequeno gap para o jogo registar, KeyUp.
        ExileCore2.Input.KeyDown(key);
        System.Threading.Thread.Sleep(5);
        ExileCore2.Input.KeyUp(key);
        _lastActionTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>True se já passou o MinGapMs desde a última ação (rede de segurança anti-kick).</summary>
    private bool GapElapsed()
    {
        if (MinGapMs <= 0) return true;
        var elapsed = (DateTime.UtcNow.Ticks - _lastActionTicks) / TimeSpan.TicksPerMillisecond;
        return elapsed >= MinGapMs;
    }

    /// <summary>
    /// Hold contínuo: mantém a tecla premida até <see cref="ReleaseHold"/>. Idempotente —
    /// chamar repetidamente com a mesma tecla não re-prime. Mudar de tecla liberta a anterior.
    /// </summary>
    public void Hold(Keys key)
    {
        if (_disposed || key == Keys.None) return;
        if (_heldKey == key) return; // já está em hold, nada a fazer (continuar não é input novo)

        // REDE DE SEGURANÇA: o ARRANQUE de um hold é uma ação nova — respeita o gap mínimo anti-kick.
        if (!GapElapsed()) return;

        if (_heldKey != Keys.None) ReleaseHold();

        // Se a tecla tinha um tap pendente, cancela-o — o hold assume o controlo da tecla.
        _pendingTapRelease.Remove(key);

        ExileCore2.Input.KeyDown(key);
        _heldKey = key;
        _lastActionTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>Liberta a tecla em hold (se houver). Seguro chamar sem nada em hold.</summary>
    public void ReleaseHold()
    {
        if (_heldKey == Keys.None) return;
        ExileCore2.Input.KeyUp(_heldKey);
        _heldKey = Keys.None;
    }

    /// <summary>
    /// Processa as libertações agendadas. DEVE ser chamado uma vez por Tick. É aqui que os taps
    /// cujo tempo expirou recebem o KeyUp — substitui o antigo Thread.Sleep por relógio real.
    /// </summary>
    public void Pump()
    {
        if (_disposed || _pendingTapRelease.Count == 0) return;

        var now = DateTime.UtcNow.Ticks;
        List<Keys> toRelease = null;
        foreach (var kv in _pendingTapRelease)
            if (now >= kv.Value)
                (toRelease ??= new List<Keys>()).Add(kv.Key);

        if (toRelease == null) return;
        foreach (var key in toRelease)
        {
            ExileCore2.Input.KeyUp(key);
            _pendingTapRelease.Remove(key);
        }
    }

    /// <summary>
    /// Larga TUDO imediatamente: hold + todos os taps pendentes. Usar ao desligar o plugin,
    /// mudar de área, ou perder o alvo a meio de um canal — garante que nenhuma tecla fica presa.
    /// </summary>
    public void ReleaseAll()
    {
        ReleaseHold();

        if (_pendingTapRelease.Count > 0)
        {
            foreach (var key in _pendingTapRelease.Keys)
                ExileCore2.Input.KeyUp(key);
            _pendingTapRelease.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseAll();
        _disposed = true;
    }
}
