using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Solver;

namespace FoxHounds.Agents;

/// <summary>
/// The true value of a position from a solver table, as an evaluator (spec 9.2, tests only): it isolates search bugs
/// from evaluation bugs. A win in d plies for the fox scores EVAL_MAX − d (a loss, the negation), so it stays in the
/// heuristic range: the search must never mistake a leaf evaluation for a proven result whose distance is counted
/// from the root (spec 8.3). Shorter wins and longer losses still score better, so a depth-1 search plays perfectly.
/// </summary>
public sealed class SolverEvaluator : IEvaluator
{
    /// <summary>Registered evaluator type name.</summary>
    public const string TypeName = "solver";

    private readonly SolverTable _table;

    /// <summary>Creates the evaluator over <paramref name="table"/>.</summary>
    public SolverEvaluator(SolverTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <inheritdoc />
    public string Name => TypeName;

    /// <summary>The table the values come from.</summary>
    public SolverTable Table => _table;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The table holds no value for the position (spec 10.3).</exception>
    public int EvaluateFox(in GameState s)
    {
        var value = _table.Get(s);
        int magnitude = Scores.EvalMax - value.Plies;
        return value.Winner(s.SideToMove) == Side.Fox ? magnitude : -magnitude;
    }
}
