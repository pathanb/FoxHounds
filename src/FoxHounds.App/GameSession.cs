using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Solver;

namespace FoxHounds.App;

/// <summary>A choice in a player cycler: a human, or an agent from a spec (or, in tests, from a factory method).</summary>
/// <param name="Label">Text shown in the cycler.</param>
/// <param name="Spec">The agent spec; null for a human (unless <paramref name="Create"/> is set).</param>
/// <param name="Create">Builds the agent directly (tests); takes precedence over <paramref name="Spec"/>.</param>
/// <param name="Budget">Budget for an agent built by <paramref name="Create"/>.</param>
internal sealed record PlayerOption(string Label, AgentSpec? Spec = null, Func<IAgent>? Create = null, SearchBudget? Budget = null)
{
    public static PlayerOption Human { get; } = new("Human");

    public bool IsHuman => Spec is null && Create is null;

    public bool NeedsSolver => Spec?.Type == AgentFactory.Solver || Spec?.Evaluator?.Type == SolverEvaluator.TypeName;
}

/// <summary>One line of the move list: the move number, the fox's ply and the hounds' ply (either may be empty), with
/// the index of each ply in the game (−1 for an empty cell).</summary>
internal readonly record struct MoveRow(int Number, string Fox, string Hounds, int FoxPly = -1, int HoundsPly = -1);

/// <summary>
/// The app's game logic without any Raylib (spec 13.1): rules, history, player slots, undo/redo, the running AI
/// task, save and load. Called only from the UI thread; AI turns run on the thread pool and are collected in
/// <see cref="Update"/>, which never blocks (spec 7.4).
/// </summary>
internal sealed partial class GameSession : IDisposable
{
    /// <summary>Delays between AI plies offered by the delay cycler (spec 13.4).</summary>
    public static readonly int[] DelayChoicesMs = [0, 250, 500, 1000, 2000];

    /// <summary>Largest board whose solver table the app builds by itself (8 x 8 takes about 0.15 s, spec 17.3).</summary>
    public const int AutoSolveMaxN = 8;

    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);

    private readonly AgentFactory _factory;
    private readonly IClock _clock;
    private readonly List<PlyRecord> _plies = [];
    private readonly Stack<PlyRecord> _redo = new();
    private readonly PlayerOption[] _players = [PlayerOption.Human, PlayerOption.Human];
    private readonly IAgent?[] _agents = new IAgent?[2];
    private readonly TimeSpan[] _settleUntil = [TimeSpan.MinValue, TimeSpan.MinValue];
    private Rng _rng = new(1);
    private GameState? _start;
    private GameState _position;
    private ForfeitInfo? _forfeit;
    private Task<AiResult>? _aiTask;
    private AgentHolder? _running;
    private CancellationTokenSource? _aiCts;
    private int _aiGeneration;
    private TimeSpan _lastPlyAt;
    private Task<SolverTable?>? _tableTask;
    private RuleSet? _tableConfig;
    private CancellationTokenSource? _tableCts;
    private double _buildProgress = -1;
    private double _tableProgress;
    private Side? _failedSide; // an agent that threw is not asked again until the user acts
    private string? _message;
    private TimeSpan? _messageExpires;

    public GameSession(AgentFactory factory, IClock clock, string gamesDirectory)
    {
        _factory = factory;
        _clock = clock;
        GamesDirectory = gamesDirectory;
        NewGame(RuleSet.Classic);
    }

    public Rules Rules { get; private set; } = new(RuleSet.Classic);

    /// <summary>The position on the board; before the fox is placed under ChooseOnBackRow, the hounds alone.</summary>
    public GameState Position => _position;

    /// <summary>True under ChooseOnBackRow until the fox's start square is chosen.</summary>
    public bool AwaitingFoxPlacement => _start is null;

    public IReadOnlyList<PlyRecord> Plies => _plies;

    public Outcome Outcome { get; private set; } = Outcome.Ongoing;

    public ForfeitInfo? Forfeit => _forfeit;

    public bool IsOver => Outcome.IsOver;

    public bool CanRedo => !InReplay && _redo.Count > 0;

    /// <summary>Square of the selected piece, if any.</summary>
    public byte? Selected { get; private set; }

    /// <summary>AI against AI: no new AI ply starts while paused (the one being computed still completes).</summary>
    public bool Paused { get; private set; }

    public TimeSpan Delay { get; set; }

    public string GamesDirectory { get; }

    public ulong Seed { get; private set; }

    public DateTimeOffset StartedUtc { get; private set; }

    /// <summary>
    /// What the status line should say instead of the turn: a problem (an agent error, a failed load), kept until the
    /// next action, or a notice (saved, loaded), shown for a few seconds.
    /// </summary>
    public string? Message => _messageExpires is { } until && _clock.Elapsed >= until ? null : _message;

    /// <summary>Changes whenever the game changes (a ply, an undo, a new game), so views can tell a new state.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// True when the Perfect player can be offered (spec 13.3): the solver supports this board size. Tables up to
    /// <see cref="AutoSolveMaxN"/> load with every game; larger ones are built on first use, with progress shown
    /// (spec 17.2, Q-07).
    /// </summary>
    public bool SolverOffered => SolverTable.IsSupported(Rules.Geo.N);

    /// <summary>Progress (0 to 1) of a solver table being loaded or built in the background; null when none is.</summary>
    public double? TableProgress => _tableTask is { IsCompleted: false } ? Volatile.Read(ref _tableProgress) : null;

    /// <summary>The position the game started from; null while the fox's start square is being chosen.</summary>
    public GameState? StartPosition => _start;

    /// <summary>Maps an agent spec from a loaded record to one of the app's player choices (null: none matches).</summary>
    public Func<AgentSpec, PlayerOption?>? ResolvePlayer { get; set; }

    /// <summary>True when <see cref="Undo"/> would change the game.</summary>
    public bool CanUndo => !InReplay && UndoTarget() >= 0;

    public bool AiThinking => _aiTask is { IsCompleted: false };

    /// <summary>
    /// How long a newly chosen computer player waits before it may move. Cycling to the wanted player passes others on
    /// the way, and without the pause the first computer player passed would move at once. The app sets half a second;
    /// tests leave it at zero.
    /// </summary>
    public TimeSpan PlayerSettle { get; set; } = TimeSpan.Zero;

    /// <summary>The solver's value of the current position, when a table is loaded for this board.</summary>
    public SolvedValue? SolverValue
    {
        get
        {
            if (_tableTask is not { IsCompletedSuccessfully: true, Result: { } table } || AwaitingFoxPlacement)
            {
                return null;
            }

            return table.TryGet(_position, out var value) ? value : null;
        }
    }

    public SolverTable? Table => _tableTask is { IsCompletedSuccessfully: true } t ? t.Result : null;

    public PlayerOption Player(Side side) => _players[(int)side];

    public bool IsHuman(Side side) => _players[(int)side].IsHuman;

    public bool HasHuman => IsHuman(Side.Fox) || IsHuman(Side.Hounds);

    /// <summary>Starts a new game; cancels any AI search first.</summary>
    public void NewGame(RuleSet ruleSet)
    {
        ExitReplay();
        CancelAi();
        Rules = new Rules(ruleSet);
        Seed = unchecked((ulong)_clock.UtcNow.Ticks);
        StartedUtc = _clock.UtcNow;
        ResetGame(ruleSet.FoxStart == FoxStart.ChooseOnBackRow ? null : Rules.Initial());
        LoadTableInBackground();
    }

    /// <summary>Starts a new game from a given position (Play from here, spec 13.5).</summary>
    public void NewGameFrom(GameState start)
    {
        ExitReplay();
        CancelAi();
        Rules.Validate(start);
        Seed = unchecked((ulong)_clock.UtcNow.Ticks);
        StartedUtc = _clock.UtcNow;
        ResetGame(start);
    }

    /// <summary>
    /// Changes a player. It takes effect at that side's next turn (spec 13.3): a search already running for the side
    /// completes and its ply is played; a side waiting for a human gets the new player at once.
    /// </summary>
    public void SetPlayer(Side side, PlayerOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        _players[(int)side] = option;
        _agents[(int)side] = null;
        _failedSide = null;
        _settleUntil[(int)side] = _clock.Elapsed + PlayerSettle;
        if (option.NeedsSolver)
        {
            LoadTableInBackground(force: true);
        }
    }

    public void TogglePause()
    {
        Paused = !Paused;
        _failedSide = null;
        _lastPlyAt = _clock.Elapsed;
    }

    /// <summary>Called once per frame: collects a finished AI ply, and starts the next AI turn when one is due.</summary>
    public void Update()
    {
        if (_aiTask is { IsCompleted: true } done)
        {
            Collect(done);
        }

        CollectAnalysis();
        if (InReplay || IsOver || AiThinking)
        {
            return;
        }

        if (AwaitingFoxPlacement)
        {
            if (!IsHuman(Side.Fox) && _failedSide != Side.Fox && _clock.Elapsed >= _settleUntil[(int)Side.Fox])
            {
                StartAi(Side.Fox, foxStart: true);
            }

            return;
        }

        Side side = _position.SideToMove;
        if (IsHuman(side) || _failedSide == side || _clock.Elapsed < _settleUntil[(int)side])
        {
            return;
        }

        bool aiVersusAi = !HasHuman;
        if (aiVersusAi && (Paused || _clock.Elapsed - _lastPlyAt < Delay))
        {
            return;
        }

        StartAi(side, foxStart: false);
    }

    /// <summary>A click on a board square (spec 13.4): select a piece, move it, or place the fox.</summary>
    public void ClickSquare(int square)
    {
        ClearMessage();
        if (InReplay || IsOver || AiThinking || square < 0 || square >= Rules.Geo.SquareCount)
        {
            return;
        }

        if (AwaitingFoxPlacement)
        {
            if (IsHuman(Side.Fox) && Rules.FoxStartSquares.Contains((byte)square))
            {
                PlaceFox((byte)square);
            }

            return;
        }

        Side side = _position.SideToMove;
        if (!IsHuman(side))
        {
            return;
        }

        var moves = LegalMoves();
        if (Selected is byte from)
        {
            int index = moves.FindIndex(m => m.From == from && m.To == square);
            if (index >= 0)
            {
                Play(moves[index], null);
                return;
            }
        }

        if (IsOwnPiece(square) && moves.Exists(m => m.From == square))
        {
            Selected = (byte)square;
            return;
        }

        // Clicking a target directly also works when only one of your pieces can reach it.
        var direct = moves.FindAll(m => m.To == square);
        Selected = null;
        if (direct.Count == 1)
        {
            Play(direct[0], null);
        }
    }

    public void ClearSelection() => Selected = null;

    /// <summary>Squares to mark as targets: the selected piece's legal destinations, or the fox's possible start squares.</summary>
    public IReadOnlyList<int> Targets()
    {
        if (IsOver)
        {
            return [];
        }

        if (AwaitingFoxPlacement)
        {
            return IsHuman(Side.Fox) ? Rules.FoxStartSquares.Select(b => (int)b).ToList() : [];
        }

        if (Selected is not byte from || !IsHuman(_position.SideToMove))
        {
            return [];
        }

        return LegalMoves().Where(m => m.From == from).Select(m => (int)m.To).ToList();
    }

    /// <summary>
    /// Undo (spec 13.4): back to the previous position where a human is to move; with no human players, pause and step
    /// back one ply. Cancels a running search first. Does nothing (and returns false) when there is no such position.
    /// </summary>
    public bool Undo()
    {
        ClearMessage();
        int keep = UndoTarget();
        if (InReplay || keep < 0)
        {
            return false;
        }

        CancelAi();
        if (!HasHuman)
        {
            Paused = true;
        }

        TruncateTo(keep);
        return true;
    }

    /// <summary>Redo: replays undone plies, as far as the next position with a human to move (one ply without humans).</summary>
    public bool Redo()
    {
        ClearMessage();
        if (InReplay || _redo.Count == 0)
        {
            return false;
        }

        CancelAi();
        _failedSide = null;
        do
        {
            var ply = _redo.Pop();
            Apply(ply);
        }
        while (_redo.Count > 0 && HasHuman && !IsOver && !IsHuman(_position.SideToMove));

        _lastPlyAt = _clock.Elapsed;
        return true;
    }

    /// <summary>Status line text (spec 13.4).</summary>
    public string Status()
    {
        if (Message is not null)
        {
            return Message;
        }

        if (InReplay)
        {
            return ReplayStatus();
        }

        if (IsOver)
        {
            return ResultText();
        }

        if (_buildProgress >= 0)
        {
            return $"building solver table {(int)(_buildProgress * 100)}%";
        }

        Side side = AwaitingFoxPlacement ? Side.Fox : _position.SideToMove;
        if (AiThinking)
        {
            var agent = Volatile.Read(ref _running!.Agent);
            if (((agent as EpsilonAgent)?.Inner ?? agent) is MinimaxAgent { CurrentSearch: { } search } && search.DepthInProgress > 0)
            {
                return $"thinking - depth {search.DepthInProgress}, {FormatNodes(search.Nodes)} nodes";
            }

            return "thinking";
        }

        if (AwaitingFoxPlacement)
        {
            return IsHuman(Side.Fox) ? "Fox: choose a start square" : "Fox is choosing a start square";
        }

        if (!HasHuman && Paused)
        {
            return "paused";
        }

        return side == Side.Fox ? "Fox to move" : "Hounds to move";
    }

    /// <summary>The winner and the reason (spec 3.4) for the game-over banner; empty while the game goes on.</summary>
    public string ResultText()
    {
        if (!IsOver)
        {
            return "";
        }

        string winner = Outcome.Result == GameResult.FoxWins ? "Fox wins" : "Hounds win";
        string reason = Outcome.Reason switch
        {
            EndReason.FoxReachedHome => "the fox reached the hounds' home row",
            EndReason.FoxTrapped => "the fox is trapped",
            EndReason.HoundsImmobile => "the hounds cannot move",
            EndReason.FoxEscaped => "the fox has escaped",
            EndReason.Forfeit when _forfeit is { } f => $"the {(f.Side == Side.Fox ? "fox" : "hounds")} forfeited ({f.Message})",
            _ => "forfeit",
        };
        return $"{winner}: {reason}";
    }

    /// <summary>Evaluation line (spec 13.4): the last AI score, plus the solver verdict when a table is loaded.</summary>
    public string Evaluation()
    {
        var last = _plies.LastOrDefault(p => p.Info?.ScoreFox is not null)?.Info;
        string ai = last?.ScoreFox is int score ? $"AI: {Scores.DescribeFox(score, last.Exact)}" : "AI: -";
        if (TableProgress is { } progress)
        {
            return $"{ai}   solver: building table {(int)(progress * 100)}%";
        }

        return SolverValue is { } v && !IsOver ? $"{ai}   solver: {v.Describe(_position.SideToMove)}" : ai;
    }

    /// <summary>The move list (spec 13.2), the fox's plies on the left; a game the hounds open starts with "...".</summary>
    public IReadOnlyList<MoveRow> MoveRows()
    {
        var geo = Rules.Geo;
        var rows = new List<MoveRow>();
        int i = 0;
        Side first = _start?.SideToMove ?? Rules.Config.FirstMover;
        if (first == Side.Hounds && _plies.Count > 0)
        {
            rows.Add(new MoveRow(1, "...", geo.FormatMove(_plies[0].Move), -1, 0));
            i = 1;
        }

        for (; i < _plies.Count; i += 2)
        {
            bool hounds = i + 1 < _plies.Count;
            rows.Add(new MoveRow(rows.Count + 1, geo.FormatMove(_plies[i].Move), hounds ? geo.FormatMove(_plies[i + 1].Move) : "", i, hounds ? i + 1 : -1));
        }

        return rows;
    }

    /// <summary>When the fox is trapped at the end, the squares around it (spec 13.2).</summary>
    public IReadOnlyList<int> TrappingSquares()
    {
        if (Outcome.Reason != EndReason.FoxTrapped)
        {
            return [];
        }

        return Rules.Geo.FoxStepList(_position.Fox).ToArray().Select(b => (int)b).ToList();
    }

    /// <summary>
    /// Saves the game as a record in <see cref="GamesDirectory"/>, named after its start time (spec 13.4); saving the
    /// same game again replaces the file. Returns the path, or null (with <see cref="Message"/> saying why).
    /// </summary>
    public string? Save()
    {
        ClearMessage();
        string name = StartedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(GamesDirectory, name + GameRecordFile.Extension);
        try
        {
            GameRecordFile.Write(ToRecord(), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage($"cannot save: {ex.Message}", sticky: true);
            return null;
        }

        Notify($"saved {Path.GetFileName(path)}");
        return path;
    }

    /// <summary>Shows <paramref name="text"/> on the status line for a few seconds.</summary>
    public void Notify(string text) => SetMessage(text, sticky: false);

    /// <summary>Shows <paramref name="text"/> on the status line until the next action, like an error.</summary>
    public void Warn(string text) => SetMessage(text, sticky: true);

    /// <summary>Saved games, newest first.</summary>
    public IReadOnlyList<string> SavedGames() =>
        Directory.Exists(GamesDirectory)
            ? Directory.GetFiles(GamesDirectory, "*" + GameRecordFile.Extension).OrderDescending(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Loads a saved game; on failure the current game is kept and <see cref="Message"/> says why.</summary>
    public bool Load(string path)
    {
        ClearMessage();
        GameRecord record;
        try
        {
            record = GameRecordFile.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            SetMessage($"cannot load {Path.GetFileName(path)}: {ex.Message}", sticky: true);
            return false;
        }

        ExitReplay();
        CancelAi();
        Rules = new Rules(record.RuleSet);
        Seed = record.Seed ?? unchecked((ulong)_clock.UtcNow.Ticks);
        StartedUtc = record.StartedUtc;
        _players[0] = FromRecord(record.Fox);
        _players[1] = FromRecord(record.Hounds);
        ResetGame(record.Start);
        foreach (var ply in record.Plies)
        {
            Apply(ply);
        }

        if (record.Outcome.Reason == EndReason.Forfeit)
        {
            Outcome = record.Outcome;
            _forfeit = record.Forfeit;
        }

        Paused = !HasHuman;
        LoadTableInBackground();
        Notify($"loaded {Path.GetFileName(path)}");
        return true;
    }

    /// <summary>The game so far as a record (spec 14.2).</summary>
    public GameRecord ToRecord() => new()
    {
        RuleSet = Rules.Config,
        Start = _start ?? Rules.Initial(),
        Fox = PlayerRecord(Side.Fox),
        Hounds = PlayerRecord(Side.Hounds),
        Seed = Seed,
        StartedUtc = StartedUtc,
        Plies = _plies.ToArray(),
        Outcome = Outcome,
        Forfeit = _forfeit,
    };

    public void Dispose()
    {
        CancelAnalysis();
        _analysisCts?.Dispose();
        CancelAi();
        _aiCts?.Dispose();
        _tableCts?.Cancel();
        _tableCts?.Dispose();
    }

    private static string FormatNodes(long nodes) =>
        nodes >= 1_000_000 ? $"{nodes / 1e6:0.0}M" : nodes >= 1_000 ? $"{nodes / 1e3:0}k" : nodes.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void ResetGame(GameState? start)
    {
        _rng = new Rng(Seed);
        _start = start;
        _position = start ?? Rules.Create(Rules.Geo.RowMask[0], Rules.DefaultFoxStart, Rules.Config.FirstMover);
        _plies.Clear();
        _redo.Clear();
        _forfeit = null;
        Outcome = start is { } s ? Rules.Evaluate(s) : Outcome.Ongoing;
        Selected = null;
        ClearMessage();
        Array.Clear(_agents);
        Paused = false;
        _failedSide = null;
        _lastPlyAt = _clock.Elapsed;
        Version++;
    }

    private void PlaceFox(byte square)
    {
        _start = Rules.Initial(square);
        _position = _start.Value;
        Outcome = Rules.Evaluate(_position);
        _lastPlyAt = _clock.Elapsed;
        Version++;
    }

    private void Play(Move move, AgentInfo? info)
    {
        _redo.Clear();
        Apply(new PlyRecord(move, info));
        _lastPlyAt = _clock.Elapsed;
    }

    private void Apply(PlyRecord ply)
    {
        _position = Rules.ApplyChecked(_position, ply.Move);
        _plies.Add(ply);
        Selected = null;
        Outcome = Rules.Evaluate(_position);
        Version++;
    }

    /// <summary>
    /// Plies Undo keeps: one fewer with no human players; otherwise the most plies, short of all of them, after which
    /// a human is to move. −1 when Undo has nothing to do.
    /// </summary>
    private int UndoTarget()
    {
        if (_plies.Count == 0 || _start is not { } start)
        {
            return -1;
        }

        if (!HasHuman)
        {
            return _plies.Count - 1;
        }

        // Sides alternate every ply, so the side to move after k plies follows from the start position.
        for (int k = _plies.Count - 1; k >= 0; k--)
        {
            Side side = (k & 1) == 0 ? start.SideToMove : start.SideToMove.Opponent();
            if (IsHuman(side))
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>Takes back plies until <paramref name="keep"/> remain; the taken plies go on the redo stack.</summary>
    private void TruncateTo(int keep)
    {
        for (int i = _plies.Count - 1; i >= keep; i--)
        {
            _redo.Push(_plies[i]);
        }

        _plies.RemoveRange(keep, _plies.Count - keep);
        var s = _start!.Value;
        foreach (var p in _plies)
        {
            s = Rules.Apply(s, p.Move);
        }

        _position = s;
        _forfeit = null;
        Outcome = Rules.Evaluate(_position);
        Selected = null;
        _failedSide = null;
        _lastPlyAt = _clock.Elapsed;
        Version++;
    }

    private void SetMessage(string text, bool sticky)
    {
        _message = text;
        _messageExpires = sticky ? null : _clock.Elapsed + NoticeDuration;
    }

    private void ClearMessage()
    {
        _message = null;
        _messageExpires = null;
    }

    private List<Move> LegalMoves()
    {
        var buffer = new Move[Core.Rules.MaxMoves];
        int count = Rules.Evaluate(_position).IsOver ? 0 : Rules.GenerateMoves(_position, buffer);
        return buffer.Take(count).ToList();
    }

    private bool IsOwnPiece(int square) => _position.SideToMove == Side.Fox
        ? square == _position.Fox
        : ((_position.Hounds >> square) & UInt128.One) != UInt128.Zero;

    private void StartAi(Side side, bool foxStart)
    {
        var option = _players[(int)side];
        var rules = Rules;
        var state = _position;
        var history = _plies.Select(p => p.Move).ToArray();
        var budget = option.Budget ?? option.Spec?.ToBudget() ?? SearchBudget.Unlimited;
        var holder = new AgentHolder { Agent = _agents[(int)side], Side = side, Option = option };
        _agents[(int)side] = null; // owned by the task until it finishes
        var agentRng = _rng.Split();
        var cts = new CancellationTokenSource();
        _aiCts?.Dispose();
        _aiCts = cts;
        int generation = ++_aiGeneration;
        var progress = new Progress(this, generation);
        _running = holder;
        _aiTask = Task.Run(() =>
        {
            var agent = holder.Agent;
            if (agent is null)
            {
                agent = option.Create?.Invoke() ?? _factory.Create(option.Spec!, rules, progress, cts.Token);
                agent.NewGame(rules, side, agentRng);
                Volatile.Write(ref holder.Agent, agent);
            }

            if (foxStart)
            {
                return new AiResult(generation, side, null, agent.ChooseFoxStart(rules));
            }

            var decision = agent.ChooseMove(state, history, budget, cts.Token);
            return new AiResult(generation, side, decision, 0);
        });
    }

    private void Collect(Task<AiResult> task)
    {
        Side? side = _running?.Side;
        ReleaseAgent(task);
        _buildProgress = -1;
        if (!task.IsCompletedSuccessfully)
        {
            if (task.Exception?.GetBaseException() is { } ex and not OperationCanceledException)
            {
                // Not a forfeit as in the game runner: the user can change the player, undo or resume instead.
                SetMessage($"AI error: {ex.Message}", sticky: true);
                Paused = true;
                _failedSide = side;
            }

            return;
        }

        var result = task.Result;
        if (result.Generation != _aiGeneration)
        {
            return; // stale: the game changed while the agent was thinking
        }

        if (result.Decision is null)
        {
            if (!AwaitingFoxPlacement)
            {
                return;
            }

            if (Rules.FoxStartSquares.Contains(result.FoxStart))
            {
                PlaceFox(result.FoxStart);
            }
            else
            {
                // As in the game runner (spec 7.3): a start square off the back row forfeits.
                _forfeit = new ForfeitInfo(Side.Fox, ForfeitCause.InvalidFoxStart, result.FoxStart < Rules.Geo.SquareCount ? $"chose {Rules.Geo.SquareName(result.FoxStart)}" : $"chose square {result.FoxStart}");
                Outcome = Outcome.Win(Side.Hounds, EndReason.Forfeit);
                Version++;
            }

            return;
        }

        if (!Rules.IsLegal(_position, result.Decision.Move))
        {
            _forfeit = new ForfeitInfo(result.Side, ForfeitCause.IllegalMove, $"played {Rules.Geo.FormatMove(result.Decision.Move)}");
            Outcome = Outcome.Win(result.Side.Opponent(), EndReason.Forfeit);
            Version++;
            return;
        }

        Play(result.Decision.Move, result.Decision.Info);
    }

    /// <summary>Cancels a running search and waits for it (it must return within 100 ms; spec 7.4).</summary>
    private void CancelAi()
    {
        if (_aiTask is not { } task)
        {
            return;
        }

        _aiGeneration++;
        _aiCts?.Cancel();
        try
        {
            task.Wait(CancelWait);
        }
        catch (AggregateException)
        {
            // Cancelled or failed: either way its result is discarded.
        }

        ReleaseAgent(task);
        _buildProgress = -1;
    }

    /// <summary>Takes the agent back from a finished task. An agent that failed, whose task has not finished (so it
    /// might still be running), or whose side has since got another player, is dropped.</summary>
    private void ReleaseAgent(Task<AiResult> task)
    {
        bool failed = task.IsFaulted && task.Exception?.GetBaseException() is not OperationCanceledException;
        if (task.IsCompleted && !failed && _running is { } holder
            && ReferenceEquals(_players[(int)holder.Side], holder.Option)
            && Volatile.Read(ref holder.Agent) is { } agent && _agents[(int)holder.Side] is null)
        {
            _agents[(int)holder.Side] = agent;
        }

        _aiTask = null;
        _running = null;
    }

    /// <summary>
    /// Loads (or builds) the solver table for the current rules in the background, for the evaluation line and the
    /// Perfect player. Boards up to <see cref="AutoSolveMaxN"/> always get one; larger supported boards only when a
    /// table file exists or <paramref name="force"/> is set (a player that needs it was chosen). A load for other
    /// rules is cancelled.
    /// </summary>
    private void LoadTableInBackground(bool force = false)
    {
        var rules = Rules;
        var config = rules.Config;
        bool supported = SolverTable.IsSupported(config.BoardSize);
        bool automatic = config.BoardSize <= AutoSolveMaxN || _factory.Tables.Exists(config);
        bool failed = _tableTask is { IsCompletedSuccessfully: true, Result: null };
        if (_tableTask is not null && _tableConfig is { } current && !(force && failed)
            && current.BoardSize == config.BoardSize && current.FoxWinCondition == config.FoxWinCondition)
        {
            return; // loaded, or loading, for these rules already
        }

        _tableCts?.Cancel(); // not disposed: the abandoned load may still read its token
        _tableCts = null;
        _tableTask = null;
        _tableConfig = null;
        if (!supported || !(force || automatic))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var tables = _factory.Tables;
        var progress = new TableLoadProgress(this, cts);
        _tableCts = cts;
        _tableConfig = config;
        Volatile.Write(ref _tableProgress, 0);
        _tableTask = Task.Run(async () =>
        {
            try
            {
                return (SolverTable?)await tables.GetOrBuildAsync(rules, progress, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException
                or NotSupportedException or OutOfMemoryException)
            {
                return null;
            }
        });
    }

    private PlayerInfo PlayerRecord(Side side)
    {
        var option = _players[(int)side];
        if (option.IsHuman)
        {
            return PlayerInfo.Human;
        }

        return new PlayerInfo(PlayerInfo.AgentKind, option.Spec?.Name ?? option.Label, option.Spec?.WithAbsolutePaths().ToJson());
    }

    private PlayerOption FromRecord(PlayerInfo info)
    {
        if (info.Kind != PlayerInfo.AgentKind || info.SpecJson is null)
        {
            return PlayerOption.Human;
        }

        try
        {
            var spec = AgentSpec.Parse(info.SpecJson);
            return ResolvePlayer?.Invoke(spec) ?? new PlayerOption(spec.Name, spec);
        }
        catch (InvalidDataException)
        {
            return PlayerOption.Human;
        }
    }

    private sealed record AiResult(int Generation, Side Side, AgentDecision? Decision, byte FoxStart);

    /// <summary>The agent of the running AI turn, shared with the task; read by the status line for progress.</summary>
    private sealed class AgentHolder
    {
#pragma warning disable CA1051 // A field so the task and the UI thread can use Volatile on it.
        public IAgent? Agent;
#pragma warning restore CA1051

        public Side Side { get; init; }

        public required PlayerOption Option { get; init; }
    }

    /// <summary>Forwards the background table load's progress to the evaluation line while it is current.</summary>
    private sealed class TableLoadProgress(GameSession session, CancellationTokenSource owner) : IProgress<double>
    {
        public void Report(double value)
        {
            if (ReferenceEquals(Volatile.Read(ref session._tableCts), owner))
            {
                Volatile.Write(ref session._tableProgress, value);
            }
        }
    }

    /// <summary>Forwards solver-table build progress to the status line while its AI turn is current.</summary>
    private sealed class Progress(GameSession session, int generation) : IProgress<double>
    {
        public void Report(double value)
        {
            if (Volatile.Read(ref session._aiGeneration) == generation)
            {
                Volatile.Write(ref session._buildProgress, value);
            }
        }
    }
}
