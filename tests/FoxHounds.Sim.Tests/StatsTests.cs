namespace FoxHounds.Sim.Tests;

/// <summary>T-SIM-03: the statistics of spec 11.5 against the values in 15.3, to 4 decimal places.</summary>
public sealed class StatsTests
{
    [Theory]
    [InlineData(45, 50, 0.7864, 0.9565)]
    [InlineData(990, 1000, 0.9817, 0.9946)]
    public void Wilson_intervals_match_the_fixed_values(int successes, int trials, double low, double high)
    {
        var interval = Stats.Wilson(successes, trials);
        Assert.Equal(low, interval.Low, 4);
        Assert.Equal(high, interval.High, 4);
    }

    [Fact]
    public void Wilson_intervals_stay_inside_zero_and_one_and_are_widest_with_no_trials()
    {
        Assert.Equal(new Interval(0, 1), Stats.Wilson(0, 0));
        var none = Stats.Wilson(0, 20);
        Assert.Equal(0, none.Low);
        Assert.True(none.High is > 0 and < 0.2);
        var all = Stats.Wilson(20, 20);
        Assert.Equal(1, all.High);
        Assert.True(all.Low is > 0.8 and < 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => Stats.Wilson(3, 2));
    }

    [Fact]
    public void Elo_for_a_score_of_sixty_percent_matches_the_fixed_value()
    {
        Assert.Equal(70.44, Stats.Elo(0.6), 2);
        Assert.Equal(0, Stats.Elo(0.5), 10);
        Assert.Equal(-Stats.Elo(0.6), Stats.Elo(0.4), 10);
        Assert.Equal(double.PositiveInfinity, Stats.Elo(1));
        Assert.Equal(double.NegativeInfinity, Stats.Elo(0));
        Assert.Equal(0.6, Stats.ScoreOfElo(Stats.Elo(0.6)), 12);
    }

    [Fact]
    public void The_SPRT_example_matches_the_fixed_value_and_accepts_H1()
    {
        double[] pairs = [.. Enumerable.Repeat(1.0, 60), .. Enumerable.Repeat(0.5, 30), .. Enumerable.Repeat(0.0, 10)];
        var (mean, variance) = Stats.MeanAndVariance(pairs);
        Assert.Equal(0.75, mean, 10);
        Assert.Equal(0.113636, variance, 6);
        Assert.Equal(3.0741, Stats.SprtLlr(75, 100, variance, 0, 10), 4);

        var state = Stats.Sprt(pairs, new SprtSettings());
        Assert.Equal(3.0741, state.Llr, 4);
        Assert.Equal(2.9444, state.Upper, 4);   // ln(0.95 / 0.05)
        Assert.Equal(-2.9444, state.Lower, 4);
        Assert.Equal(SprtDecision.AcceptH1, state.Decision);
    }

    [Fact]
    public void The_SPRT_waits_for_fifty_pairs_and_can_accept_H0()
    {
        double[] early = [.. Enumerable.Repeat(1.0, 49)];
        Assert.Equal(SprtDecision.Continue, Stats.Sprt(early, new SprtSettings()).Decision);
        Assert.Equal(double.PositiveInfinity, Stats.Sprt(early, new SprtSettings()).Llr); // zero variance

        // Even play: about 28,400 · σ̂² pairs are needed to reject +10 Elo; here σ̂² = 0.05, so 1,600 pairs suffice.
        double[] even = [.. Enumerable.Repeat(0.5, 1280), .. Enumerable.Repeat(1.0, 160), .. Enumerable.Repeat(0.0, 160)];
        var state = Stats.Sprt(even, new SprtSettings());
        Assert.Equal(-3.31, state.Llr, 2);
        Assert.Equal(SprtDecision.AcceptH0, state.Decision);
    }

    [Fact]
    public void Head_to_head_reports_the_mean_score_with_a_normal_interval_and_its_Elo()
    {
        double[] pairs = [.. Enumerable.Repeat(1.0, 60), .. Enumerable.Repeat(0.5, 30), .. Enumerable.Repeat(0.0, 10)];
        var h = Stats.HeadToHead(pairs);
        Assert.Equal(100, h.Pairs);
        Assert.Equal(0.75, h.Score, 10);
        double half = 1.96 * Math.Sqrt(0.113636 / 100);
        Assert.Equal(0.75 - half, h.ScoreInterval.Low, 4);
        Assert.Equal(0.75 + half, h.ScoreInterval.High, 4);
        Assert.Equal(190.85, h.Elo, 2);           // −400 · log₁₀(1/0.75 − 1)
        Assert.Equal(Stats.Elo(h.ScoreInterval.Low), h.EloInterval.Low, 10);
        Assert.Equal(Stats.Elo(h.ScoreInterval.High), h.EloInterval.High, 10);
    }

    [Fact]
    public void A_proportion_carries_its_rate_and_interval()
    {
        var p = Proportion.Of(45, 50);
        Assert.Equal(0.9, p.Rate, 10);
        Assert.Equal(Stats.Wilson(45, 50), p.Interval);
        Assert.Equal(0, Proportion.Of(0, 0).Rate);
    }
}
