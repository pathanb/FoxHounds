using System.Globalization;
using System.Text;
using FoxHounds.Core;

namespace FoxHounds.Tuning;

/// <summary>What SPSA maximises (spec 12.3): two weight vectors scored on the same batch.</summary>
public interface ISpsaObjective
{
    /// <summary>Draws the batch of one iteration from its random stream, after Δ (decision D-16).</summary>
    int[] DrawBatch(Rng rng);

    /// <summary>J for each of two integer weight vectors on the same batch (higher is better).</summary>
    (double Plus, double Minus) Evaluate(int[] plus, int[] minus, int[] batch, Rng rng, CancellationToken ct);

    /// <summary>The validation score of a weight vector.</summary>
    double Validate(int[] weights, CancellationToken ct);
}

/// <summary>Settings of the SPSA tuner (spec 12.3, decision D-16).</summary>
public sealed record SpsaOptions
{
    /// <summary>Iterations.</summary>
    public int Iterations { get; init; } = 500;

    /// <summary>Gain a; null calibrates it before iteration 0 (D-16).</summary>
    public double? Gain { get; init; }

    /// <summary>Perturbation size c.</summary>
    public double C { get; init; } = 2.0;

    /// <summary>Stability constant A; null means Iterations / 10.</summary>
    public double? Stability { get; init; }

    /// <summary>Calibrated first steps move each weight by about this many scale units.</summary>
    public double TargetStep { get; init; } = 1.0;

    /// <summary>Trial perturbations for the calibration.</summary>
    public int CalibrationTrials { get; init; } = 10;

    /// <summary>Validate every this many iterations, and keep the best.</summary>
    public int ValidateEvery { get; init; } = 50;

    /// <summary>Iteration k draws Δ and its batch from Rng.ForGame(seed, k).</summary>
    public ulong Seed { get; init; }

    /// <summary>Per-weight scales s; null means max(1, |θᵢ| / 10).</summary>
    public double[]? Scales { get; init; }
}

/// <summary>One row of tuning.csv.</summary>
/// <param name="Iteration">k, from 0.</param>
/// <param name="Theta">θ after the update.</param>
/// <param name="Plus">J⁺.</param>
/// <param name="Minus">J⁻.</param>
/// <param name="Validation">The validation score of round(θ), when this iteration validated.</param>
public sealed record SpsaStep(int Iteration, double[] Theta, double Plus, double Minus, double? Validation);

/// <summary>The outcome of an SPSA run.</summary>
/// <param name="Best">The best weights seen at a validation.</param>
/// <param name="BestValidation">Their validation score.</param>
/// <param name="StartValidation">The starting weights' validation score.</param>
/// <param name="Gain">The gain a used (calibrated or given).</param>
/// <param name="Steps">Every iteration.</param>
/// <param name="Warnings">Problems worth reporting (a perturbation erased by rounding, a failed calibration).</param>
public sealed record SpsaResult(int[] Best, double BestValidation, double StartValidation, double Gain,
    IReadOnlyList<SpsaStep> Steps, IReadOnlyList<string> Warnings)
{
    /// <summary>tuning.csv: iteration, θ, J⁺, J⁻, validation score (spec 12.3).</summary>
    public string ToCsv(IReadOnlyList<string> weightNames)
    {
        ArgumentNullException.ThrowIfNull(weightNames);
        var sb = new StringBuilder("iteration,");
        sb.Append(string.Join(',', weightNames)).Append(",jPlus,jMinus,validation\n");
        foreach (var step in Steps)
        {
            sb.Append(step.Iteration.ToString(CultureInfo.InvariantCulture));
            foreach (double t in step.Theta)
            {
                sb.Append(',').Append(t.ToString("0.####", CultureInfo.InvariantCulture));
            }

            sb.Append(',').Append(step.Plus.ToString("0.######", CultureInfo.InvariantCulture))
                .Append(',').Append(step.Minus.ToString("0.######", CultureInfo.InvariantCulture))
                .Append(',').Append(step.Validation?.ToString("0.######", CultureInfo.InvariantCulture) ?? "")
                .Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>
/// Simultaneous perturbation stochastic approximation (spec 12.3) with the scaled update and gain calibration of
/// decision D-16. Deterministic for a seed: iteration k draws Δ, then its batch, from Rng.ForGame(seed, k).
/// </summary>
public static class Spsa
{
    private const ulong CalibrationSalt = 0xCA11_B8A7_E5EE_D5UL;

    /// <summary>Maximises <paramref name="objective"/> from <paramref name="start"/>.</summary>
    public static SpsaResult Run(double[] start, ISpsaObjective objective, SpsaOptions options, Action<SpsaStep>? onStep = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(options);
        int d = start.Length;
        var theta = (double[])start.Clone();
        double[] s = options.Scales ?? theta.Select(t => Math.Max(1, Math.Abs(t) / 10)).ToArray();
        if (s.Length != d || s.Any(x => !(x > 0)))
        {
            throw new ArgumentException("Need one positive scale per weight.", nameof(options));
        }

        var warnings = new List<string>();
        double stability = options.Stability ?? options.Iterations / 10.0;
        double cLast = options.C / Math.Pow(options.Iterations, 0.101);
        for (int i = 0; i < d; i++)
        {
            if (cLast * s[i] < 1)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"weight {i}: c_k · s = {cLast * s[i]:0.###} < 1 by the last iteration, so rounding can erase its perturbation"));
            }
        }

        double gain = options.Gain ?? Calibrate(theta, s, objective, options, stability, warnings, ct);
        var best = Round(theta);
        double startValidation = objective.Validate(best, ct);
        double bestValidation = startValidation;
        var steps = new List<SpsaStep>(options.Iterations);
        var delta = new int[d];
        var plus = new double[d];
        var minus = new double[d];
        for (int k = 0; k < options.Iterations; k++)
        {
            ct.ThrowIfCancellationRequested();
            double ak = gain / Math.Pow(stability + k + 1, 0.602);
            double ck = options.C / Math.Pow(k + 1, 0.101);
            var rng = Rng.ForGame(options.Seed, k);
            var (jPlus, jMinus) = Perturb(theta, s, ck, rng, objective, delta, plus, minus, ct);
            for (int i = 0; i < d; i++)
            {
                // D-16: the step is taken in scaled coordinates, so θᵢ moves by about aₖ · sᵢ.
                theta[i] += ak * s[i] * (jPlus - jMinus) / (2 * ck * delta[i]);
            }

            double? validation = null;
            if ((k + 1) % options.ValidateEvery == 0 || k + 1 == options.Iterations)
            {
                var rounded = Round(theta);
                validation = objective.Validate(rounded, ct);
                if (validation > bestValidation)
                {
                    bestValidation = validation.Value;
                    best = rounded;
                }
            }

            var step = new SpsaStep(k, (double[])theta.Clone(), jPlus, jMinus, validation);
            steps.Add(step);
            onStep?.Invoke(step);
        }

        return new SpsaResult(best, bestValidation, startValidation, gain, steps, warnings);
    }

    /// <summary>Integer weights: round half away from zero.</summary>
    public static int[] Round(double[] theta)
    {
        ArgumentNullException.ThrowIfNull(theta);
        return theta.Select(t => (int)Math.Round(t, MidpointRounding.AwayFromZero)).ToArray();
    }

    private static (double Plus, double Minus) Perturb(double[] theta, double[] s, double ck, Rng rng, ISpsaObjective objective,
        int[] delta, double[] plus, double[] minus, CancellationToken ct)
    {
        for (int i = 0; i < theta.Length; i++)
        {
            delta[i] = rng.NextInt(2) == 0 ? -1 : 1; // Δ first, then the batch (D-16)
            plus[i] = theta[i] + (ck * s[i] * delta[i]);
            minus[i] = theta[i] - (ck * s[i] * delta[i]);
        }

        var batch = objective.DrawBatch(rng);
        return objective.Evaluate(Round(plus), Round(minus), batch, rng, ct);
    }

    /// <summary>Spall's gain calibration (D-16): a = TargetStep · (A + 1)^0.602 / mean |J⁺ − J⁻| / (2c₀).</summary>
    private static double Calibrate(double[] theta, double[] s, ISpsaObjective objective, SpsaOptions options, double stability,
        List<string> warnings, CancellationToken ct)
    {
        int d = theta.Length;
        var delta = new int[d];
        var plus = new double[d];
        var minus = new double[d];
        double sum = 0;
        for (int t = 0; t < options.CalibrationTrials; t++)
        {
            var (jPlus, jMinus) = Perturb(theta, s, options.C, Rng.ForGame(options.Seed ^ CalibrationSalt, t), objective, delta, plus, minus, ct);
            sum += Math.Abs(jPlus - jMinus) / (2 * options.C);
        }

        double mean = sum / Math.Max(1, options.CalibrationTrials);
        if (mean > 0)
        {
            return options.TargetStep * Math.Pow(stability + 1, 0.602) / mean;
        }

        warnings.Add("calibration: every trial gave J+ = J-, so the gain falls back to a = 1");
        return 1;
    }
}
