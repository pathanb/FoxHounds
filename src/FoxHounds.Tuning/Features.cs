using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Sim;

namespace FoxHounds.Tuning;

/// <summary>A labelled feature matrix for the regression tuner (spec 12.2).</summary>
public sealed class FeatureSet
{
    private FeatureSet(double[] x, double[] y, int columns)
    {
        X = x;
        Y = y;
        Columns = columns;
    }

    /// <summary>Row-major features, <see cref="Rows"/> × <see cref="Columns"/>.</summary>
    public double[] X { get; }

    /// <summary>Targets: 1 when the fox wins with perfect play, else 0 (or a probability, for synthetic data).</summary>
    public double[] Y { get; }

    /// <summary>Features per row: 11, or 22 in tapered mode (early block, then late block).</summary>
    public int Columns { get; }

    /// <summary>Number of rows.</summary>
    public int Rows => Y.Length;

    /// <summary>The features of solver-labelled positions; tapered mode splits each feature by phase (12.2).</summary>
    /// <exception cref="ArgumentException">A position has no label.</exception>
    public static FeatureSet FromPositions(IReadOnlyList<LabelledPosition> positions, bool tapered)
    {
        ArgumentNullException.ThrowIfNull(positions);
        int columns = tapered ? 2 * FeatureExtractor.Count : FeatureExtractor.Count;
        var x = new double[positions.Count * columns];
        var y = new double[positions.Count];
        Span<int> f = stackalloc int[FeatureExtractor.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var winner = p.Winner ?? throw new ArgumentException($"Position {i} has no solver label.", nameof(positions));
            y[i] = winner == Side.Fox ? 1 : 0;
            FeatureExtractor.Extract(p.Position, f);
            Fill(x.AsSpan(i * columns, columns), f, tapered ? Phase(p.Position, f) : -1);
        }

        return new FeatureSet(x, y, columns);
    }

    /// <summary>A feature set from given rows (synthetic data in tests).</summary>
    public static FeatureSet FromRows(double[] x, double[] y, int columns)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (columns < 1 || x.Length != y.Length * columns)
        {
            throw new ArgumentException("x must hold y.Length rows of the given width.", nameof(x));
        }

        return new FeatureSet(x, y, columns);
    }

    /// <summary>The tapered evaluator's phase (spec 9.2): 256 · HoundMovesLeft / (H · (n − 1)), integer.</summary>
    public static int Phase(in GameState s, ReadOnlySpan<int> features)
    {
        var geo = Geometry.For(s.N);
        return (int)(256L * features[(int)FeatureId.HoundMovesLeft] / (geo.HoundCount * (geo.N - 1)));
    }

    private static void Fill(Span<double> row, ReadOnlySpan<int> f, int phase)
    {
        if (phase < 0)
        {
            for (int j = 0; j < f.Length; j++)
            {
                row[j] = f[j];
            }

            return;
        }

        // (phase · f / 256, (256 − phase) · f / 256): early weights, then late weights.
        for (int j = 0; j < f.Length; j++)
        {
            row[j] = phase * f[j] / 256.0;
            row[f.Length + j] = (256 - phase) * f[j] / 256.0;
        }
    }
}
