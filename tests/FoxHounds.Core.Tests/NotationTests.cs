namespace FoxHounds.Core.Tests;

/// <summary>T-CORE-06: notation round-trip and every rejection case of spec 5.6.</summary>
public sealed class NotationTests
{
    [Fact]
    public void Initial_classic_position_notation()
    {
        var rules = new Rules(RuleSet.Classic);
        var s = rules.Initial();
        Assert.Equal("8/e1/b8,d8,f8,h8/F", rules.ToNotation(s));
        Assert.Equal(0b1111UL, (ulong)s.Hounds);
        Assert.Equal(30, s.Fox);
        Assert.Equal("8/e1/b8,d8,f8,h8/F", s.ToString());
    }

    [Fact]
    public void Round_trip_on_10000_random_positions_of_every_size()
    {
        var rng = new Rng(0x707A7104UL);
        foreach (int n in TestPositions.AllSizes)
        {
            var rules = TestPositions.Classic(n);
            for (int i = 0; i < 10_000; i++)
            {
                var s = TestPositions.Random(rules, rng);
                string text = rules.ToNotation(s);
                var parsed = rules.FromNotation(text);
                Assert.Equal(s, parsed);
                Assert.Equal(s.Hash, parsed.Hash);
                Assert.Equal(text, rules.ToNotation(parsed));
            }
        }
    }

    [Fact]
    public void Hounds_may_be_given_in_any_order_but_are_written_ascending()
    {
        var rules = new Rules(RuleSet.Classic);
        var s = rules.FromNotation("8/d4/f6,e7,c7,a7/H");
        Assert.Equal("8/d4/a7,c7,e7,f6/H", rules.ToNotation(s));
    }

    [Theory]
    [InlineData("8/e1/b8,d8,f8,f8/F", "appears twice")]           // duplicate hound
    [InlineData("8/e1/b8,d8,f8,g8/F", "not a playable square")]   // unplayable hound square
    [InlineData("8/e2/b8,d8,f8,h8/F", "not a playable square")]   // unplayable fox square
    [InlineData("8/e1/b8,d8,f8/F", "3 hounds instead of 4")]      // too few hounds
    [InlineData("8/e1/b8,d8,f8,h8,a7/F", "5 hounds instead of 4")] // too many hounds
    [InlineData("10/e1/b8,d8,f8,h8/F", "differs from the ruleset")] // other board size
    [InlineData("8/b8/b8,d8,f8,h8/F", "fox stands on a hound")]   // fox on a hound
    [InlineData("8/e1/b8,d8,f8,h8/X", "must be F or H")]          // bad side
    [InlineData("8/e1/b8,d8,f8,h8", "four parts")]                // missing part
    [InlineData("8/e1/b8,d8,f8,h8/F/x", "four parts")]            // extra part
    [InlineData("x/e1/b8,d8,f8,h8/F", "not a board size")]        // bad size
    [InlineData("8/i1/b8,d8,f8,h8/F", "is not on the 8×8 board")] // off-board file
    [InlineData("8/e9/b8,d8,f8,h8/F", "is not on the 8×8 board")] // off-board rank
    [InlineData("8/e1/b8,,f8,h8/F", "is not on the 8×8 board")]  // empty hound
    [InlineData("8/E1/b8,d8,f8,h8/F", "is not on the 8×8 board")] // uppercase file
    [InlineData(" 8/e1/b8,d8,f8,h8/F", "not a board size")]       // whitespace
    public void Every_rejection_case_throws(string text, string message)
    {
        var rules = new Rules(RuleSet.Classic);
        var ex = Assert.Throws<FormatException>(() => rules.FromNotation(text));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }
}
