using System.Numerics;
using FoxHounds.Core;
using FoxHounds.Solver;
using Raylib_cs;

namespace FoxHounds.App;

/// <summary>Draws the board from a <see cref="BoardView"/> snapshot (spec 13.1, 13.2, 13.5). Only this class and
/// <see cref="Ui"/> call Raylib drawing functions, always on the main thread.</summary>
internal static class RaylibView
{
    public static readonly Color Background = new(246, 244, 240, 255);
    public static readonly Color Fastest = new(40, 160, 70, 255);
    public static readonly Color Slower = new(235, 185, 30, 255);
    public static readonly Color Blunder = new(215, 45, 40, 255);
    public static readonly Color Lost = new(150, 150, 150, 255);
    private static readonly Color Light = new(236, 228, 212, 255);
    private static readonly Color Dark = new(168, 150, 128, 255);
    private static readonly Color FoxColor = new(222, 120, 40, 255);
    private static readonly Color HoundColor = new(48, 62, 80, 255);
    private static readonly Color LastPly = new(250, 220, 90, 150);
    private static readonly Color SelectedRing = new(40, 120, 230, 255);
    private static readonly Color TargetDot = new(30, 30, 30, 110);
    private static readonly Color Trap = new(200, 30, 30, 255);
    private static readonly Color Coordinates = new(90, 90, 90, 255);
    private static readonly Color Outline = new(0, 0, 0, 170);

    public static Color ColourOf(MoveQuality quality) => quality switch
    {
        MoveQuality.Fastest => Fastest,
        MoveQuality.Slower => Slower,
        MoveQuality.Blunder => Blunder,
        _ => Lost,
    };

    public static void DrawBoard(Layout layout, Geometry geo, BoardView view)
    {
        int n = geo.N;
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                Raylib.DrawRectangleRec(Ui.ToRaylib(layout.CellRect(r, c)), ((r + c) & 1) == 1 ? Dark : Light);
            }
        }

        if (view.LastMove is { } last)
        {
            Tint(layout, geo, last.From, LastPly);
            Tint(layout, geo, last.To, LastPly);
        }

        foreach (int sq in view.TrappingSquares)
        {
            Raylib.DrawRectangleLinesEx(Ui.ToRaylib(Cell(layout, geo, sq).Inset(1)), Math.Max(2, layout.Cell / 16), Trap);
        }

        var s = view.Position;
        UInt128 hounds = s.Hounds;
        while (hounds != UInt128.Zero)
        {
            int sq = (int)UInt128.TrailingZeroCount(hounds);
            Piece(layout, geo, sq, HoundColor, "H");
            hounds &= hounds - UInt128.One;
        }

        if (view.FoxPlaced)
        {
            Piece(layout, geo, s.Fox, FoxColor, "F");
        }

        if (view.Selected is byte selected)
        {
            var cell = Cell(layout, geo, selected);
            Raylib.DrawRing(Center(cell), layout.Cell * 0.42f, layout.Cell * 0.48f, 0, 360, 48, SelectedRing);
        }

        foreach (int sq in view.Targets)
        {
            Raylib.DrawCircleV(Center(Cell(layout, geo, sq)), Math.Max(4, layout.Cell * 0.12f), TargetDot);
        }

        foreach (var g in view.Graded)
        {
            MoveMarker(layout, geo, g.Move, ColourOf(g.Quality), g.Move == view.NextMove);
        }

        if (view.Graded.Count == 0 && view.NextMove is { } next)
        {
            MoveMarker(layout, geo, next, SelectedRing, ringed: true); // no solver colours: just show what was played
        }

        DrawCoordinates(layout, n);
    }

    /// <summary>Where the marker of a ply sits: in its target square, shifted towards the piece that moves, so two
    /// hounds that can reach the same square get two markers.</summary>
    public static Vector2 MarkerCenter(Layout layout, Geometry geo, Move move)
    {
        var from = Center(Cell(layout, geo, move.From));
        var to = Center(Cell(layout, geo, move.To));
        return to + (Vector2.Normalize(from - to) * (layout.Cell * 0.24f));
    }

    public static float MarkerRadius(Layout layout) => Math.Max(5, layout.Cell * 0.12f);

    /// <summary>The game-over banner's box and text; its buttons are drawn by the controller.</summary>
    public static void DrawBannerBox(Rect box, Rect textRow, string text, int fontSize)
    {
        Raylib.DrawRectangleRec(Ui.ToRaylib(box), new Color(255, 255, 255, 240));
        Raylib.DrawRectangleLinesEx(Ui.ToRaylib(box), 2, Ui.Text);
        Ui.DrawCentered(text, textRow, Ui.Text, fontSize);
    }

    /// <summary>A box of text lines near the mouse, kept inside the window.</summary>
    public static void DrawTooltip(Vector2 mouse, IReadOnlyList<(string Text, Color Swatch)> lines, int fontSize)
    {
        const int pad = 8;
        int swatch = fontSize - 4;
        int w = lines.Max(l => Raylib.MeasureText(l.Text, fontSize)) + (3 * pad) + swatch;
        int h = (lines.Count * (fontSize + 6)) + (2 * pad) - 6;
        float x = Math.Min(mouse.X + 16, Raylib.GetScreenWidth() - w - 4);
        float y = Math.Min(mouse.Y + 16, Raylib.GetScreenHeight() - h - 4);
        Raylib.DrawRectangleRec(new Rectangle(x, y, w, h), new Color(255, 255, 255, 245));
        Raylib.DrawRectangleLinesEx(new Rectangle(x, y, w, h), 1, Ui.Border);
        for (int i = 0; i < lines.Count; i++)
        {
            int ly = (int)y + pad + (i * (fontSize + 6));
            Raylib.DrawRectangle((int)x + pad, ly + 2, swatch, swatch, lines[i].Swatch);
            Raylib.DrawText(lines[i].Text, (int)x + (2 * pad) + swatch, ly, fontSize, Ui.Text);
        }
    }

    private static void MoveMarker(Layout layout, Geometry geo, Move move, Color color, bool ringed)
    {
        var from = Center(Cell(layout, geo, move.From));
        var dot = MarkerCenter(layout, geo, move);
        float radius = MarkerRadius(layout);
        var direction = Vector2.Normalize(dot - from);
        var start = from + (direction * (layout.Cell * 0.42f));
        var end = dot - (direction * radius);
        if (Vector2.Dot(end - start, direction) > 0)
        {
            Raylib.DrawLineEx(start, end, Math.Max(2, layout.Cell * 0.05f), color with { A = 210 });
        }

        Raylib.DrawCircleV(dot, radius, color);
        Raylib.DrawCircleLinesV(dot, radius, Outline);
        if (ringed)
        {
            // The ply the game went on with.
            Raylib.DrawRing(dot, radius + 2, radius + Math.Max(3, layout.Cell * 0.05f) + 2, 0, 360, 36, Ui.Text);
        }
    }

    private static void DrawCoordinates(Layout layout, int n)
    {
        int font = layout.CoordinateFont;
        for (int i = 0; i < n; i++)
        {
            string file = ((char)('a' + i)).ToString();
            var below = new Rect(layout.Board.X + (i * layout.Cell), layout.Board.Y + layout.Board.H + 2, layout.Cell, font + 4);
            Ui.DrawCentered(file, below, Coordinates, font);
            string rank = (n - i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            int w = Raylib.MeasureText(rank, font);
            Raylib.DrawText(rank, (int)(layout.Board.X - w - 5), (int)(layout.Board.Y + (i * layout.Cell) + ((layout.Cell - font) / 2)), font, Coordinates);
        }
    }

    private static void Piece(Layout layout, Geometry geo, int sq, Color color, string letter)
    {
        var cell = Cell(layout, geo, sq);
        var c = Center(cell);
        float radius = layout.Cell * 0.4f; // 80% of the cell
        Raylib.DrawCircleV(c, radius, color);
        Raylib.DrawCircleLinesV(c, radius, new Color(0, 0, 0, 120));
        int font = (int)Math.Max(10, layout.Cell * 0.45f);
        Ui.DrawCentered(letter, cell, Color.White, font);
    }

    private static void Tint(Layout layout, Geometry geo, int sq, Color color) =>
        Raylib.DrawRectangleRec(Ui.ToRaylib(Cell(layout, geo, sq)), color);

    private static Rect Cell(Layout layout, Geometry geo, int sq) => layout.CellRect(geo.Row[sq], geo.Col[sq]);

    private static Vector2 Center(Rect r) => new(r.X + (r.W / 2), r.Y + (r.H / 2));
}
