using System;
using System.Numerics;
using AutoPilot.Detection;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared;

namespace AutoPilot.Aiming;

/// <summary>
/// Move o cursor para o alvo escolhido pelo targeting.
///
/// Decisões (lições da auditoria):
///   • Mira o CENTRO DO CORPO, não os pés. entity.Pos projeta para o chão → o cursor cai abaixo do
///     mob e o ataque falha. Usa Render.Pos + metade de UnclampedHeight (padrão do Radar).
///   • Confinamento opcional a um círculo à volta do jogador (evita o cursor disparar para longe).
///   • Suavização opcional: o cursor desliza para o alvo em vez de teleportar — movimento mais natural
///     e menos propenso a falhar quando o alvo salta. Configurável (0 = teleporte imediato).
///   • Nunca move o cursor para fora da janela do jogo (clamp à área visível).
/// </summary>
public sealed class AimController
{
    private readonly GameController _gc;

    public bool ConfineToCircle { get; set; }
    public float ConfineRadius { get; set; } = 300f;
    public float Smoothing { get; set; } = 0f; // 0 = sem suavização (teleporte); 0..1 = fração por tick

    private Vector2 _lastCursor;
    private bool _hasLast;

    public AimController(GameController gameController)
    {
        _gc = gameController;
    }

    /// <summary>
    /// Aponta o cursor ao alvo. Devolve a posição de ecrã usada, ou null se o alvo não é projetável.
    /// </summary>
    public Vector2? AimAt(TrackedEntity target)
    {
        if (target?.Entity == null) return null;

        var world = BodyCenterWorld(target.Entity);
        var screen = _gc.IngameState.Camera.WorldToScreen(world);
        if (screen == Vector2.Zero || float.IsNaN(screen.X) || float.IsNaN(screen.Y))
            return null;

        var rect = _gc.Window.GetWindowRectangle();
        var topLeft = rect.TopLeft;

        // Confinamento: limita o alvo a um círculo à volta do jogador no ecrã.
        if (ConfineToCircle)
            screen = Confine(screen);

        // Suavização: desliza do último cursor para o novo alvo.
        var destination = screen;
        if (Smoothing > 0f && _hasLast)
        {
            var t = Math.Clamp(Smoothing, 0f, 1f);
            destination = Vector2.Lerp(_lastCursor, screen, t);
        }

        // Clamp à área visível da janela (em coords locais à janela).
        destination = ClampToWindow(destination, rect);

        // SetCursorPos espera coords de ecrã absolutas → soma o canto da janela.
        var absolute = destination + topLeft;
        ExileCore2.Input.SetCursorPos(absolute);

        // DIAGNÓSTICO do "atacar paredes": compara onde o mob está no ecrã (screen), onde mandámos o
        // cursor (abs), e onde o cursor REALMENTE ficou (mouse). Se 'mouse' difere de 'abs', há erro
        // de coordenadas (ex.: WorldToScreen já era absoluto e somámos topLeft a dobrar).
        try
        {
            var realMouse = ExileCore2.Input.MousePosition;
            AimDebug = $"screen=({screen.X:F0},{screen.Y:F0}) topLeft=({topLeft.X:F0},{topLeft.Y:F0}) " +
                       $"abs=({absolute.X:F0},{absolute.Y:F0}) mouse=({realMouse.X:F0},{realMouse.Y:F0}) " +
                       $"win=({rect.Width:F0}x{rect.Height:F0})";
        }
        catch { }

        _lastCursor = destination;
        _hasLast = true;
        return destination;
    }

    /// <summary>Diagnóstico da última mira (HUD/log). Mostra se o cursor vai ao sítio certo.</summary>
    public string AimDebug { get; private set; } = "";

    /// <summary>Esquece o último cursor (ex.: ao perder alvo) para a suavização recomeçar limpa.</summary>
    public void Reset()
    {
        _hasLast = false;
    }

    /// <summary>
    /// Posição de mundo do centro do corpo do alvo. entity.Pos = pés; somamos metade da altura do
    /// modelo (Render.UnclampedHeight) para mirar o tronco. Fallback a entity.Pos sem Render.
    /// </summary>
    private static Vector3 BodyCenterWorld(Entity entity)
    {
        try
        {
            var render = entity.GetComponent<Render>();
            if (render != null)
                return render.Pos with { Z = render.Pos.Z + render.UnclampedHeight / 2f };
        }
        catch { }
        return entity.Pos;
    }

    private Vector2 Confine(Vector2 targetScreen)
    {
        try
        {
            var playerScreen = _gc.IngameState.Camera.WorldToScreen(_gc.Player.Pos);
            var toTarget = targetScreen - playerScreen;
            var dist = toTarget.Length();
            if (dist > ConfineRadius && dist > 0f)
                return playerScreen + toTarget / dist * ConfineRadius;
        }
        catch { }
        return targetScreen;
    }

    private static Vector2 ClampToWindow(Vector2 pos, RectangleF rect)
    {
        // Margem para o cursor não colar à borda exata.
        const float margin = 4f;
        var x = Math.Clamp(pos.X, margin, rect.Width - margin);
        var y = Math.Clamp(pos.Y, margin, rect.Height - margin);
        return new Vector2(x, y);
    }
}
