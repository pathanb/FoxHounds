using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.Sim.Tests;

/// <summary>Opening sets and position suites (spec 11.2, 14.5; decisions D-15, D-28).</summary>
public sealed class OpeningsTests
{
    private static readonly RuleSet Eight = RuleSet.Classic with { AdjudicateEscape = false };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SolverTable Table8 => SimFixture.Tables.GetOrBuild(new Rules(Eight), null, Ct);

    [Fact]
    public void The_spec_example_lines_carry_the_labels_our_solver_gives()
    {
        // Spec 14.5: three real 6-ply openings labelled by the reference prototype.
        var file = PositionFile.Parse("""
            # fh openings --n 8 --plies 6 --count 2000 --seed 7 --label --filter any
            # ruleSet=classic adjudicateEscape=false
            8/b4/b8,h8,e7,d6/F	F7
            8/f2/f8,h8,e7,d6/F	F15
            8/d2/b8,f8,e7,h6/F	F11
            """, "spec 14.5", Eight);
        Assert.Equal(3, file.Positions.Count);
        foreach (var p in file.Positions)
        {
            Assert.Equal(Table8.Get(p.Position), p.Value);
        }
    }

    [Fact]
    public void Openings_are_distinct_unfinished_correctly_labelled_and_filtered()
    {
        var options = new OpeningOptions { RuleSet = Eight, MinPlies = 6, MaxPlies = 16, Count = 300, Seed = 7, Filter = OpeningFilter.HoundsWon };
        var result = Openings.Generate(options, Table8, Ct);
        var rules = new Rules(Eight);
        Assert.Equal(300, result.Positions.Count);
        Assert.Equal(300, result.Positions.Select(p => p.Position).Distinct().Count());
        foreach (var p in result.Positions)
        {
            Assert.False(rules.Evaluate(p.Position).IsOver);
            Assert.Equal(Table8.Get(p.Position), p.Value);
            Assert.Equal(Side.Hounds, p.Winner);
        }

        Assert.Equal(result.Attempts, result.Positions.Count + result.Terminal + result.Duplicates + result.Filtered);
        Assert.Equal(result.Positions, Openings.Generate(options, Table8, Ct).Positions);         // same seed, same set
        Assert.NotEqual(result.Positions, Openings.Generate(options with { Seed = 8 }, Table8, Ct).Positions);
    }

    [Fact]
    public void Exactly_six_plies_cannot_give_a_thousand_distinct_openings()
    {
        // Decision D-15: after exactly 6 plies there are only 544 distinct positions on 8x8.
        var result = Openings.Generate(new OpeningOptions { RuleSet = Eight, MinPlies = 6, MaxPlies = 6, Count = 1000, Label = false, MaxAttempts = 200_000 }, null, Ct);
        Assert.Equal(544, result.Positions.Count);
    }

    [Fact]
    public void The_noisy_solver_generator_labels_its_openings()
    {
        var result = Openings.Generate(new OpeningOptions
        {
            RuleSet = Eight,
            MinPlies = 6,
            MaxPlies = 12,
            Count = 40,
            Policy = OpeningPolicy.NoisySolver,
            Epsilon = 0.3,
            Filter = OpeningFilter.FoxWon,
        }, Table8, Ct);
        Assert.Equal(40, result.Positions.Count);
        Assert.All(result.Positions, p => Assert.Equal(Side.Fox, p.Winner));
    }

    [Fact]
    public void Without_a_solver_labels_and_filters_are_refused()
    {
        var twelve = RuleSet.Classic with { BoardSize = 12 };
        Assert.Throws<ArgumentException>(() => Openings.Generate(new OpeningOptions { RuleSet = twelve, Count = 5 }, null, Ct));
        Assert.Throws<ArgumentException>(() => Openings.Generate(new OpeningOptions { RuleSet = twelve, Count = 5, Label = false, Filter = OpeningFilter.FoxWon }, null, Ct));
        var plain = Openings.Generate(new OpeningOptions { RuleSet = twelve, Count = 5, Label = false }, null, Ct);
        Assert.All(plain.Positions, p => Assert.Null(p.Value));
    }

    [Fact]
    public void A_position_file_survives_a_round_trip_and_rejects_other_rules_and_bad_labels()
    {
        var result = Openings.Generate(new OpeningOptions { RuleSet = Eight, MinPlies = 4, MaxPlies = 10, Count = 50, Seed = 1 }, Table8, Ct);
        var file = new PositionFile(Eight, ["made by a test"], result.Positions);
        var again = PositionFile.Parse(file.ToText(), "round trip");
        Assert.Equal(Eight, again.RuleSet);
        Assert.Equal(file.Positions, again.Positions);
        Assert.Equal(["made by a test"], again.Comments);

        Assert.Throws<InvalidDataException>(() => PositionFile.Parse(file.ToText(), "x", RuleSet.Gilith));
        Assert.Throws<InvalidDataException>(() => PositionFile.Parse(file.ToText(), "x", RuleSet.Classic with { BoardSize = 6 }));
        Assert.Throws<InvalidDataException>(() => PositionFile.Parse("8/b4/b8,h8,e7,d6/F\tX7\n", "x", Eight));
        Assert.Throws<InvalidDataException>(() => PositionFile.Parse("8/b4/b8,h8,e7,d6/F\tF0\n", "x", Eight));
        Assert.Throws<InvalidDataException>(() => PositionFile.Parse("8/z9/b8,h8,e7,d6/F\tF7\n", "x", Eight));
        Assert.Throws<InvalidDataException>(() => PositionFile.Parse("8/b4/b8,h8,e7,d6/F\tF7\n", "no rules"));
    }

    [Fact]
    public void A_suite_fills_its_strata_in_disjoint_splits_the_same_way_with_any_worker_count()
    {
        var options = new SuiteOptions { RuleSet = Eight, Train = 160, Validation = 40, Test = 80, Seed = 5, MaxGames = 200_000 };
        var one = Suites.Generate(options, Table8, null, Ct);
        var two = Suites.Generate(options with { Workers = 2 }, Table8, null, Ct);
        Assert.Equal(one.Train, two.Train);
        Assert.Equal(one.Validation, two.Validation);
        Assert.Equal(one.Test, two.Test);
        Assert.Empty(one.Shortfall);
        Assert.Equal((160, 40, 80), (one.Train.Count, one.Validation.Count, one.Test.Count));

        var all = one.Train.Concat(one.Validation).Concat(one.Test).ToList();
        Assert.Equal(all.Count, all.Select(p => p.Position).Distinct().Count()); // no position in two splits
        int maxMovesLeft = 4 * 7;
        foreach (var split in new[] { one.Train, one.Validation, one.Test })
        {
            var strata = split.GroupBy(p => (Suites.Phase(p.Position, maxMovesLeft), p.Winner)).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(8, strata.Count);
            Assert.Single(strata.Values.Distinct()); // equal quotas
        }

        Assert.All(all, p => Assert.Equal(Table8.Get(p.Position), p.Value));
    }
}
