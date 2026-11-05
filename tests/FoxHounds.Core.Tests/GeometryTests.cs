namespace FoxHounds.Core.Tests;

/// <summary>T-CORE-01 and the precomputed tables of spec 5.1.</summary>
public sealed class GeometryTests
{
    public static TheoryData<int> Sizes => new(TestPositions.AllSizes);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Square_index_is_a_bijection_onto_0_to_P_minus_1(int n)
    {
        var geo = Geometry.For(n);
        Assert.Equal(n * n / 2, geo.SquareCount);
        Assert.Equal(n / 2, geo.HoundCount);
        var seen = new bool[geo.SquareCount];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                int sq = geo.SquareAt(r, c);
                if (((r + c) & 1) == 0)
                {
                    Assert.Equal(Geometry.NoSquare, sq);
                    continue;
                }

                Assert.Equal(((r * n) + c) >> 1, sq);
                Assert.False(seen[sq], $"square {sq} produced twice");
                seen[sq] = true;
                Assert.Equal(r, geo.Row[sq]);
                Assert.Equal(c, geo.Col[sq]);
            }
        }

        Assert.All(seen, Assert.True);
    }

    [Fact]
    public void Worked_example_of_spec_5_1()
    {
        var geo = Geometry.For(8);
        Assert.Equal(0, Named(geo, "b8"));
        Assert.Equal(1, Named(geo, "d8"));
        Assert.Equal(2, Named(geo, "f8"));
        Assert.Equal(3, Named(geo, "h8"));
        Assert.Equal(4, Named(geo, "a7"));
        Assert.Equal(30, Named(geo, "e1"));
        Assert.Equal(30, geo.SquareAt(7, 4));
        Assert.Equal(0, geo.SquareAt(0, 1));
        Assert.Equal(4, geo.SquareAt(1, 0));
    }

    [Fact]
    public void Off_board_coordinates_are_not_squares()
    {
        var geo = Geometry.For(8);
        Assert.Equal(Geometry.NoSquare, geo.SquareAt(-1, 0));
        Assert.Equal(Geometry.NoSquare, geo.SquareAt(0, -1));
        Assert.Equal(Geometry.NoSquare, geo.SquareAt(8, 1));
        Assert.Equal(Geometry.NoSquare, geo.SquareAt(1, 8));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Step_lists_follow_the_fixed_order_and_match_the_masks(int n)
    {
        var geo = Geometry.For(n);
        for (int sq = 0; sq < geo.SquareCount; sq++)
        {
            int r = geo.Row[sq];
            int c = geo.Col[sq];
            var expectedFox = new List<int>();
            foreach (var (dr, dc) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
            {
                int to = geo.SquareAt(r + dr, c + dc);
                if (to != Geometry.NoSquare)
                {
                    expectedFox.Add(to);
                }
            }

            var expectedHound = new List<int>();
            foreach (int dc in new[] { -1, 1 })
            {
                int to = geo.SquareAt(r + 1, c + dc);
                if (to != Geometry.NoSquare)
                {
                    expectedHound.Add(to);
                }
            }

            Assert.Equal(expectedFox, geo.FoxStepList(sq).ToArray().Select(b => (int)b));
            Assert.Equal(expectedHound, geo.HoundStepList(sq).ToArray().Select(b => (int)b));
            Assert.Equal(Mask(expectedFox), geo.FoxSteps[sq]);
            Assert.Equal(Mask(expectedHound), geo.HoundSteps[sq]);
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Row_masks_partition_the_board_and_rows_0_and_n_minus_1_hold_H_squares(int n)
    {
        var geo = Geometry.For(n);
        UInt128 union = UInt128.Zero;
        for (int r = 0; r < n; r++)
        {
            Assert.Equal(UInt128.Zero, union & geo.RowMask[r]);
            union |= geo.RowMask[r];
        }

        Assert.Equal(geo.AllSquares, union);
        Assert.Equal(geo.HoundCount, (int)UInt128.PopCount(geo.RowMask[0]));
        Assert.Equal(geo.HoundCount, (int)UInt128.PopCount(geo.RowMask[n - 1]));
        Assert.Equal(geo.HoundCount, geo.BackRowSquares.Length);
    }

    [Fact]
    public void Geometry_is_cached_per_size()
    {
        Assert.Same(Geometry.For(8), Geometry.For(8));
        Assert.NotSame(Geometry.For(8), Geometry.For(9));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    public void Geometry_rejects_sizes_outside_4_to_16(int n)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Geometry.For(n));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Square_names_round_trip(int n)
    {
        var geo = Geometry.For(n);
        for (int sq = 0; sq < geo.SquareCount; sq++)
        {
            string name = geo.SquareName(sq);
            Assert.True(geo.TryParseSquare(name, out int parsed), name);
            Assert.Equal(sq, parsed);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("e0")]
    [InlineData("e01")]
    [InlineData("e9")]
    [InlineData("i1")]
    [InlineData("E1")]
    [InlineData("e1x")]
    [InlineData("d1")]
    public void Bad_square_names_are_rejected_on_8x8(string name)
    {
        Assert.False(Geometry.For(8).TryParseSquare(name, out _));
    }

    [Fact]
    public void Names_reach_file_p_and_rank_16()
    {
        var geo = Geometry.For(16);
        Assert.Equal("b16", geo.SquareName(0));
        Assert.True(geo.TryParseSquare("p16", out int sq));
        Assert.Equal((0, 15), (geo.Row[sq], geo.Col[sq]));
        Assert.False(geo.TryParseSquare("p15", out _)); // row 1, column 15: unplayable
    }

    [Fact]
    public void Moves_format_and_parse_as_from_dash_to()
    {
        var geo = Geometry.For(8);
        var move = new Move(30, 25);
        Assert.Equal("e1-d2", geo.FormatMove(move));
        Assert.True(geo.TryParseMove("e1-d2", out var parsed));
        Assert.Equal(move, parsed);
        Assert.False(geo.TryParseMove("e1d2", out _));
        Assert.False(geo.TryParseMove("e1-d3", out _));
    }

    private static int Named(Geometry geo, string name)
    {
        Assert.True(geo.TryParseSquare(name, out int sq), name);
        return sq;
    }

    private static UInt128 Mask(IEnumerable<int> squares)
    {
        UInt128 m = UInt128.Zero;
        foreach (int sq in squares)
        {
            m |= UInt128.One << sq;
        }

        return m;
    }
}
