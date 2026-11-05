using FoxHounds.Core;
using Raylib_cs;

namespace FoxHounds.App;

/// <summary>Draws the board from the session's state (spec 13.2). Only this class and <see cref="Ui"/> call Raylib
/// drawing functions, always on the main thread.</summary>
internal static class RaylibView
{
    public static readonly Color Background = new(246, 244, 240, 255);
    private static readonly Color Light = new(236, 228, 212, 255);
    private static readonly Color Dark = new(168, 150, 128, 255);
    private static readonly Color FoxColor = new(222, 120, 40, 255);
    private static readonly Color HoundColor = new(48, 62, 80, 255);
    private static readonly Color LastPly = new(250, 220, 90, 150);
    private static readonly Color SelectedRing = new(40, 120, 230, 255);
    private static readonly Color TargetDot = new(30, 30, 30, 110);
    private static readonly Color Trap = new(200, 30, 30, 255);
    private static readonly Color Coordinates = new(90, 90, 90, 255);

    public static void DrawBoard(Layout layout, GameSession session)
    {
        var rules = session.Rules;
        var geo = rules.Geo;
        int n = geo.N;
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                Raylib.DrawRectangleRec(Ui.ToRaylib(layout.CellRect(r, c)), ((r + c) & 1) == 1 ? Dark : Light);
            }
        }

        if (session.Plies.Count > 0)
        {
            var last = session.Plies[^1].Move;
            Tint(layout, geo, last.From, LastPly);
            Tint(layout, geo, last.To, LastPly);
        }

        foreach (int sq in session.TrappingSquares())
        {
            Raylib.DrawRectangleLinesEx(Ui.ToRaylib(Cell(layout, geo, sq).Inset(1)), Math.Max(2, layout.Cell / 16), Trap);
        }

        var s = session.Position;
        UInt128 hounds = s.Hounds;
        while (hounds != UInt128.Zero)
        {
            int sq = (int)UInt128.TrailingZeroCount(hounds);
            Piece(layout, geo, sq, HoundColor, "H");
            hounds &= hounds - UInt128.One;
        }

        if (!session.AwaitingFoxPlacement)
        {
            Piece(layout, geo, s.Fox, FoxColor, "F");
        }

        if (session.Selected is byte selected)
        {
            var cell = Cell(layout, geo, selected);
            Raylib.DrawRing(Center(cell), layout.Cell * 0.42f, layout.Cell * 0.48f, 0, 360, 48, SelectedRing);
        }

        foreach (int sq in session.Targets())
        {
            Raylib.DrawCircleV(Center(Cell(layout, geo, sq)), Math.Max(4, layout.Cell * 0.12f), TargetDot);
        }

        DrawCoordinates(layout, n);
    }

    /// <summary>The game-over banner's box and text; its buttons are drawn by the controller.</summary>
    public static void DrawBannerBox(Rect box, Rect textRow, string text, int fontSize)
    {
        Raylib.DrawRectangleRec(Ui.ToRaylib(box), new Color(255, 255, 255, 240));
        Raylib.DrawRectangleLinesEx(Ui.ToRaylib(box), 2, Ui.Text);
        Ui.DrawCentered(text, textRow, Ui.Text, fontSize);
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

    private static System.Numerics.Vector2 Center(Rect r) => new(r.X + (r.W / 2), r.Y + (r.H / 2));
}
