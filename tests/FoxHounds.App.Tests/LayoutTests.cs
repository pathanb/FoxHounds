using FoxHounds.Core;

namespace FoxHounds.App.Tests;

/// <summary>The window layout (spec 13.2) at every board size and a range of window sizes.</summary>
public sealed class LayoutTests
{
    public static TheoryData<int, int, int> Sizes()
    {
        var data = new TheoryData<int, int, int>();
        foreach (var (w, h) in new[] { (800, 600), (1100, 760), (1920, 1080), (900, 1400), (2600, 700) })
        {
            foreach (int n in new[] { 4, 7, 8, 12, 16 })
            {
                data.Add(w, h, n);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void The_board_is_square_fits_beside_the_panel_and_leaves_room_for_coordinates(int width, int height, int n)
    {
        var layout = Layout.Compute(width, height, n);
        var board = layout.Board;
        Assert.Equal(layout.Cell * n, board.W);
        Assert.Equal(board.W, board.H);
        Assert.Equal(MathF.Floor(layout.Cell), layout.Cell);         // whole pixels per cell
        Assert.True(board.X >= 30, "no room for the rank numbers");
        Assert.True(board.Y >= 0 && board.Y + board.H + 20 <= height, "no room for the file letters");
        Assert.True(board.X + board.W <= layout.Panel.X, "the board runs under the panel");
        Assert.Equal(width, layout.Panel.X + layout.Panel.W);
        Assert.True(layout.Panel.W is >= 300 and <= 420);

        // The cell is as large as the free area allows (spec 13.2: floor(min(width, height) / n)).
        float freeW = layout.Panel.X - 16 - 16 - 22;
        float freeH = height - 16 - 16 - 22;
        Assert.Equal(MathF.Floor(Math.Min(freeW, freeH) / n), layout.Cell);

        // Readable coordinates at every size (spec 16.1 item 5), and the move list keeps some rows.
        Assert.True(layout.CoordinateFont >= 10);
        Assert.True(layout.PanelRowFromBottom(2).Y - layout.PanelRow(7).Y >= 3 * 22, "no room for the move list");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Clicks_map_to_the_playable_square_under_the_pointer(int width, int height, int n)
    {
        var layout = Layout.Compute(width, height, n);
        var geo = Geometry.For(n);
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                var cell = layout.CellRect(r, c);
                int expected = geo.SquareAt(r, c);
                Assert.Equal(expected, layout.SquareAt(cell.X + (cell.W / 2), cell.Y + (cell.H / 2), geo));
                Assert.Equal(expected, layout.SquareAt(cell.X, cell.Y, geo));                     // top-left pixel
                Assert.Equal(expected, layout.SquareAt(cell.X + cell.W - 0.5f, cell.Y + cell.H - 0.5f, geo));
            }
        }

        Assert.Equal(-1, layout.SquareAt(layout.Board.X - 1, layout.Board.Y, geo));
        Assert.Equal(-1, layout.SquareAt(layout.Board.X + layout.Board.W, layout.Board.Y, geo));
        Assert.Equal(-1, layout.SquareAt(layout.Panel.X + 5, 100, geo));
    }

    [Fact]
    public void A_window_below_the_minimum_is_laid_out_as_the_minimum()
    {
        var small = Layout.Compute(300, 200, 8);
        var minimum = Layout.Compute(Layout.MinWidth, Layout.MinHeight, 8);
        Assert.Equal(minimum.Board, small.Board);
        Assert.Equal(minimum.Panel, small.Panel);
    }

    [Fact]
    public void Split_divides_a_row_into_equal_cells_with_gaps()
    {
        var row = new Rect(10, 20, 300, 28);
        var cells = Enumerable.Range(0, 3).Select(i => Layout.Split(row, i, 3)).ToArray();
        Assert.All(cells, c => Assert.Equal(cells[0].W, c.W, 3));
        Assert.Equal(row.X, cells[0].X);
        Assert.Equal(row.X + row.W, cells[2].X + cells[2].W, 3);
        Assert.Equal(8, cells[1].X - (cells[0].X + cells[0].W), 3);
    }
}
