namespace FoxHounds.Core.Tests;

/// <summary>T-CORE-08: games never exceed MaxPlies, and the bound is attained (spec 3.7).</summary>
public sealed class GameLengthTests
{
    public static TheoryData<int> Sizes => new(TestPositions.AllSizes);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Ten_thousand_random_games_never_exceed_MaxPlies(int n)
    {
        var rules = TestPositions.Classic(n);
        Assert.Equal((2 * (n / 2) * (n - 1)) + 1, rules.MaxPlies);
        var rng = new Rng(0x1E46UL + (ulong)n);
        int longest = 0;
        for (int game = 0; game < 10_000; game++)
        {
            var start = rules.Initial(rules.FoxStartSquares[rng.NextInt(rules.FoxStartSquares.Count)]);
            int plies = TestPositions.RandomGame(rules, start, rng);
            longest = Math.Max(longest, plies);
        }

        Assert.InRange(longest, 1, rules.MaxPlies);
    }

    [Theory]
    [InlineData(4, 13)]
    [InlineData(6, 31)]
    [InlineData(8, 57)]
    public void Longest_game_by_exhaustive_search_equals_MaxPlies(int n, int expected)
    {
        var rules = TestPositions.Classic(n);
        Assert.Equal(expected, rules.MaxPlies);
        var memo = new Dictionary<GameState, int>();
        Assert.Equal(expected, Longest(rules, rules.Initial(), memo));
    }

    [Fact]
    public void MaxPliesFrom_the_initial_position_equals_MaxPlies_and_shrinks_with_hound_plies()
    {
        var rules = TestPositions.Classic(8);
        var s = rules.Initial();
        Assert.Equal(57, rules.MaxPliesFrom(s));
        s = rules.Apply(s, new Move(30, 26));      // fox ply: unchanged
        Assert.Equal(57, rules.MaxPliesFrom(s));
        s = rules.Apply(s, new Move(0, 4));        // hound ply: two plies fewer
        Assert.Equal(55, rules.MaxPliesFrom(s));
        Assert.Equal(1, rules.HoundRowSum(s));
    }

    private static int Longest(Rules rules, GameState s, Dictionary<GameState, int> memo)
    {
        if (memo.TryGetValue(s, out int known))
        {
            return known;
        }

        int best = 0;
        if (!rules.Evaluate(s).IsOver)
        {
            Span<Move> moves = stackalloc Move[Rules.MaxMoves];
            int count = rules.GenerateMoves(s, moves);
            var children = moves[..count].ToArray();
            foreach (var m in children)
            {
                best = Math.Max(best, 1 + Longest(rules, rules.Apply(s, m), memo));
            }
        }

        memo[s] = best;
        return best;
    }
}
