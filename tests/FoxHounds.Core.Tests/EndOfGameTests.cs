namespace FoxHounds.Core.Tests;

/// <summary>
/// T-CORE-04: hand-built positions for every rules-level end reason under both win conditions, with adjudication on
/// and off. Expected values come from the reference prototype (reference/core_vectors.txt). Forfeit is a game
/// controller decision and is tested in <see cref="GameRunnerTests"/>.
/// </summary>
public sealed class EndOfGameTests
{
    private const string Ongoing = "Ongoing";

    // Columns: ReachHomeRow adj off, ReachHomeRow adj on, HoundsImmobile adj off, HoundsImmobile adj on, then the
    // escape test under ReachHomeRow and under HoundsImmobile.
    public static TheoryData<string, string, string, string, string, bool, bool> Cases => new()
    {
        // fox trapped in a corner by one hound
        { "8/a1/b2,b8,d8,f8/F", "HoundsWin FoxTrapped", "HoundsWin FoxTrapped", "HoundsWin FoxTrapped", "HoundsWin FoxTrapped", false, false },
        // fox trapped in the middle by four hounds
        { "8/d4/c5,e5,c3,e3/F", "HoundsWin FoxTrapped", "HoundsWin FoxTrapped", "HoundsWin FoxTrapped", "HoundsWin FoxTrapped", false, false },
        // fox on row 0: home under ReachHomeRow, not over under HoundsImmobile
        { "8/d8/b8,f8,h8,e7/H", "FoxWins FoxReachedHome", "FoxWins FoxReachedHome", Ongoing, Ongoing, true, false },
        { "8/d8/b8,f8,h8,e7/F", "FoxWins FoxReachedHome", "FoxWins FoxReachedHome", Ongoing, Ongoing, true, false },
        // hounds immobile on the back row; rule 3 comes before rule 4 (the escape test also passes)
        { "8/b4/a1,c1,e1,g1/H", "FoxWins HoundsImmobile", "FoxWins HoundsImmobile", "FoxWins HoundsImmobile", "FoxWins HoundsImmobile", true, true },
        // fox home and hounds immobile: rule 1 comes first under ReachHomeRow
        { "8/b8/a1,c1,e1,g1/H", "FoxWins FoxReachedHome", "FoxWins FoxReachedHome", "FoxWins HoundsImmobile", "FoxWins HoundsImmobile", true, true },
        // escape exactly at fox.row = minHoundRow (ReachHomeRow only)
        { "8/c5/e5,b4,d4,h4/F", Ongoing, "FoxWins FoxEscaped", Ongoing, Ongoing, true, false },
        { "8/c5/e5,b4,d4,h4/H", Ongoing, "FoxWins FoxEscaped", Ongoing, Ongoing, true, false },
        // fox one row behind the rearmost hound: no escape
        { "8/c5/d6,b4,d4,h4/F", Ongoing, Ongoing, Ongoing, Ongoing, false, false },
        // fox ahead of the hounds with minHoundRow = 2: escaped under both win conditions
        { "8/c7/b6,d6,f6,h6/F", Ongoing, "FoxWins FoxEscaped", Ongoing, "FoxWins FoxEscaped", true, true },
        // HoundsImmobile escape refused when minHoundRow = 1 (fox on row 0)
        { "8/d8/a7,c7,e7,g7/H", "FoxWins FoxReachedHome", "FoxWins FoxReachedHome", Ongoing, Ongoing, true, false },
        // ... and there the fox can indeed be trapped
        { "8/d8/a7,c7,e7,g7/F", "FoxWins FoxReachedHome", "FoxWins FoxReachedHome", "HoundsWin FoxTrapped", "HoundsWin FoxTrapped", true, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Evaluate_matches_the_reference_prototype(
        string notation, string reachOff, string reachOn, string immobileOff, string immobileOn, bool escapedReach, bool escapedImmobile)
    {
        Check(FoxWinCondition.ReachHomeRow, false, reachOff, escapedReach);
        Check(FoxWinCondition.ReachHomeRow, true, reachOn, escapedReach);
        Check(FoxWinCondition.HoundsImmobile, false, immobileOff, escapedImmobile);
        Check(FoxWinCondition.HoundsImmobile, true, immobileOn, escapedImmobile);

        void Check(FoxWinCondition win, bool adjudicate, string expected, bool escaped)
        {
            var rules = new Rules(TestPositions.Variant(8, win, adjudicate));
            var s = rules.FromNotation(notation);
            var outcome = rules.Evaluate(s);
            string actual = outcome.IsOver ? $"{outcome.Result} {outcome.Reason}" : Ongoing;
            Assert.True(expected == actual, $"{notation} under {win}, adjudication {adjudicate}: expected {expected}, got {actual}");
            Assert.Equal(escaped, rules.IsEscaped(s));
        }
    }

    [Fact]
    public void Ongoing_outcome_has_reason_None()
    {
        var rules = TestPositions.Classic(8);
        Assert.Equal(Outcome.Ongoing, rules.Evaluate(rules.Initial()));
        Assert.Equal(EndReason.None, Outcome.Ongoing.Reason);
        Assert.False(Outcome.Ongoing.IsOver);
    }

    [Fact]
    public void A_terminal_position_can_be_a_win_for_the_side_to_move()
    {
        // Spec 6.1: FoxEscaped with the fox to move. Search must not assume terminal = loss.
        var rules = new Rules(RuleSet.Classic);
        var s = rules.FromNotation("8/c5/e5,b4,d4,h4/F");
        var outcome = rules.Evaluate(s);
        Assert.Equal(EndReason.FoxEscaped, outcome.Reason);
        Assert.Equal(s.SideToMove, outcome.Winner);
    }

    [Fact]
    public void MinHoundRow_is_the_row_of_the_rearmost_hound()
    {
        var rules = TestPositions.Classic(8);
        Assert.Equal(0, rules.MinHoundRow(rules.Initial()));
        Assert.Equal(3, rules.MinHoundRow(rules.FromNotation("8/c5/e5,b4,d4,h4/F")));
        Assert.Equal(7, rules.MinHoundRow(rules.FromNotation("8/b4/a1,c1,e1,g1/H")));
    }
}
