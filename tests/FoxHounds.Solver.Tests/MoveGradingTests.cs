using FoxHounds.Core;

namespace FoxHounds.Solver.Tests;

/// <summary>The viewer's move colours and the harness's blunders (spec 11.5, 13.5), checked on every position.</summary>
public sealed class MoveGradingTests
{
    public static TheoryData<int, string> Boards => new() { { 5, "classic" }, { 6, "classic" }, { 6, "gilith" }, { 7, "classic" } };

    public static TheoryData<int, string, bool> GameRules => new()
    {
        { 6, "classic", true }, { 6, "classic", false }, { 6, "gilith", true }, { 6, "gilith", false }, { 7, "classic", true },
    };

    [Theory]
    [MemberData(nameof(Boards))]
    public void Every_position_is_graded_as_the_definitions_say(int n, string condition)
    {
        var table = TestTables.Get(n, TestTables.ParseCondition(condition));
        var rules = table.Rules;
        var graded = new GradedMove[Rules.MaxMoves];
        var moves = new Move[Rules.MaxMoves];
        int positions = 0;
        for (long e = 0; e < table.EntryCount; e++)
        {
            if (!table.TryGetPosition(e, out var s))
            {
                continue;
            }

            if (rules.Evaluate(s).IsOver)
            {
                Assert.Equal(0, MoveGrading.Grade(table, s, graded));
                continue;
            }

            positions++;
            int count = MoveGrading.Grade(table, s, graded);
            Assert.Equal(rules.GenerateMoves(s, moves), count);
            var value = table.Get(s);
            int fastest = 0;
            for (int i = 0; i < count; i++)
            {
                // Computed here from the children directly, not through RateMoves.
                var child = table.Get(rules.Apply(s, moves[i]));
                var expected = new SolvedValue(!child.SideToMoveWins, child.Plies + 1);
                var quality = !value.SideToMoveWins ? MoveQuality.AlreadyLost
                    : !expected.SideToMoveWins ? MoveQuality.Blunder
                    : expected.Plies == value.Plies ? MoveQuality.Fastest
                    : MoveQuality.Slower;
                Assert.Equal(new GradedMove(moves[i], expected, quality), graded[i]);
                fastest += quality == MoveQuality.Fastest ? 1 : 0;
            }

            // A won position has a ply that keeps the win at its distance; a lost one has none.
            Assert.Equal(value.SideToMoveWins, fastest > 0);
        }

        Assert.True(positions > 1000, $"only {positions} positions");
    }

    [Theory]
    [MemberData(nameof(GameRules))]
    public void The_blunder_judgement_agrees_with_the_colours_under_the_game_rules(int n, string condition, bool adjudicate)
    {
        var win = TestTables.ParseCondition(condition);
        var table = TestTables.Get(n, win);
        var rules = TestTables.Rules(n, win, adjudicate: adjudicate);
        var graded = new GradedMove[Rules.MaxMoves];
        int blunders = 0;
        int escapes = 0;
        for (long e = 0; e < table.EntryCount; e++)
        {
            if (!table.TryGetPosition(e, out var s) || rules.Evaluate(s).IsOver)
            {
                continue;
            }

            bool won = table.Get(s).SideToMoveWins;
            int count = MoveGrading.Grade(table, s, graded);
            for (int i = 0; i < count; i++)
            {
                var verdict = MoveGrading.Judge(rules, table, s, graded[i].Move);
                Assert.Equal(won, verdict.MoverWasWinning);
                Assert.Equal(graded[i].Quality == MoveQuality.Blunder, verdict.Blunder);
                blunders += verdict.Blunder ? 1 : 0;
                var after = rules.Apply(s, graded[i].Move);
                escapes += rules.Evaluate(after).Reason == EndReason.FoxEscaped ? 1 : 0;
            }
        }

        Assert.True(blunders > 100, $"only {blunders} blunders");
        Assert.True(adjudicate == escapes > 0, $"{escapes} escapes with adjudication {adjudicate}");
    }

    [Fact]
    public void A_game_is_judged_ply_by_ply()
    {
        // 6 x 6 Classic, fox first: the fox wins in 25 (spec 10.7). The fox plays a fastest ply until it can throw the
        // win away, then does; the hounds, lost throughout, play their first legal ply.
        var table = TestTables.Classic(6);
        var rules = TestTables.Rules(6, FoxWinCondition.ReachHomeRow, adjudicate: true);
        var start = rules.Initial();
        Assert.True(table.Get(start).SideToMoveWins);
        var graded = new GradedMove[Rules.MaxMoves];
        var plies = new List<Move>();
        var s = start;
        while (true)
        {
            Assert.False(rules.Evaluate(s).IsOver, "the fox never had a losing ply");
            var options = graded.AsSpan(0, MoveGrading.Grade(table, s, graded)).ToArray();
            var losing = options.Where(g => g.Quality == MoveQuality.Blunder).ToList();
            if (s.SideToMove == Side.Fox && losing.Count > 0)
            {
                plies.Add(losing[0].Move);
                break;
            }

            var move = s.SideToMove == Side.Fox ? options.First(g => g.Quality == MoveQuality.Fastest).Move : options[0].Move;
            plies.Add(move);
            s = rules.Apply(s, move);
        }

        var verdicts = MoveGrading.JudgeGame(rules, table, start, plies);
        var foxPly = Enumerable.Range(0, plies.Count).Select(i => i % 2 == 0).ToArray();
        Assert.Equal(foxPly, verdicts.Select(v => v.MoverWasWinning));
        Assert.Equal(Enumerable.Range(0, plies.Count).Select(i => i == plies.Count - 1), verdicts.Select(v => v.Blunder));
    }
}
