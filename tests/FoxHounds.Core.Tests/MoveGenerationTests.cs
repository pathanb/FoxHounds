namespace FoxHounds.Core.Tests;

/// <summary>T-CORE-02: move generation against an independent naive generator, plus the end-of-game rules against
/// the same naive implementation.</summary>
public sealed class MoveGenerationTests
{
    private const int PositionsPerSize = 100_000;

    public static TheoryData<int> SpecSizes => new(4, 5, 8, 11, 16);

    [Theory]
    [MemberData(nameof(SpecSizes))]
    public void Generated_moves_equal_the_naive_generator_in_the_same_order(int n)
    {
        var rules = TestPositions.Classic(n);
        var rng = new Rng(0xC0FE0000UL + (ulong)n);
        Span<Move> buffer = stackalloc Move[Rules.MaxMoves];
        for (int i = 0; i < PositionsPerSize; i++)
        {
            var s = TestPositions.Random(rules, rng);
            int count = rules.GenerateMoves(s, buffer);
            var expected = NaiveRules.Moves(n, s);
            Assert.Equal(expected.Count, count);
            for (int k = 0; k < count; k++)
            {
                Assert.Equal(expected[k], (buffer[k].From, buffer[k].To));
            }

            Assert.Equal(count > 0, rules.HasAnyMove(s));
        }
    }

    [Theory]
    [MemberData(nameof(SpecSizes))]
    public void Evaluate_equals_the_naive_end_of_game_rules_under_every_variant(int n)
    {
        foreach (var variant in TestPositions.AllVariants(n))
        {
            var rules = new Rules(variant);
            var rng = new Rng(0xE0A10000UL + (ulong)n);
            for (int i = 0; i < PositionsPerSize / 10; i++)
            {
                var s = TestPositions.Random(rules, rng);
                Assert.Equal(NaiveRules.Evaluate(variant, s), rules.Evaluate(s));
            }
        }
    }

    [Fact]
    public void Generation_never_exceeds_MaxMoves_and_writes_nothing_past_the_count()
    {
        var rules = TestPositions.Classic(16);
        var rng = new Rng(99);
        Span<Move> buffer = stackalloc Move[Rules.MaxMoves + 1];
        for (int i = 0; i < 20_000; i++)
        {
            buffer.Fill(Move.None);
            var s = TestPositions.Random(rules, rng);
            int count = rules.GenerateMoves(s, buffer);
            Assert.InRange(count, 0, Rules.MaxMoves);
            Assert.True(buffer[count..].ToArray().All(m => m.IsNone));
        }
    }

    [Fact]
    public void Initial_classic_position_has_the_expected_moves()
    {
        var rules = TestPositions.Classic(8);
        var s = rules.Initial();
        Assert.Equal(new[] { "e1-d2", "e1-f2" }, Moves(rules, s));
        var afterFox = rules.Apply(s, new Move(30, 26)); // e1-f2
        Assert.Equal(new[] { "b8-a7", "b8-c7", "d8-c7", "d8-e7", "f8-e7", "f8-g7", "h8-g7" }, Moves(rules, afterFox));
    }

    private static string[] Moves(Rules rules, GameState s)
    {
        Span<Move> buffer = stackalloc Move[Rules.MaxMoves];
        int n = rules.GenerateMoves(s, buffer);
        var names = new string[n];
        for (int i = 0; i < n; i++)
        {
            names[i] = rules.Geo.FormatMove(buffer[i]);
        }

        return names;
    }
}
