using System.Collections.Concurrent;

namespace FoxHounds.Engine.Evaluation;

/// <summary>
/// Evaluator types by unique name (spec 9.2). An evaluator spec names a type and, for weighted types, a weight file.
/// New evaluator classes register here; the solver-backed evaluator is registered by FoxHounds.Agents.
/// </summary>
public static class EvaluatorRegistry
{
    private static readonly ConcurrentDictionary<string, Func<WeightFile?, int, IEvaluator>> Factories = new(StringComparer.Ordinal)
    {
        ["zero"] = (_, _) => ZeroEvaluator.Instance,
        [WeightFile.Linear] = (weights, n) => Require(weights, WeightFile.Linear).CreateEvaluator(n),
        [WeightFile.Tapered] = (weights, n) => Require(weights, WeightFile.Tapered).CreateEvaluator(n),
    };

    /// <summary>Registered type names.</summary>
    public static IReadOnlyCollection<string> Names => Factories.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Registers a new evaluator type.</summary>
    /// <exception cref="ArgumentException">The name is already registered.</exception>
    public static void Register(string name, Func<WeightFile?, int, IEvaluator> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (!Factories.TryAdd(name, factory))
        {
            throw new ArgumentException($"Evaluator '{name}' is already registered.", nameof(name));
        }
    }

    /// <summary>True when <paramref name="name"/> is registered.</summary>
    public static bool IsRegistered(string name) => Factories.ContainsKey(name);

    /// <summary>Creates an evaluator of type <paramref name="type"/> for board size <paramref name="n"/>.</summary>
    /// <exception cref="ArgumentException">The type is unknown.</exception>
    /// <exception cref="InvalidDataException">The weights do not fit the type or the board size.</exception>
    public static IEvaluator Create(string type, WeightFile? weights, int n)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        if (!Factories.TryGetValue(type, out var factory))
        {
            throw new ArgumentException($"Unknown evaluator '{type}'. Registered: {string.Join(", ", Names)}.", nameof(type));
        }

        if (weights is not null && (type == WeightFile.Linear || type == WeightFile.Tapered) && weights.Evaluator != type)
        {
            throw new InvalidDataException($"Weights '{weights.Name}' are for a {weights.Evaluator} evaluator, not {type}.");
        }

        return factory(weights, n);
    }

    private static WeightFile Require(WeightFile? weights, string type) =>
        weights ?? throw new InvalidDataException($"A {type} evaluator needs a weight file.");
}
