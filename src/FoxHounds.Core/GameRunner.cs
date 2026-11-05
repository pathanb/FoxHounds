using System.Diagnostics.CodeAnalysis;

namespace FoxHounds.Core;

/// <summary>Options for <see cref="GameRunner.Play"/>.</summary>
public sealed record GameOptions
{
    /// <summary>Budget passed to the fox's agent on every turn.</summary>
    public required SearchBudget FoxBudget { get; init; }

    /// <summary>Budget passed to the hounds' agent on every turn.</summary>
    public required SearchBudget HoundsBudget { get; init; }

    /// <summary>Start from this position instead of the initial one (openings, "play from here").</summary>
    public GameState? Start { get; init; }

    /// <summary>Clock for timestamps and time-limit checks.</summary>
    public IClock Clock { get; init; } = SystemClock.Instance;

    /// <summary>Recorded as the fox player; defaults to the agent's name.</summary>
    public PlayerInfo? FoxPlayer { get; init; }

    /// <summary>Recorded as the hounds player; defaults to the agent's name.</summary>
    public PlayerInfo? HoundsPlayer { get; init; }
}

/// <summary>Drives one game between two agents (spec 7.3). Used by the CLI and the simulator.</summary>
public static class GameRunner
{
    /// <summary>
    /// Plays a game. Each agent gets its own random stream split from <paramref name="rng"/>, fox first (D-23).
    /// An agent that throws, returns an illegal ply or overruns its hard time limit by more than 20% forfeits; an
    /// <see cref="InvariantViolationException"/> is a rules bug and propagates instead. Cancelling
    /// <paramref name="ct"/> stops the game with <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="rules">The rules.</param>
    /// <param name="fox">Agent for the fox.</param>
    /// <param name="hounds">Agent for the hounds; must not be the same instance as <paramref name="fox"/>.</param>
    /// <param name="options">Budgets, start position and clock.</param>
    /// <param name="rng">A fresh generator for this game; its seed is recorded.</param>
    /// <param name="ct">Cancels the game.</param>
    /// <exception cref="InvariantViolationException">The game exceeded <see cref="Rules.MaxPliesFrom"/>: a rules bug.</exception>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Spec 7.3: any exception thrown by an agent is a forfeit, never a crash of the runner.")]
    public static GameRecord Play(Rules rules, IAgent fox, IAgent hounds, GameOptions options, Rng rng, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(fox);
        ArgumentNullException.ThrowIfNull(hounds);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rng);
        if (ReferenceEquals(fox, hounds))
        {
            throw new ArgumentException("An agent instance plays one side of one game at a time.", nameof(hounds));
        }

        var clock = options.Clock;
        var game = new GameInProgress(rules, options, rng.Seed, clock.UtcNow, fox, hounds);
        Rng foxRng = rng.Split();
        Rng houndsRng = rng.Split();
        if (options.Start is { } given)
        {
            rules.Validate(given);
            game.Start = given;
        }

        if (!TryCall(() => fox.NewGame(rules, Side.Fox, foxRng), ct, out string? error))
        {
            return game.Forfeit(Side.Fox, ForfeitCause.AgentThrew, error);
        }

        if (!TryCall(() => hounds.NewGame(rules, Side.Hounds, houndsRng), ct, out error))
        {
            return game.Forfeit(Side.Hounds, ForfeitCause.AgentThrew, error);
        }

        if (options.Start is null && rules.Config.FoxStart == FoxStart.ChooseOnBackRow)
        {
            byte chosen = 0;
            if (!TryCall(() => chosen = fox.ChooseFoxStart(rules), ct, out error))
            {
                return game.Forfeit(Side.Fox, ForfeitCause.AgentThrew, error);
            }

            if (!rules.FoxStartSquares.Contains(chosen))
            {
                return game.Forfeit(Side.Fox, ForfeitCause.InvalidFoxStart, $"square {chosen} is not on the fox's back row");
            }

            game.Start = rules.Initial(chosen);
        }

        GameState s = game.Start;
        int maxPlies = rules.MaxPliesFrom(s);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = rules.Evaluate(s);
            if (outcome.IsOver)
            {
                return game.Finish(outcome, null);
            }

            Side side = s.SideToMove;
            IAgent agent = side == Side.Fox ? fox : hounds;
            SearchBudget budget = side == Side.Fox ? options.FoxBudget : options.HoundsBudget;
            TimeSpan before = clock.Elapsed;
            AgentDecision? decision;
            try
            {
                decision = agent.ChooseMove(s, game.HistoryView, budget, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not InvariantViolationException)
            {
                return game.Forfeit(side, ForfeitCause.AgentThrew, $"{ex.GetType().Name}: {ex.Message}");
            }

            TimeSpan spent = clock.Elapsed - before;
            if (budget.HardTime is { } hard && spent - hard > hard / 5) // more than 20% over, without overflow
            {
                return game.Forfeit(side, ForfeitCause.TimeLimit, $"took {spent.TotalMilliseconds:F0} ms with a hard limit of {hard.TotalMilliseconds:F0} ms");
            }

            if (decision is null || !rules.IsLegal(s, decision.Move))
            {
                string what = decision is null ? "no decision" : DescribeMove(rules, decision.Move);
                return game.Forfeit(side, ForfeitCause.IllegalMove, $"returned {what} in {s}");
            }

            s = rules.Apply(s, decision.Move);
            game.Add(decision.Move, decision.Info);
            if (game.PlyCount > maxPlies)
            {
                throw new InvariantViolationException(
                    $"Game from {game.Start} exceeded MaxPliesFrom = {maxPlies} plies; this can only be a rules bug.");
            }
        }
    }

    private static bool TryCall(Action call, CancellationToken ct, [NotNullWhen(false)] out string? error)
    {
        try
        {
            call();
            error = null;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Spec 7.3: an agent exception is a forfeit, except a detected rules bug.
        catch (Exception ex) when (ex is not InvariantViolationException)
#pragma warning restore CA1031
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string DescribeMove(Rules rules, Move m) =>
        m.From < rules.Geo.SquareCount && m.To < rules.Geo.SquareCount ? rules.Geo.FormatMove(m) : $"({m.From}, {m.To})";

    private sealed class GameInProgress
    {
        private readonly Rules _rules;
        private readonly ulong _seed;
        private readonly DateTimeOffset _started;
        private readonly PlayerInfo _foxPlayer;
        private readonly PlayerInfo _houndsPlayer;
        private readonly List<PlyRecord> _plies = [];
        private readonly List<Move> _history = [];

        public GameInProgress(Rules rules, GameOptions options, ulong seed, DateTimeOffset started, IAgent fox, IAgent hounds)
        {
            _rules = rules;
            _seed = seed;
            _started = started;
            _foxPlayer = options.FoxPlayer ?? PlayerInfo.ForAgent(fox);
            _houndsPlayer = options.HoundsPlayer ?? PlayerInfo.ForAgent(hounds);
            Start = rules.Initial();
            HistoryView = _history.AsReadOnly();
        }

        public GameState Start { get; set; }

        public IReadOnlyList<Move> HistoryView { get; }

        public int PlyCount => _plies.Count;

        public void Add(Move move, AgentInfo info)
        {
            _plies.Add(new PlyRecord(move, info));
            _history.Add(move);
        }

        public GameRecord Forfeit(Side side, ForfeitCause cause, string message) =>
            Finish(Outcome.Win(side.Opponent(), EndReason.Forfeit), new ForfeitInfo(side, cause, message));

        public GameRecord Finish(Outcome outcome, ForfeitInfo? forfeit) => new()
        {
            RuleSet = _rules.Config,
            Start = Start,
            Fox = _foxPlayer,
            Hounds = _houndsPlayer,
            Seed = _seed,
            StartedUtc = _started,
            Plies = _plies.ToArray(),
            Outcome = outcome,
            Forfeit = forfeit,
        };
    }
}
