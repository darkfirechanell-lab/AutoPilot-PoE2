using System;
using System.Numerics;
using ExileCore2;

namespace AutoPilot.Targeting;

/// <summary>
/// Linha de vista por terreno: decide se um ponto do grid é visível a partir do jogador, para não
/// mirar mobs atrás de paredes.
///
/// Decisões face ao AutoMyAim:
///   • FALLBACK seguro: se os dados de terreno não estão legíveis (transição de área, etc.), assume
///     VISÍVEL — nunca trava o combate por falta de leitura. (Escolha de produto do utilizador.)
///   • Sem raycast duplicado (era o L3): o teste é uma única passagem DDA, sem segundo cálculo.
///   • Os dados de terreno são copiados só em <see cref="UpdateArea"/> (mudança de área), não por tick.
///
/// O valor de terreno por célula é uma "altura" de obstrução; abaixo do limiar passa-se, igual ou
/// acima bloqueia. O limiar é configurável (TargetLayerValue no AutoMyAim).
/// </summary>
public sealed class RayCaster
{
    public int BlockingLayerValue { get; set; } = 2;
    public bool UseWalkableTerrain { get; set; } = false;

    private readonly GameController _gc;
    private int[][] _terrain;
    private int _width;
    private int _height;

    public RayCaster(GameController gameController)
    {
        _gc = gameController;
    }

    /// <summary>Recarrega o mapa de terreno da área atual. Chamar em AreaChange (e no arranque).</summary>
    public void UpdateArea()
    {
        try
        {
            var data = _gc.IngameState.Data;
            var dims = data.AreaDimensions;
            _width = (int)dims.X;
            _height = (int)dims.Y;

            var raw = UseWalkableTerrain ? data.RawPathfindingData : data.RawTerrainTargetingData;
            if (raw == null) { _terrain = null; return; }

            _terrain = new int[raw.Length][];
            for (var y = 0; y < raw.Length; y++)
            {
                _terrain[y] = new int[raw[y].Length];
                Array.Copy(raw[y], _terrain[y], raw[y].Length);
            }
        }
        catch
        {
            _terrain = null; // fallback: tudo visível até a próxima leitura boa
        }
    }

    /// <summary>
    /// True se há linha de vista do jogador até <paramref name="target"/> (ambos em grid).
    /// Se o terreno não está carregado, devolve true (fallback — não bloqueia o combate).
    /// </summary>
    public bool IsVisible(Vector2 observer, Vector2 target)
    {
        if (_terrain == null) return true;

        int x0 = (int)observer.X, y0 = (int)observer.Y;
        int x1 = (int)target.X, y1 = (int)target.Y;

        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx - dy;

        var x = x0;
        var y = y0;

        // Bresenham: caminha célula a célula; a primeira obstrução corta a linha de vista.
        while (true)
        {
            if (x == x1 && y == y1) return true;

            var e2 = err * 2;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }

            // Não testa a célula de origem; testa cada célula intermédia até (mas não incluindo) o alvo.
            if (x == x1 && y == y1) return true;
            if (Blocks(x, y)) return false;
        }
    }

    private bool Blocks(int x, int y)
    {
        if (y < 0 || y >= _terrain.Length) return false;
        var row = _terrain[y];
        if (x < 0 || x >= row.Length) return false;
        // No terrain targeting do PoE2, valores ALTOS = chão aberto (vê-se), valores BAIXOS = obstáculo.
        // Bloqueia a linha de vista quando o valor é MENOR que o limiar. (Antes estava invertido:
        // bloqueava o chão aberto e marcava TODOS os mobs como invisíveis → nunca havia alvo.)
        return row[x] < BlockingLayerValue;
    }

    public void Clear() => _terrain = null;
}
