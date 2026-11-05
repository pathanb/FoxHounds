namespace FoxHounds.Core;

/// <summary>
/// Anything that picks plies for one side: random, minimax, solver (spec 7.1). Humans are not agents (D-12).
/// An instance plays one side of one game at a time.
/// </summary>
public interface IAgent
{
    /// <summary>Stable name for logs and records, for example "minimax[d=12,eval=lin-v1]".</summary>
    string Name { get; }

    /// <summary>Resets all per-game state for a new game of <paramref name="side"/>. Randomness comes only from
    /// <paramref name="rng"/>, which belongs to this agent for the game.</summary>
    void NewGame(Rules rules, Side side, Rng rng);

    /// <summary>Picks the fox's start square under <see cref="FoxStart.ChooseOnBackRow"/>; called after
    /// <see cref="NewGame"/>, only for the fox. Must return one of <see cref="Rules.FoxStartSquares"/>.</summary>
    byte ChooseFoxStart(Rules rules);

    /// <summary>
    /// Picks a legal ply for a non-terminal <paramref name="state"/>. Must honour <paramref name="ct"/> and must
    /// not mutate shared state.
    /// </summary>
    AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct);
}

/// <summary>The ply an agent chose and how it got there.</summary>
/// <param name="Move">The chosen ply.</param>
/// <param name="Info">Search statistics for logs, records and the analysis viewer.</param>
public sealed record AgentDecision(Move Move, AgentInfo Info);

/// <summary>Statistics about one decision (spec 7.1).</summary>
/// <param name="ScoreFox">Root score from the fox's perspective, or null when the agent has none.</param>
/// <param name="DepthReached">Last completed search depth in plies.</param>
/// <param name="Nodes">Nodes searched.</param>
/// <param name="Elapsed">Wall time spent; never used in determinism comparisons (D-20).</param>
/// <param name="PrincipalVariation">Expected line starting with the chosen ply; may be empty.</param>
/// <param name="Source">"search", "solver" or "random".</param>
public sealed record AgentInfo(
    int? ScoreFox,
    int DepthReached,
    long Nodes,
    TimeSpan Elapsed,
    IReadOnlyList<Move> PrincipalVariation,
    string Source)
{
    /// <summary>Source of a searched decision.</summary>
    public const string SourceSearch = "search";

    /// <summary>Source of a decision read from a solver table.</summary>
    public const string SourceSolver = "solver";

    /// <summary>Source of a random decision.</summary>
    public const string SourceRandom = "random";

    /// <summary>Info for a random ply: no score, no search.</summary>
    public static AgentInfo Random { get; } = new(null, 0, 0, TimeSpan.Zero, [], SourceRandom);
}

/// <summary>Limits for one decision (spec 7.1). Experiments use depth or node limits only (D-09).</summary>
/// <param name="MaxDepth">Deepest iteration in plies.</param>
/// <param name="MaxNodes">Node limit.</param>
/// <param name="SoftTime">Do not start a new iteration after this.</param>
/// <param name="HardTime">Abort the search at this point; the game runner forfeits an agent that overruns it by more
/// than 20%.</param>
public sealed record SearchBudget(int? MaxDepth, long? MaxNodes, TimeSpan? SoftTime, TimeSpan? HardTime)
{
    /// <summary>No limits at all: the agent decides. Searching agents treat it as exhaustive.</summary>
    public static SearchBudget Unlimited { get; } = new(null, null, null, null);

    /// <summary>True when the budget contains no time limit, so results do not depend on machine load.</summary>
    public bool IsDeterministic => SoftTime is null && HardTime is null;

    /// <summary>A depth-only budget.</summary>
    public static SearchBudget Depth(int maxDepth) => new(maxDepth, null, null, null);

    /// <summary>A node-only budget.</summary>
    public static SearchBudget Nodes(long maxNodes) => new(null, maxNodes, null, null);
}
