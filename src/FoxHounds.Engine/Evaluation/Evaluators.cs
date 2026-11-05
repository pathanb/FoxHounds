using FoxHounds.Core;

namespace FoxHounds.Engine.Evaluation;

/// <summary>
/// A static evaluation (spec 8.1), from the fox's perspective and clamped to ±<see cref="Scores.EvalMax"/>.
/// Implementations hold no mutable state, never allocate, and return the same value for the same position.
/// </summary>
public interface IEvaluator
{
    /// <summary>Stable name, logged and recorded; for weighted evaluators the weight file's name.</summary>
    string Name { get; }

    /// <summary>Score for the fox: positive is good for the fox. Never called on a finished position by the search.</summary>
    int EvaluateFox(in GameState s);
}

/// <summary>Always 0: measures what search alone achieves.</summary>
public sealed class ZeroEvaluator : IEvaluator
{
    /// <summary>The shared instance.</summary>
    public static ZeroEvaluator Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "zero";

    /// <inheritdoc />
    public int EvaluateFox(in GameState s) => 0;
}

/// <summary>clamp(Σ wᵢ·fᵢ, ±EVAL_MAX): the default product evaluator (spec 9.2).</summary>
public sealed class LinearEvaluator : IEvaluator
{
    private readonly int[] _weights;

    /// <summary>Creates the evaluator from weights in <see cref="FeatureId"/> order.</summary>
    public LinearEvaluator(string name, ReadOnlySpan<int> weights)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (weights.Length != FeatureExtractor.Count)
        {
            throw new ArgumentException($"Expected {FeatureExtractor.Count} weights, got {weights.Length}.", nameof(weights));
        }

        Name = name;
        _weights = weights.ToArray();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The weights in <see cref="FeatureId"/> order.</summary>
    public ReadOnlySpan<int> Weights => _weights;

    /// <inheritdoc />
    public int EvaluateFox(in GameState s)
    {
        Span<int> f = stackalloc int[FeatureExtractor.Count];
        FeatureExtractor.Extract(s, f);
        long sum = 0;
        for (int i = 0; i < FeatureExtractor.Count; i++)
        {
            sum += (long)_weights[i] * f[i];
        }

        return Scores.ClampEval(sum);
    }
}

/// <summary>
/// A phase-weighted mix of early and late weights (spec 9.2): phase = 256 · HoundMovesLeft / (H·(n−1)), integer;
/// score = (phase · Σ wᵉᵃʳˡʸ·f + (256 − phase) · Σ wˡᵃᵗᵉ·f) / 256 with long intermediates (C# division truncates
/// toward zero), then clamped.
/// </summary>
public sealed class TaperedEvaluator : IEvaluator
{
    private readonly int[] _early;
    private readonly int[] _late;

    /// <summary>Creates the evaluator from two weight vectors in <see cref="FeatureId"/> order.</summary>
    public TaperedEvaluator(string name, ReadOnlySpan<int> early, ReadOnlySpan<int> late)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (early.Length != FeatureExtractor.Count || late.Length != FeatureExtractor.Count)
        {
            throw new ArgumentException($"Expected {FeatureExtractor.Count} early and {FeatureExtractor.Count} late weights.");
        }

        Name = name;
        _early = early.ToArray();
        _late = late.ToArray();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int EvaluateFox(in GameState s)
    {
        Span<int> f = stackalloc int[FeatureExtractor.Count];
        FeatureExtractor.Extract(s, f);
        var geo = Geometry.For(s.N);
        long phase = 256L * f[(int)FeatureId.HoundMovesLeft] / (geo.HoundCount * (geo.N - 1));
        long early = 0;
        long late = 0;
        for (int i = 0; i < FeatureExtractor.Count; i++)
        {
            early += (long)_early[i] * f[i];
            late += (long)_late[i] * f[i];
        }

        return Scores.ClampEval(((phase * early) + ((256 - phase) * late)) / 256);
    }
}
