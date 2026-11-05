using FoxHounds.Core;

namespace FoxHounds.Tuning.Tests;

/// <summary>T-TUNE-01 and T-TUNE-02: the tuners on synthetic problems with known answers.</summary>
public sealed class TunerTests
{
    private static readonly int[] TrueWeights = [120, -75, 180, 50, -240];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Regression_recovers_a_known_linear_model_from_probabilistic_labels()
    {
        // T-TUNE-01 (fast form): labels are the model's own win probabilities, so the optimum is the true weights.
        var train = Synthetic(20_000, seed: 1, sampled: false);
        var validation = Synthetic(5_000, seed: 2, sampled: false);
        var result = RegressionTuner.Fit(train, validation, new RegressionOptions { Lambda = 0 }, Ct);
        AssertWithin(result.Weights, 0.05);
        Assert.InRange(result.Intercept, -0.05, 0.05); // the data has no intercept
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Regression_recovers_a_known_linear_model_from_sampled_labels()
    {
        // T-TUNE-01: labels drawn as wins and losses; 400,000 rows put every weight's standard error under 1 point.
        var train = Synthetic(400_000, seed: 3, sampled: true);
        var validation = Synthetic(50_000, seed: 4, sampled: true);
        var result = RegressionTuner.Fit(train, validation, new RegressionOptions(), Ct);
        AssertWithin(result.Weights, 0.05);
    }

    [Fact]
    public void Regression_is_deterministic_and_reports_its_fit()
    {
        var train = Synthetic(4_000, seed: 5, sampled: true);
        var validation = Synthetic(1_000, seed: 6, sampled: true);
        var a = RegressionTuner.Fit(train, validation, null, Ct);
        var b = RegressionTuner.Fit(train, validation, null, Ct);
        Assert.Equal(a.Weights, b.Weights);
        Assert.Equal(a.ValidationLoss, b.ValidationLoss);
        Assert.InRange(a.ValidationAccuracy, 0.6, 1.0);
        Assert.True(a.BestStep <= a.Steps);
        Assert.True(a.ValidationLoss < Math.Log(2)); // better than a coin
    }

    [Fact]
    public void Spsa_converges_on_a_noisy_quadratic_and_the_same_seed_gives_the_same_run()
    {
        // T-TUNE-02.
        // Each weight starts a few scale units away, the regime SPSA is meant for: s = max(1, |θ| / 10).
        double[] target = [40, -12, 250, 3];
        double[] start = [30, -8, 200, 0];
        var objective = new Quadratic(target, noise: 0.05);
        var options = new SpsaOptions { Iterations = 500, Seed = 42 };
        var run = Spsa.Run(start, objective, options, null, Ct);
        for (int i = 0; i < target.Length; i++)
        {
            double scale = Math.Max(1, Math.Abs(start[i]) / 10);
            Assert.True(Math.Abs(run.Best[i] - target[i]) <= scale, $"weight {i}: {run.Best[i]} vs {target[i]} (scale {scale})");
        }

        Assert.True(run.BestValidation > run.StartValidation);
        var again = Spsa.Run(start, objective, options, null, Ct);
        Assert.Equal(run.Best, again.Best);
        Assert.Equal(run.Gain, again.Gain);
        Assert.Equal(run.Steps.Select(s => s.Theta), again.Steps.Select(s => s.Theta));
        var other = Spsa.Run(start, objective, options with { Seed = 43 }, null, Ct);
        Assert.NotEqual(run.Steps[^1].Theta, other.Steps[^1].Theta);
    }

    [Fact]
    public void The_calibrated_first_step_moves_each_weight_by_about_one_scale_unit()
    {
        // Decision D-16: with TargetStep = 1, |θ₁ − θ₀| ≈ s for every weight.
        double[] start = [100, -40, 5];
        var objective = new Quadratic([0, 0, 0], noise: 0);
        var run = Spsa.Run(start, objective, new SpsaOptions { Iterations = 1, Seed = 7 }, null, Ct);
        for (int i = 0; i < start.Length; i++)
        {
            double scale = Math.Max(1, Math.Abs(start[i]) / 10);
            double moved = Math.Abs(run.Steps[0].Theta[i] - start[i]);
            Assert.InRange(moved / scale, 0.2, 5.0);
        }
    }

    [Fact]
    public void A_perturbation_that_rounding_would_erase_is_reported()
    {
        var run = Spsa.Run([5, 5], new Quadratic([0, 0], 0), new SpsaOptions { Iterations = 2, C = 0.5, Seed = 1 }, null, Ct);
        Assert.Contains(run.Warnings, w => w.Contains("rounding", StringComparison.Ordinal));
    }

    [Fact]
    public void The_csv_has_a_row_per_iteration_with_the_validation_every_fifty()
    {
        var run = Spsa.Run([1, 2], new Quadratic([0, 0], 0), new SpsaOptions { Iterations = 100, Seed = 3 }, null, Ct);
        string[] lines = run.ToCsv(["a", "b"]).TrimEnd('\n').Split('\n');
        Assert.Equal("iteration,a,b,jPlus,jMinus,validation", lines[0]);
        Assert.Equal(101, lines.Length);
        Assert.Equal(2, lines.Skip(1).Count(l => !l.EndsWith(',')));
    }

    private static void AssertWithin(int[] weights, double tolerance)
    {
        for (int i = 0; i < TrueWeights.Length; i++)
        {
            double error = Math.Abs(weights[i] - TrueWeights[i]) / Math.Abs((double)TrueWeights[i]);
            Assert.True(error <= tolerance, $"weight {i}: {weights[i]} vs {TrueWeights[i]} ({error:P1})");
        }
    }

    /// <summary>Features uniform in 0…4; label the win probability σ(w·x / 400), or a draw from it.</summary>
    private static FeatureSet Synthetic(int rows, ulong seed, bool sampled)
    {
        var rng = new Rng(seed);
        int d = TrueWeights.Length;
        var x = new double[rows * d];
        var y = new double[rows];
        for (int i = 0; i < rows; i++)
        {
            double z = 0;
            for (int j = 0; j < d; j++)
            {
                x[(i * d) + j] = rng.NextInt(5);
                z += TrueWeights[j] * x[(i * d) + j] / 400.0;
            }

            double p = 1 / (1 + Math.Exp(-z));
            y[i] = sampled ? (rng.NextDouble() < p ? 1 : 0) : p;
        }

        return FeatureSet.FromRows(x, y, d);
    }

    /// <summary>J(w) = −Σ ((wᵢ − targetᵢ) / sᵢ)² plus batch noise that both evaluations of an iteration share.</summary>
    private sealed class Quadratic(double[] target, double noise) : ISpsaObjective
    {
        public int[] DrawBatch(Rng rng) => [rng.NextInt(1_000_000)];

        public (double Plus, double Minus) Evaluate(int[] plus, int[] minus, int[] batch, Rng rng, CancellationToken ct)
        {
            double shared = noise * ((batch[0] / 1_000_000.0) - 0.5);
            return (J(plus) + shared, J(minus) + shared);
        }

        public double Validate(int[] weights, CancellationToken ct) => J(weights);

        private double J(int[] w)
        {
            double sum = 0;
            for (int i = 0; i < w.Length; i++)
            {
                double s = Math.Max(1, Math.Abs(target[i]) / 10);
                sum += ((w[i] - target[i]) / s) * ((w[i] - target[i]) / s);
            }

            return -sum / 100;
        }
    }
}
