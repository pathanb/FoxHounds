using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.App.Tests;

/// <summary>Plays the given moves in order, then the first legal move; every decision scores +7 for the fox.</summary>
internal sealed class ScriptedAgent(params string[] moves) : IAgent
{
    private readonly Queue<string> _moves = new(moves);
    private Rules? _rules;
    private int _calls;

    public byte FoxStart { get; init; } = byte.MaxValue; // MaxValue: the rules' default start

    public int Calls => Volatile.Read(ref _calls);

    public string Name => "scripted";

    public void NewGame(Rules rules, Side side, Rng rng) => _rules = rules;

    public byte ChooseFoxStart(Rules rules) => FoxStart == byte.MaxValue ? rules.DefaultFoxStart : FoxStart;

    public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var rules = _rules!;
        Move move;
        if (!_moves.TryDequeue(out string? text) || !rules.Geo.TryParseMove(text, out move))
        {
            Span<Move> legal = stackalloc Move[Rules.MaxMoves];
            rules.GenerateMoves(state, legal);
            move = legal[0];
        }

        return new AgentDecision(move, new AgentInfo(7, 1, 1, TimeSpan.Zero, [move], AgentInfo.SourceSearch));
    }
}

/// <summary>Blocks in ChooseMove until <see cref="Gate"/> is set (then plays the first legal move) or it is cancelled.</summary>
internal sealed class GatedAgent : IAgent, IDisposable
{
    private Rules? _rules;
    private int _calls;
    private int _cancelled;

    public ManualResetEventSlim Started { get; } = new();

    public ManualResetEventSlim Gate { get; } = new();

    public int Calls => Volatile.Read(ref _calls);

    public bool WasCancelled => Volatile.Read(ref _cancelled) != 0;

    public string Name => "gated";

    public void NewGame(Rules rules, Side side, Rng rng) => _rules = rules;

    public byte ChooseFoxStart(Rules rules) => rules.DefaultFoxStart;

    public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Started.Set();
        WaitHandle.WaitAny([Gate.WaitHandle, ct.WaitHandle], TimeSpan.FromSeconds(30));
        if (ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _cancelled, 1);
            ct.ThrowIfCancellationRequested();
        }

        Span<Move> legal = stackalloc Move[Rules.MaxMoves];
        _rules!.GenerateMoves(state, legal);
        return new AgentDecision(legal[0], new AgentInfo(null, 0, 0, TimeSpan.Zero, [legal[0]], AgentInfo.SourceSearch));
    }

    public void Dispose()
    {
        Started.Dispose();
        Gate.Dispose();
    }
}

/// <summary>An agent that throws, or that returns a ply the position does not allow.</summary>
internal sealed class BrokenAgent(bool illegal) : IAgent
{
    public string Name => "broken";

    public void NewGame(Rules rules, Side side, Rng rng)
    {
    }

    public byte ChooseFoxStart(Rules rules) => (byte)(rules.Geo.SquareCount / 2); // not on the back row

    public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
    {
        if (!illegal)
        {
            throw new InvalidOperationException("broken on purpose");
        }

        var move = new Move(state.Fox, state.Fox); // never legal
        return new AgentDecision(move, new AgentInfo(null, 0, 0, TimeSpan.Zero, [], AgentInfo.SourceSearch));
    }
}

/// <summary>Sessions over a temporary data folder and a manual clock.</summary>
internal static class Sessions
{
    /// <summary>One table store for all tests, so each board's solver table is built once per run.</summary>
    public static SolverTableStore Tables { get; } = new(Path.Combine(Path.GetTempPath(), "fh-app-tests-" + Guid.NewGuid().ToString("N"), "tables"));

    public static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public static string TempDir() => Path.Combine(Path.GetTempPath(), "fh-app-" + Guid.NewGuid().ToString("N"));

    public static GameSession Create(out ManualClock clock, RuleSet? ruleSet = null, string? dataDir = null)
    {
        clock = new ManualClock(Start);
        var session = new GameSession(new AgentFactory(Tables, clock), clock, Path.Combine(dataDir ?? TempDir(), "games"));
        session.NewGame(ruleSet ?? RuleSet.Classic);
        return session;
    }

    public static PlayerOption Agent(IAgent agent, string label = "test agent") => new(label, Create: () => agent);

    public static void Click(GameSession session, string move)
    {
        Assert.True(session.Rules.Geo.TryParseMove(move, out var m), $"not a move: {move}");
        session.ClickSquare(m.From);
        session.ClickSquare(m.To);
    }

    public static int Square(GameSession session, string name)
    {
        Assert.True(session.Rules.Geo.TryParseSquare(name, out int square), $"not a square: {name}");
        return square;
    }

    /// <summary>Calls Update, as the frame loop does, until <paramref name="done"/> holds (30 s real-time limit).</summary>
    public static void RunUntil(GameSession session, Func<bool> done, string what)
    {
        var limit = SystemClock.Instance.Elapsed + TimeSpan.FromSeconds(30);
        while (true)
        {
            session.Update();
            if (done())
            {
                return;
            }

            if (SystemClock.Instance.Elapsed > limit)
            {
                Assert.Fail($"timed out waiting for {what}; status: {session.Status()}");
            }

            Thread.Sleep(1);
        }
    }
}
