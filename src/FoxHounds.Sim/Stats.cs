namespace FoxHounds.Sim;

/// <summary>A two-sided interval.</summary>
/// <param name="Low">Lower end.</param>
/// <param name="High">Upper end.</param>
public readonly record struct Interval(double Low, double High);

/// <summary>A proportion with its 95% Wilson interval.</summary>
/// <param name="Successes">Count of successes.</param>
/// <param name="Trials">Count of trials.</param>
/// <param name="Rate">Successes / trials (0 when there are no trials).</param>
/// <param name="Interval">Wilson score interval.</param>
public sealed record Proportion(long Successes, long Trials, double Rate, Interval Interval)
{
    /// <summary>The proportion for <paramref name="successes"/> out of <paramref name="trials"/>.</summary>
    public static Proportion Of(long successes, long trials) =>
        new(successes, trials, trials == 0 ? 0 : (double)successes / trials, Stats.Wilson(successes, trials));
}

/// <summary>Head-to-head result of agent A over pairs of games (spec 11.5).</summary>
/// <param name="Pairs">Complete pairs (one game in each role).</param>
/// <param name="Score">Mean of A's pair scores, each 0, ½ or 1.</param>
/// <param name="ScoreInterval">Normal 95% interval from the sample variance.</param>
/// <param name="Elo">−400 · log₁₀(1/S − 1); infinite at S = 0 or 1.</param>
/// <param name="EloInterval">The score interval's ends transformed the same way.</param>
/// <param name="Variance">Sample variance of the pair scores (n − 1 denominator).</param>
public sealed record HeadToHeadScore(int Pairs, double Score, Interval ScoreInterval, double Elo, Interval EloInterval, double Variance);

/// <summary>Settings of the optional sequential test (spec 11.5).</summary>
/// <param name="Elo0">Elo under H0.</param>
/// <param name="Elo1">Elo under H1.</param>
/// <param name="Alpha">False-positive rate.</param>
/// <param name="Beta">False-negative rate.</param>
/// <param name="MinPairs">No decision before this many pairs.</param>
public sealed record SprtSettings(double Elo0 = 0, double Elo1 = 10, double Alpha = 0.05, double Beta = 0.05, int MinPairs = 50);

/// <summary>Where a sequential test stands.</summary>
public enum SprtDecision
{
    /// <summary>Not enough evidence either way.</summary>
    Continue,

    /// <summary>The LLR reached the upper bound.</summary>
    AcceptH1,

    /// <summary>The LLR reached the lower bound.</summary>
    AcceptH0,
}

/// <summary>A sequential test's state after some pairs.</summary>
/// <param name="Llr">Log-likelihood ratio.</param>
/// <param name="Lower">Accept H0 at or below this.</param>
/// <param name="Upper">Accept H1 at or above this.</param>
/// <param name="Decision">The decision.</param>
public sealed record SprtState(double Llr, double Lower, double Upper, SprtDecision Decision);

/// <summary>The statistics of spec 11.5, each implemented once and tested against hand-computed values (T-SIM-03).</summary>
public static class Stats
{
    /// <summary>The normal quantile for 95% intervals.</summary>
    public const double Z95 = 1.96;

    /// <summary>Wilson score interval for <paramref name="successes"/> out of <paramref name="trials"/>.</summary>
    /// <remarks>With no trials the interval is [0, 1].</remarks>
    public static Interval Wilson(long successes, long trials, double z = Z95)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, successes);
        if (trials == 0)
        {
            return new Interval(0, 1);
        }

        double n = trials;
        double p = successes / n;
        double z2 = z * z;
        double denominator = 1 + (z2 / n);
        double centre = (p + (z2 / (2 * n))) / denominator;
        double half = z * Math.Sqrt((p * (1 - p) / n) + (z2 / (4 * n * n))) / denominator;
        return new Interval(Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>Elo difference for a mean score: −400 · log₁₀(1/S − 1), infinite at S = 0 or 1.</summary>
    public static double Elo(double score) => score switch
    {
        <= 0 => double.NegativeInfinity,
        >= 1 => double.PositiveInfinity,
        _ => -400 * Math.Log10((1 / score) - 1),
    };

    /// <summary>Expected score for an Elo difference: 1 / (1 + 10^(−elo/400)).</summary>
    public static double ScoreOfElo(double elo) => 1 / (1 + Math.Pow(10, -elo / 400));

    /// <summary>Sample mean and sample variance with the n − 1 denominator (0 for fewer than two values).</summary>
    public static (double Mean, double Variance) MeanAndVariance(ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
        {
            return (0, 0);
        }

        double sum = 0;
        foreach (double v in values)
        {
            sum += v;
        }

        double mean = sum / values.Length;
        if (values.Length < 2)
        {
            return (mean, 0);
        }

        double squares = 0;
        foreach (double v in values)
        {
            squares += (v - mean) * (v - mean);
        }

        return (mean, squares / (values.Length - 1));
    }

    /// <summary>Head-to-head summary from A's pair scores (each 0, ½ or 1).</summary>
    public static HeadToHeadScore HeadToHead(ReadOnlySpan<double> pairScores)
    {
        var (mean, variance) = MeanAndVariance(pairScores);
        int n = pairScores.Length;
        double half = n == 0 ? 0.5 : Z95 * Math.Sqrt(variance / n);
        var interval = n == 0 ? new Interval(0, 1) : new Interval(Math.Max(0, mean - half), Math.Min(1, mean + half));
        return new HeadToHeadScore(n, mean, interval, Elo(mean), new Interval(Elo(interval.Low), Elo(interval.High)), variance);
    }

    /// <summary>
    /// SPRT log-likelihood ratio (spec 11.5): (Σx − n(s₀ + s₁)/2) · (s₁ − s₀) / σ̂², with sᵢ the expected score at
    /// eloᵢ and σ̂² the sample variance of the pair scores. With zero variance the ratio is ±∞ (0 if the sum is exactly
    /// halfway).
    /// </summary>
    public static double SprtLlr(double sum, int pairs, double variance, double elo0, double elo1)
    {
        double s0 = ScoreOfElo(elo0);
        double s1 = ScoreOfElo(elo1);
        double numerator = (sum - (pairs * (s0 + s1) / 2)) * (s1 - s0);
        if (variance > 0)
        {
            return numerator / variance;
        }

        return numerator > 0 ? double.PositiveInfinity : numerator < 0 ? double.NegativeInfinity : 0;
    }

    /// <summary>The sequential test after the given pair scores.</summary>
    public static SprtState Sprt(ReadOnlySpan<double> pairScores, SprtSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double lower = Math.Log(settings.Beta / (1 - settings.Alpha));
        double upper = Math.Log((1 - settings.Beta) / settings.Alpha);
        var (mean, variance) = MeanAndVariance(pairScores);
        double llr = SprtLlr(mean * pairScores.Length, pairScores.Length, variance, settings.Elo0, settings.Elo1);
        var decision = pairScores.Length < settings.MinPairs ? SprtDecision.Continue
            : llr >= upper ? SprtDecision.AcceptH1
            : llr <= lower ? SprtDecision.AcceptH0
            : SprtDecision.Continue;
        return new SprtState(llr, lower, upper, decision);
    }
}
