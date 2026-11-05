using FoxHounds.Core;

namespace FoxHounds.Solver.Tests;

/// <summary>T-SOL-05: reachable positions from the initial position, counting side to move and including terminal
/// positions, equal spec 15.2.</summary>
public sealed class ReachableCountTests
{
    public static TheoryData<string, int, int> Counts => new()
    {
        { "classic-off", 4, 114 }, { "classic-off", 5, 546 }, { "classic-off", 6, 10_357 }, { "classic-off", 7, 37_583 },
        { "classic-on", 4, 88 }, { "classic-on", 5, 407 }, { "classic-on", 6, 8_705 }, { "classic-on", 7, 31_182 },
        { "gilith", 4, 132 }, { "gilith", 5, 593 }, { "gilith", 6, 11_349 }, { "gilith", 7, 40_226 },
    };

    [Theory]
    [MemberData(nameof(Counts))]
    public void Reachable_counts_match_spec_15_2(string ruleset, int n, int expected) =>
        Assert.Equal(expected, Reachable(Rules(ruleset, n)).Total);

    [Fact]
    [Trait("Category", "Slow")]
    public void Reachable_counts_match_spec_15_2_on_8x8()
    {
        var off = Reachable(Rules("classic-off", 8));
        Assert.Equal(895_280, off.Total);
        Assert.Equal(61_945, off.Terminal);
        Assert.Equal(783_184, Reachable(Rules("classic-on", 8)).Total);
        Assert.Equal(957_529, Reachable(Rules("gilith", 8)).Total);
    }

    private static Rules Rules(string ruleset, int n) => ruleset switch
    {
        "classic-off" => new Rules(RuleSet.Classic with { BoardSize = n, AdjudicateEscape = false }),
        "classic-on" => new Rules(RuleSet.Classic with { BoardSize = n, AdjudicateEscape = true }),
        _ => new Rules(RuleSet.Gilith with { BoardSize = n }),
    };

    private static (int Total, int Terminal) Reachable(Rules rules)
    {
        var start = rules.Initial();
        var seen = new HashSet<GameState> { start };
        var queue = new Queue<GameState>();
        queue.Enqueue(start);
        int terminal = 0;
        var moves = new Move[Core.Rules.MaxMoves];
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            if (rules.Evaluate(s).IsOver)
            {
                terminal++;
                continue;
            }

            int count = rules.GenerateMoves(s, moves);
            for (int i = 0; i < count; i++)
            {
                var child = rules.Apply(s, moves[i]);
                if (seen.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return (seen.Count, terminal);
    }
}
