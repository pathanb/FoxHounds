using System.Runtime.CompilerServices;

namespace FoxHounds.Core.Tests;

/// <summary>RuleSet, GameState and the Rules API contracts of spec 3, 5 and 6.</summary>
public sealed class RulesApiTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(0)]
    public void RuleSet_rejects_board_sizes_outside_4_to_16_at_construction(int n)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuleSet { BoardSize = n });
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleSet.Classic with { BoardSize = n });
    }

    [Fact]
    public void RuleSet_presets_and_value_equality()
    {
        Assert.Equal(new RuleSet(), RuleSet.Classic);
        Assert.Equal(8, RuleSet.Classic.BoardSize);
        Assert.Equal(FoxStart.Fixed, RuleSet.Classic.FoxStart);
        Assert.Null(RuleSet.Classic.FixedFoxColumn);
        Assert.Equal(Side.Fox, RuleSet.Classic.FirstMover);
        Assert.Equal(FoxWinCondition.ReachHomeRow, RuleSet.Classic.FoxWinCondition);
        Assert.True(RuleSet.Classic.AdjudicateEscape);
        Assert.Equal(FoxWinCondition.HoundsImmobile, RuleSet.Gilith.FoxWinCondition);
        Assert.False(RuleSet.Gilith.AdjudicateEscape);
        Assert.NotEqual(RuleSet.Classic, RuleSet.Gilith);
        Assert.Equal(RuleSet.Classic with { BoardSize = 10 }, RuleSet.Classic.WithBoardSize(10));
    }

    [Theory]
    [InlineData(4, "c1")]
    [InlineData(5, "b1")]
    [InlineData(6, "c1")]
    [InlineData(7, "d1")]
    [InlineData(8, "e1")]
    [InlineData(9, "d1")]
    [InlineData(10, "e1")]
    [InlineData(16, "i1")]
    public void Default_fox_start_is_the_back_row_square_with_the_largest_column_at_most_n_over_2(int n, string expected)
    {
        var rules = TestPositions.Classic(n);
        Assert.Equal(expected, rules.Geo.SquareName(rules.DefaultFoxStart));
        Assert.Equal(rules.DefaultFoxStart, rules.Initial().Fox);
    }

    [Fact]
    public void FixedFoxColumn_overrides_the_default_and_must_be_playable_in_row_n_minus_1()
    {
        var rules = new Rules(RuleSet.Classic with { FixedFoxColumn = 0 });
        Assert.Equal("a1", rules.Geo.SquareName(rules.DefaultFoxStart));
        Assert.Throws<ArgumentException>(() => new Rules(RuleSet.Classic with { FixedFoxColumn = 1 }));
        Assert.Throws<ArgumentException>(() => new Rules(RuleSet.Classic with { FixedFoxColumn = 8 }));
        Assert.Throws<ArgumentException>(() => new Rules(RuleSet.Classic with { FixedFoxColumn = -1 }));
    }

    [Fact]
    public void Undefined_enum_values_in_a_ruleset_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Rules(RuleSet.Classic with { FirstMover = (Side)7 }));
        Assert.Throws<ArgumentException>(() => new Rules(RuleSet.Classic with { FoxWinCondition = (FoxWinCondition)9 }));
        Assert.Throws<ArgumentException>(() => new Rules(RuleSet.Classic with { FoxStart = (FoxStart)9 }));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(16)]
    public void Initial_position_has_a_hound_on_every_row_0_square_and_validates(int n)
    {
        var rules = TestPositions.Classic(n);
        foreach (byte start in rules.FoxStartSquares)
        {
            var s = rules.Initial(start);
            rules.Validate(s);
            Assert.Equal(rules.Geo.RowMask[0], s.Hounds);
            Assert.Equal(n / 2, (int)UInt128.PopCount(s.Hounds));
            Assert.Equal(n - 1, rules.Geo.Row[s.Fox]);
            Assert.Equal(Side.Fox, s.SideToMove);
        }

        Assert.Equal(n / 2, rules.FoxStartSquares.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => rules.Initial(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rules.Initial(200));
    }

    [Fact]
    public void FirstMover_hounds_puts_hounds_to_move()
    {
        var rules = new Rules(RuleSet.Classic with { FirstMover = Side.Hounds });
        Assert.Equal(Side.Hounds, rules.Initial().SideToMove);
    }

    [Fact]
    public void GameState_is_a_32_byte_struct_with_value_equality_excluding_nothing_but_the_hash()
    {
        Assert.Equal(32, Unsafe.SizeOf<GameState>());
        var rules = TestPositions.Classic(8);
        var a = rules.FromNotation("8/d4/a7,c7,e7,f6/H");
        var b = rules.FromNotation("8/d4/f6,e7,c7,a7/H");
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.Hash, b.Hash);
        Assert.False(a == rules.FromNotation("8/d4/a7,c7,e7,f6/F"));
        Assert.False(a.Equals(new object()));
    }

    [Fact]
    public void IsLegal_accepts_exactly_the_generated_moves()
    {
        var rules = TestPositions.Classic(8);
        var rng = new Rng(5);
        Span<Move> buffer = stackalloc Move[Rules.MaxMoves];
        for (int i = 0; i < 2_000; i++)
        {
            var s = TestPositions.Random(rules, rng);
            bool over = rules.Evaluate(s).IsOver;
            int count = rules.GenerateMoves(s, buffer);
            var generated = buffer[..count].ToArray().ToHashSet();
            for (int from = 0; from < 32; from++)
            {
                for (int to = 0; to < 32; to++)
                {
                    var m = new Move((byte)from, (byte)to);
                    Assert.Equal(!over && generated.Contains(m), rules.IsLegal(s, m));
                }
            }
        }
    }

    [Fact]
    public void Plies_after_the_end_of_the_game_are_illegal()
    {
        // D-31: in a FoxReachedHome position the hounds still have plies, but none is legal.
        var rules = TestPositions.Classic(8);
        var s = rules.FromNotation("8/d8/b8,f8,h8,e7/H");
        Span<Move> buffer = stackalloc Move[Rules.MaxMoves];
        Assert.True(rules.GenerateMoves(s, buffer) > 0);
        Assert.False(rules.IsLegal(s, buffer[0]));
        Move first = buffer[0];
        Assert.Throws<IllegalMoveException>(() => rules.ApplyChecked(s, first));
    }

    [Fact]
    public void ApplyChecked_rejects_illegal_plies_and_out_of_range_squares()
    {
        var rules = TestPositions.Classic(8);
        var s = rules.Initial();
        Assert.Throws<IllegalMoveException>(() => rules.ApplyChecked(s, new Move(30, 22)));   // e1-e3: not a step
        Assert.Throws<IllegalMoveException>(() => rules.ApplyChecked(s, new Move(0, 4)));     // hound ply on fox's turn
        Assert.Throws<IllegalMoveException>(() => rules.ApplyChecked(s, new Move(200, 201))); // off the board
        Assert.Throws<IllegalMoveException>(() => rules.ApplyChecked(s, Move.None));
        var next = rules.ApplyChecked(s, new Move(30, 25)); // e1-d2
        Assert.Equal("8/d2/b8,d8,f8,h8/H", rules.ToNotation(next));
    }

    [Fact]
    public void IsLegal_rejects_a_position_of_another_board_size()
    {
        var rules8 = TestPositions.Classic(8);
        var rules10 = TestPositions.Classic(10);
        Assert.False(rules8.IsLegal(rules10.Initial(), new Move(30, 26)));
    }

    [Fact]
    public void Validate_and_CreateChecked_reject_every_broken_invariant()
    {
        var rules = TestPositions.Classic(8);
        UInt128 four = rules.Geo.RowMask[0];
        Assert.Throws<InvalidPositionException>(() => rules.CreateChecked(four >> 1, 30, Side.Fox));              // 3 hounds
        Assert.Throws<InvalidPositionException>(() => rules.CreateChecked(four | (UInt128.One << 5), 30, Side.Fox)); // 5 hounds
        Assert.Throws<InvalidPositionException>(() => rules.CreateChecked((four >> 1) | (UInt128.One << 40), 30, Side.Fox)); // bit ≥ P
        Assert.Throws<InvalidPositionException>(() => rules.CreateChecked(four, 2, Side.Fox));                    // fox on a hound
        Assert.Throws<InvalidPositionException>(() => rules.CreateChecked(four, 32, Side.Fox));                   // fox ≥ P
        Assert.Throws<InvalidPositionException>(() => rules.CreateChecked(four, 30, (Side)3));                    // bad side

        var other = TestPositions.Classic(10).Initial();
        Assert.Throws<InvalidPositionException>(() => rules.Validate(other));                                     // other size

        var tampered = new GameState(four, 30, Side.Fox, 8, 12345);
        var ex = Assert.Throws<InvalidPositionException>(() => rules.Validate(tampered));                         // wrong hash
        Assert.Contains("hash", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_produces_the_spec_example_positions()
    {
        var rules = TestPositions.Classic(8);
        var s = rules.Initial();
        s = rules.Apply(s, new Move(30, 25)); // e1-d2
        s = rules.Apply(s, new Move(0, 4));   // b8-a7
        Assert.Equal("8/d2/d8,f8,h8,a7/F", rules.ToNotation(s));
        rules.Validate(s);
    }
}
