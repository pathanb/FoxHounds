using FoxHounds.Core;

namespace FoxHounds.App;

/// <summary>An axis-aligned rectangle in window pixels.</summary>
internal readonly record struct Rect(float X, float Y, float W, float H)
{
    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;

    public Rect Inset(float d) => new(X + d, Y + d, W - (2 * d), H - (2 * d));
}

/// <summary>
/// Rectangles for the board, its cells and the control panel, computed from the window size every frame
/// (spec 13.1, 13.2). No Raylib, so it is unit-tested.
/// </summary>
internal sealed class Layout
{
    public const int MinWidth = 800;
    public const int MinHeight = 600;
    public const float RowHeight = 34;
    private const float Margin = 16;
    private const float Gutter = 22; // room for the coordinates

    private Layout(int width, int height, int n)
    {
        N = n;
        float panelWidth = Math.Clamp(width * 0.32f, 300, 420);
        Panel = new Rect(width - panelWidth, 0, panelWidth, height);
        float areaW = width - panelWidth - (2 * Margin) - Gutter;
        float areaH = height - (2 * Margin) - Gutter;
        Cell = MathF.Floor(Math.Max(1, Math.Min(areaW, areaH)) / n);
        float size = Cell * n;
        float x = Margin + Gutter + MathF.Floor((areaW - size) / 2);
        float y = Margin + MathF.Floor((areaH - size) / 2);
        Board = new Rect(x, y, size, size);
    }

    public int N { get; }

    public Rect Board { get; }

    public float Cell { get; }

    public Rect Panel { get; }

    /// <summary>Font size of the coordinates along the board edges.</summary>
    public int CoordinateFont => (int)Math.Clamp(Cell / 3, 10, 18);

    public static Layout Compute(int width, int height, int n) =>
        new(Math.Max(width, MinWidth), Math.Max(height, MinHeight), n);

    public Rect CellRect(int row, int col) => new(Board.X + (col * Cell), Board.Y + (row * Cell), Cell, Cell);

    /// <summary>The playable square under a window point, or −1.</summary>
    public int SquareAt(float px, float py, Geometry geo)
    {
        if (!Board.Contains(px, py))
        {
            return -1;
        }

        int col = (int)((px - Board.X) / Cell);
        int row = (int)((py - Board.Y) / Cell);
        return geo.SquareAt(row, col);
    }

    /// <summary>The i-th row of the panel, from the top.</summary>
    public Rect PanelRow(int i) => new(Panel.X + 12, Panel.Y + 12 + (i * RowHeight), Panel.W - 24, RowHeight - 6);

    /// <summary>The i-th row counted from the bottom of the panel (0 = lowest).</summary>
    public Rect PanelRowFromBottom(int i) =>
        new(Panel.X + 12, Panel.Y + Panel.H - 12 - ((i + 1) * RowHeight) + 6, Panel.W - 24, RowHeight - 6);

    /// <summary>Splits a row into <paramref name="count"/> equal cells with gaps.</summary>
    public static Rect Split(Rect row, int index, int count, float gap = 8)
    {
        float w = (row.W - (gap * (count - 1))) / count;
        return new Rect(row.X + (index * (w + gap)), row.Y, w, row.H);
    }
}
