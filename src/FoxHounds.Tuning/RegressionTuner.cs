namespace FoxHounds.Tuning;

/// <summary>Settings of the regression tuner (spec 12.2, decision D-17).</summary>
public sealed record RegressionOptions
{
    /// <summary>Evaluation points per logit: P(fox wins) = σ(w · f / K).</summary>
    public double K { get; init; } = 400;

    /// <summary>L2 penalty on β = w / K; the intercept is not penalised.</summary>
    public double Lambda { get; init; } = 1e-4;

    /// <summary>Adam learning rate.</summary>
    public double LearningRate { get; init; } = 0.01;

    /// <summary>Adam β₁.</summary>
    public double Beta1 { get; init; } = 0.9;

    /// <summary>Adam β₂.</summary>
    public double Beta2 { get; init; } = 0.999;

    /// <summary>Most full-batch steps.</summary>
    public int MaxSteps { get; init; } = 20_000;

    /// <summary>Stop when the validation loss has not improved for this many steps.</summary>
    public int Patience { get; init; } = 500;
}

/// <summary>The fitted model and how well it fits.</summary>
/// <param name="Weights">w = round(K · β), in feature order (and early, then late, in tapered mode).</param>
/// <param name="Beta">The fitted β at the best validation loss.</param>
/// <param name="Intercept">The fitted intercept, dropped from the output (FoxToMove carries side bias).</param>
/// <param name="Steps">Steps taken.</param>
/// <param name="BestStep">Step with the lowest validation loss.</param>
/// <param name="TrainLoss">Cross-entropy on train of the rounded weights with the fitted intercept.</param>
/// <param name="ValidationLoss">The same on validation.</param>
/// <param name="TrainAccuracy">Share of train positions whose winner the rounded weights and intercept predict.</param>
/// <param name="ValidationAccuracy">The same on validation.</param>
/// <param name="ValidationLossWithoutIntercept">Validation loss once the intercept is dropped, as in the weight
/// file. Dropping it shifts every evaluation by the same amount, which changes no search decision, but it does raise
/// the static loss.</param>
public sealed record RegressionResult(int[] Weights, double[] Beta, double Intercept, int Steps, int BestStep,
    double TrainLoss, double ValidationLoss, double TrainAccuracy, double ValidationAccuracy, double ValidationLossWithoutIntercept);

/// <summary>
/// Logistic regression of the solver's winner on the features (spec 12.2), optimised on β = w / K with full-batch
/// Adam (decision D-17). Deterministic: sums run in row order on one thread.
/// </summary>
public static class RegressionTuner
{
    /// <summary>Fits weights on <paramref name="train"/>, stopping early on <paramref name="validation"/>.</summary>
    public static RegressionResult Fit(FeatureSet train, FeatureSet validation, RegressionOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(validation);
        options ??= new RegressionOptions();
        if (train.Columns != validation.Columns || train.Rows == 0 || validation.Rows == 0)
        {
            throw new ArgumentException("Train and validation need rows of the same width.", nameof(validation));
        }

        int d = train.Columns;
        var theta = new double[d + 1]; // β, then the intercept
        var m = new double[d + 1];
        var v = new double[d + 1];
        var gradient = new double[d + 1];
        var best = (double[])theta.Clone();
        double bestLoss = Loss(validation, theta, withIntercept: true);
        int bestStep = 0;
        int step = 0;
        while (step < options.MaxSteps && step - bestStep < options.Patience)
        {
            ct.ThrowIfCancellationRequested();
            step++;
            Gradient(train, theta, options.Lambda, gradient);
            double correction1 = 1 - Math.Pow(options.Beta1, step);
            double correction2 = 1 - Math.Pow(options.Beta2, step);
            for (int j = 0; j <= d; j++)
            {
                m[j] = (options.Beta1 * m[j]) + ((1 - options.Beta1) * gradient[j]);
                v[j] = (options.Beta2 * v[j]) + ((1 - options.Beta2) * gradient[j] * gradient[j]);
                theta[j] -= options.LearningRate * (m[j] / correction1) / (Math.Sqrt(v[j] / correction2) + 1e-8);
            }

            double loss = Loss(validation, theta, withIntercept: true);
            if (loss < bestLoss)
            {
                bestLoss = loss;
                bestStep = step;
                Array.Copy(theta, best, theta.Length);
            }
        }

        var weights = new int[d];
        var rounded = new double[d + 1];
        for (int j = 0; j < d; j++)
        {
            weights[j] = (int)Math.Round(options.K * best[j], MidpointRounding.AwayFromZero);
            rounded[j] = weights[j] / options.K;
        }

        rounded[d] = best[d];
        return new RegressionResult(weights, best[..d], best[d], step, bestStep,
            Loss(train, rounded, withIntercept: true), Loss(validation, rounded, withIntercept: true),
            Accuracy(train, rounded), Accuracy(validation, rounded), Loss(validation, rounded, withIntercept: false));
    }

    /// <summary>Mean cross-entropy of the model (β, intercept) on a set.</summary>
    public static double Loss(FeatureSet set, double[] theta, bool withIntercept)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(theta);
        int d = set.Columns;
        double total = 0;
        for (int i = 0; i < set.Rows; i++)
        {
            double z = Logit(set, i, theta, withIntercept);
            double y = set.Y[i];

            // log(1 + e^z) − y·z, computed without overflow.
            total += (z > 0 ? z + Math.Log(1 + Math.Exp(-z)) : Math.Log(1 + Math.Exp(z))) - (y * z);
        }

        return total / set.Rows;
    }

    private static double Accuracy(FeatureSet set, double[] theta)
    {
        int correct = 0;
        for (int i = 0; i < set.Rows; i++)
        {
            correct += (Logit(set, i, theta, withIntercept: true) > 0) == (set.Y[i] > 0.5) ? 1 : 0;
        }

        return (double)correct / set.Rows;
    }

    private static double Logit(FeatureSet set, int row, double[] theta, bool withIntercept)
    {
        int d = set.Columns;
        var x = set.X.AsSpan(row * d, d);
        double z = withIntercept ? theta[d] : 0;
        for (int j = 0; j < d; j++)
        {
            z += theta[j] * x[j];
        }

        return z;
    }

    private static void Gradient(FeatureSet set, double[] theta, double lambda, double[] gradient)
    {
        int d = set.Columns;
        Array.Clear(gradient);
        for (int i = 0; i < set.Rows; i++)
        {
            double z = Logit(set, i, theta, withIntercept: true);
            double error = (1 / (1 + Math.Exp(-z))) - set.Y[i];
            var x = set.X.AsSpan(i * d, d);
            for (int j = 0; j < d; j++)
            {
                gradient[j] += error * x[j];
            }

            gradient[d] += error;
        }

        for (int j = 0; j <= d; j++)
        {
            gradient[j] /= set.Rows;
        }

        for (int j = 0; j < d; j++)
        {
            gradient[j] += 2 * lambda * theta[j];
        }
    }
}
