namespace FoxHounds.Core;

/// <summary>Who played a side: a human, or an agent with its name and (optionally) its full spec as JSON.</summary>
/// <param name="Kind">"human" or "agent".</param>
/// <param name="Name">Agent name, or null for a human.</param>
/// <param name="SpecJson">The AgentSpec the agent was built from, as JSON text, when known.</param>
public sealed record PlayerInfo(string Kind, string? Name, string? SpecJson = null)
{
    /// <summary>Kind of a human player.</summary>
    public const string HumanKind = "human";

    /// <summary>Kind of an automated player.</summary>
    public const string AgentKind = "agent";

    /// <summary>A human player.</summary>
    public static PlayerInfo Human { get; } = new(HumanKind, null);

    /// <summary>An agent known by name only.</summary>
    public static PlayerInfo ForAgent(IAgent agent, string? specJson = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return new PlayerInfo(AgentKind, agent.Name, specJson);
    }
}

/// <summary>One ply of a game record, with the deciding agent's statistics when an agent played it.</summary>
/// <param name="Move">The ply.</param>
/// <param name="Info">Agent statistics, or null for a human ply.</param>
public sealed record PlyRecord(Move Move, AgentInfo? Info);

/// <summary>Why the game controller ended a game outside the rules (spec 3.4, 7.3).</summary>
public enum ForfeitCause : byte
{
    /// <summary>The agent threw an exception.</summary>
    AgentThrew,

    /// <summary>The agent returned an illegal ply.</summary>
    IllegalMove,

    /// <summary>The agent overran its hard time limit by more than 20%.</summary>
    TimeLimit,

    /// <summary>Under ChooseOnBackRow the fox's agent chose a square outside <see cref="Rules.FoxStartSquares"/>.</summary>
    InvalidFoxStart,
}

/// <summary>Details of a forfeit.</summary>
/// <param name="Side">The side that forfeited (and lost).</param>
/// <param name="Cause">What happened.</param>
/// <param name="Message">The exception message or a description of the offending ply.</param>
public sealed record ForfeitInfo(Side Side, ForfeitCause Cause, string Message);

/// <summary>
/// A complete game (spec 7.3, 14.2): ruleset, start position, players, every ply with its agent statistics, and
/// the result. Enough to replay and analyse the game without re-running the agents.
/// </summary>
public sealed record GameRecord
{
    /// <summary>The ruleset the game was played under.</summary>
    public required RuleSet RuleSet { get; init; }

    /// <summary>The position before the first ply (after the fox's start square was chosen).</summary>
    public required GameState Start { get; init; }

    /// <summary>Who played the fox.</summary>
    public required PlayerInfo Fox { get; init; }

    /// <summary>Who played the hounds.</summary>
    public required PlayerInfo Hounds { get; init; }

    /// <summary>Seed of the game's <see cref="Rng"/>, when the game was played by <see cref="GameRunner"/>.</summary>
    public ulong? Seed { get; init; }

    /// <summary>When the game started; excluded from determinism comparisons (D-20).</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Every ply in order.</summary>
    public required IReadOnlyList<PlyRecord> Plies { get; init; }

    /// <summary>The final result and reason.</summary>
    public required Outcome Outcome { get; init; }

    /// <summary>Forfeit details when <see cref="Outcome"/> has reason <see cref="EndReason.Forfeit"/>.</summary>
    public ForfeitInfo? Forfeit { get; init; }

    /// <summary>Replays the plies from <see cref="Start"/> with full legality checks and returns every position,
    /// starting with <see cref="Start"/>.</summary>
    /// <exception cref="IllegalMoveException">A stored ply is not legal.</exception>
    public IReadOnlyList<GameState> Replay(Rules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate(Start);
        var positions = new List<GameState>(Plies.Count + 1) { Start };
        GameState s = Start;
        foreach (var ply in Plies)
        {
            s = rules.ApplyChecked(s, ply.Move);
            positions.Add(s);
        }

        return positions;
    }
}
