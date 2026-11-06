using System.Globalization;
using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Solver;

namespace FoxHounds.App;

/// <summary>What the board shows (spec 13.2, 13.5): a read-only snapshot for the view, in play or in replay.</summary>
/// <param name="Position">The position on the board.</param>
/// <param name="FoxPlaced">False while the fox's start square is still to be chosen (the fox is not drawn).</param>
/// <param name="LastMove">The ply that led to the position, tinted.</param>
/// <param name="TrappingSquares">Squares around a trapped fox at the end of the game.</param>
/// <param name="Selected">The selected piece in play.</param>
/// <param name="Targets">Squares marked as targets in play (dots).</param>
/// <param name="Graded">In replay with a solver table: every legal ply, coloured by the solver.</param>
/// <param name="NextMove">In replay: the ply the game went on with from this position.</param>
internal sealed record BoardView(
    GameState Position,
    bool FoxPlaced,
    Move? LastMove,
    IReadOnlyList<int> TrappingSquares,
    byte? Selected,
    IReadOnlyList<int> Targets,
    IReadOnlyList<GradedMove> Graded,
    Move? NextMove);

/// <summary>
/// The replay and analysis viewer's logic (spec 13.5): a cursor over the game's positions, the solver's colours and
/// blunders, the agent info stored with each ply, a background "Analyse" search and "Play from here". While the viewer
/// is open the game itself stands still: no AI turn starts and board clicks are ignored.
/// </summary>
internal sealed partial class GameSession
{
    /// <summary>Transposition table of the analysis search, the size the presets use.</summary>
    private const int AnalysisTtMegabytes = 64;

    private List<GameState>? _replay; // positions after 0, 1, ... plies while the viewer is open
    private int _cursor;
    private PlyJudgement[]? _judgements;
    private int _judgedVersion = -1;
    private SolverTable? _judgedWith;
    private GradedMove[] _graded = [];
    private int _gradedCursor = -1;
    private SolverTable? _gradedWith;
    private MoveQuality? _plyQuality;
    private int _plyQualityCursor = -1;
    private SolverTable? _plyQualityWith;
    private Search? _analysisSearch;
    private Task<AnalysisResult>? _analysisTask;
    private CancellationTokenSource? _analysisCts;
    private int _analysisCursor;
    private string? _analysisError;
    private readonly Dictionary<int, AnalysisResult> _analyses = [];

    /// <summary>True while the replay viewer is open.</summary>
    public bool InReplay => _replay is not null;

    /// <summary>True when the viewer can open on this game: the fox is placed and at least one ply was played.</summary>
    public bool CanReplay => !InReplay && _start is not null && _plies.Count > 0;

    /// <summary>Plies played before the shown position: 0 is the start, <see cref="Plies"/>.Count the end.</summary>
    public int ReplayCursor => _cursor;

    /// <summary>The position the viewer shows.</summary>
    /// <exception cref="InvalidOperationException">The viewer is not open.</exception>
    public GameState ReplayPosition => (_replay ?? throw new InvalidOperationException("The replay viewer is not open."))[_cursor];

    /// <summary>True when "Play from here" can start a game: the shown position is not finished.</summary>
    public bool CanPlayFromHere => InReplay && !Rules.Evaluate(ReplayPosition).IsOver;

    /// <summary>True while the analysis search runs.</summary>
    public bool Analysing => _analysisTask is { IsCompleted: false };

    /// <summary>True when "Analyse" can start: the viewer is open on an unfinished position and no analysis runs.</summary>
    public bool CanAnalyse => CanPlayFromHere && !Analysing;

    /// <summary>The budget of "Analyse": up to 2 s (spec 13.5). Tests use a depth limit instead.</summary>
    internal SearchBudget AnalysisBudget { get; set; } = new(null, null, null, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Opens the viewer at the end of the game. A running search is cancelled and no AI turn starts until the viewer
    /// closes.
    /// </summary>
    public void EnterReplay()
    {
        if (!CanReplay)
        {
            return;
        }

        CancelAi();
        ClearMessage();
        Selected = null;
        var s = _start!.Value;
        _replay = new List<GameState>(_plies.Count + 1) { s };
        foreach (var ply in _plies)
        {
            s = Rules.Apply(s, ply.Move);
            _replay.Add(s);
        }

        _cursor = _plies.Count;
        _analyses.Clear();
        _analysisError = null;
    }

    /// <summary>Closes the viewer; the game goes on where it was.</summary>
    public void ExitReplay()
    {
        if (!InReplay)
        {
            return;
        }

        CancelAnalysis();
        _replay = null;
        _analyses.Clear();
        _lastPlyAt = _clock.Elapsed;
    }

    /// <summary>Shows the position after <paramref name="cursor"/> plies (clamped); a running analysis is dropped.</summary>
    public void ReplayTo(int cursor)
    {
        if (!InReplay)
        {
            return;
        }

        cursor = Math.Clamp(cursor, 0, _plies.Count);
        if (cursor != _cursor)
        {
            CollectAnalysis(); // a result that has just come in is kept with its position
            CancelAnalysis();
            _cursor = cursor;
            _analysisError = null;
        }
    }

    /// <summary>Steps the viewer by <paramref name="delta"/> plies.</summary>
    public void ReplayStep(int delta) => ReplayTo(_cursor + delta);

    /// <summary>"Play from here" (spec 13.5): a new game from the shown position, with the players now selected.</summary>
    public void PlayFromHere()
    {
        if (!CanPlayFromHere)
        {
            return;
        }

        var position = ReplayPosition;
        ExitReplay();
        NewGameFrom(position);
    }

    /// <summary>The board to draw: the game in play, or the shown position in the viewer.</summary>
    public BoardView Board()
    {
        if (!InReplay)
        {
            return new BoardView(_position, !AwaitingFoxPlacement, _plies.Count > 0 ? _plies[^1].Move : null,
                TrappingSquares(), Selected, Targets(), [], null);
        }

        bool atEnd = _cursor == _plies.Count;
        return new BoardView(ReplayPosition, FoxPlaced: true, _cursor > 0 ? _plies[_cursor - 1].Move : null,
            atEnd ? TrappingSquares() : [], null, [], ReplayGrades(), atEnd ? null : _plies[_cursor].Move);
    }

    /// <summary>
    /// The solver's verdict on every ply of the game (spec 11.5, the harness's definition), or null without a table
    /// for these rules.
    /// </summary>
    public IReadOnlyList<PlyJudgement>? Judgements()
    {
        if (CurrentTable() is not { } table || _start is not { } start)
        {
            return null;
        }

        if (_judgedVersion != Version || !ReferenceEquals(_judgedWith, table))
        {
            _judgements = MoveGrading.JudgeGame(Rules, table, start, _plies.Select(p => p.Move).ToList());
            _judgedVersion = Version;
            _judgedWith = table;
        }

        return _judgements;
    }

    /// <summary>The legal plies of the shown position, coloured by the solver (spec 13.5); empty without a table.</summary>
    public IReadOnlyList<GradedMove> ReplayGrades()
    {
        if (!InReplay || CurrentTable() is not { } table)
        {
            return [];
        }

        if (_gradedCursor != _cursor || !ReferenceEquals(_gradedWith, table))
        {
            _graded = Grade(table, ReplayPosition);
            _gradedCursor = _cursor;
            _gradedWith = table;
        }

        return _graded;
    }

    /// <summary>The solver's class of the ply that led to the shown position; null at the start or without a table.</summary>
    public MoveQuality? ReplayPlyQuality()
    {
        if (!InReplay || _cursor == 0 || CurrentTable() is not { } table)
        {
            return null;
        }

        if (_plyQualityCursor != _cursor || !ReferenceEquals(_plyQualityWith, table))
        {
            var played = _plies[_cursor - 1].Move;
            var options = Grade(table, _replay![_cursor - 1]);
            int index = Array.FindIndex(options, g => g.Move == played);
            _plyQuality = index >= 0 ? options[index].Quality : null;
            _plyQualityCursor = _cursor;
            _plyQualityWith = table;
        }

        return _plyQuality;
    }

    /// <summary>Header of the viewer: the cursor and the side to move.</summary>
    public string ReplayStatus()
    {
        if (!InReplay)
        {
            return "";
        }

        var s = ReplayPosition;
        string turn = Rules.Evaluate(s).IsOver ? "game over" : s.SideToMove == Side.Fox ? "fox to move" : "hounds to move";
        return string.Create(CultureInfo.InvariantCulture, $"Ply {_cursor} of {_plies.Count}, {turn}");
    }

    /// <summary>The solver's view of the shown position, or the result at the end of the game.</summary>
    public string ReplaySolverText()
    {
        if (!InReplay)
        {
            return "";
        }

        var s = ReplayPosition;
        if (_cursor == _plies.Count && IsOver)
        {
            return ResultText();
        }

        if (TableProgress is { } progress)
        {
            return $"solver: building table {(int)(progress * 100)}%";
        }

        if (CurrentTable() is { } table && table.TryGet(s, out var value))
        {
            return $"solver: {value.Describe(s.SideToMove)}";
        }

        int n = Rules.Geo.N;
        return SolverOffered ? $"solver: no table for {n} x {n}" : $"solver: none for {n} x {n}";
    }

    /// <summary>"23. ... b8-c7" for the ply that led to the shown position, with the solver's class; "start position" at 0.</summary>
    public string ReplayPlyText()
    {
        if (!InReplay)
        {
            return "";
        }

        if (_cursor == 0)
        {
            return "start position";
        }

        string text = PlyLabel(_cursor - 1);
        return ReplayPlyQuality() is { } quality ? $"{text}: {QualityText(quality)}" : text;
    }

    /// <summary>The agent info stored with the ply that led to the shown position (spec 13.5).</summary>
    public string ReplayInfoText() => !InReplay || _cursor == 0 ? "" : DescribeInfo(_plies[_cursor - 1].Info);

    /// <summary>The principal variation stored with that ply, or empty.</summary>
    public string ReplayLineText() =>
        !InReplay || _cursor == 0 || _plies[_cursor - 1].Info is not { PrincipalVariation.Count: > 0 } info ? "" : "line " + FormatLine(info.PrincipalVariation);

    /// <summary>The analysis of the shown position: progress while it runs, then its result.</summary>
    public string AnalysisText()
    {
        if (!InReplay)
        {
            return "";
        }

        CollectAnalysis();
        if (Analysing && _analysisCursor == _cursor && _analysisSearch is { } search)
        {
            int depth = search.DepthInProgress;
            return depth > 0 ? $"analysing - depth {depth}, {FormatNodes(search.Nodes)} nodes" : "analysing";
        }

        if (_analysisError is { } error)
        {
            return $"analysis failed: {error}";
        }

        if (!_analyses.TryGetValue(_cursor, out var analysis))
        {
            return "";
        }

        var info = analysis.Info;
        if (info.DepthReached == 0)
        {
            return "analysis: stopped before depth 1";
        }

        return $"analysis: {Rules.Geo.FormatMove(analysis.Move)}, {Scores.DescribeFox(info.ScoreFox ?? 0, info.Exact)}";
    }

    /// <summary>Depth, nodes and time of the analysis kept for the shown position, or empty.</summary>
    public string AnalysisDetailText() =>
        ReplayAnalysis() is { Info.DepthReached: > 0 } analysis && !Analysing
            ? string.Create(CultureInfo.InvariantCulture,
                $"depth {analysis.Info.DepthReached}, {FormatNodes(analysis.Info.Nodes)} nodes, {analysis.Info.Elapsed.TotalSeconds:0.0} s")
            : "";

    /// <summary>The analysis's principal variation, or empty.</summary>
    public string AnalysisLineText() =>
        ReplayAnalysis() is { Info.DepthReached: > 0 } analysis && !Analysing ? "line " + FormatLine(analysis.Info.PrincipalVariation) : "";

    /// <summary>
    /// The analysis kept for the shown position, if any. A search that has just finished counts at once: its result is
    /// collected here rather than waiting for the next <see cref="Update"/>.
    /// </summary>
    public AnalysisResult? ReplayAnalysis()
    {
        if (!InReplay)
        {
            return null;
        }

        CollectAnalysis();
        return _analyses.TryGetValue(_cursor, out var analysis) ? analysis : null;
    }

    /// <summary>
    /// "Analyse" (spec 13.5): a fresh search of the shown position in the background, up to 2 s, with the default
    /// evaluator (lin-v0) and its own 64 MB table, cleared first, so the result does not depend on what was analysed
    /// before. The result stays with the position while the viewer is open.
    /// </summary>
    public void Analyse()
    {
        CollectAnalysis(); // a finished search's result is kept before a new one starts
        if (!CanAnalyse)
        {
            return;
        }

        var rules = Rules;
        var position = ReplayPosition;
        var budget = AnalysisBudget;
        if (_analysisSearch is null || !ReferenceEquals(_analysisSearch.Rules, rules))
        {
            var evaluator = _factory.CreateEvaluator(null, "", rules);
            _analysisSearch = new Search(rules, evaluator, new SearchOptions { TtMegabytes = AnalysisTtMegabytes }, _clock);
        }

        var search = _analysisSearch;
        var cts = new CancellationTokenSource();
        _analysisCts?.Dispose();
        _analysisCts = cts;
        _analysisCursor = _cursor;
        _analysisError = null;
        _analyses.Remove(_cursor);
        _analysisTask = Task.Run(() =>
        {
            search.NewGame();
            var result = search.Run(position, budget, cts.Token);
            var info = new AgentInfo(Scores.ToFox(result.Score, position.SideToMove), result.Depth, result.Nodes, result.Elapsed,
                search.PrincipalVariation.ToArray(), AgentInfo.SourceSearch, result.Exact);
            return new AnalysisResult(result.Move, info);
        });
    }

    /// <summary>Stops the analysis early; the deepest completed iteration is kept.</summary>
    public void StopAnalysis() => _analysisCts?.Cancel();

    /// <summary>Describes a solver class for the panel and the tooltip.</summary>
    public static string QualityText(MoveQuality quality) => quality switch
    {
        MoveQuality.Fastest => "fastest win",
        MoveQuality.Slower => "slower win",
        MoveQuality.Blunder => "blunder",
        _ => "already lost",
    };

    /// <summary>The stored info of one ply as text: score, depth, nodes and time, or who played it.</summary>
    public static string DescribeInfo(AgentInfo? info)
    {
        if (info is null)
        {
            return "played by a human";
        }

        if (info.Source == AgentInfo.SourceSolver)
        {
            return info.ScoreFox is int exact ? $"solver: {Scores.DescribeFox(exact, true)}" : "solver";
        }

        if (info.Source == AgentInfo.SourceRandom)
        {
            return "random ply";
        }

        string score = info.ScoreFox is int s ? Scores.DescribeFox(s, info.Exact) : "-";
        return string.Create(CultureInfo.InvariantCulture,
            $"score {score}, depth {info.DepthReached}, {FormatNodes(info.Nodes)} nodes, {info.Elapsed.TotalSeconds:0.00} s");
    }

    /// <summary>"23. e1-d2" or "23. ... b8-c7": a ply with its number in the move list.</summary>
    public string PlyLabel(int ply)
    {
        Side first = _start?.SideToMove ?? Rules.Config.FirstMover;
        int number;
        bool fox;
        if (first == Side.Fox)
        {
            number = (ply / 2) + 1;
            fox = ply % 2 == 0;
        }
        else
        {
            number = ply == 0 ? 1 : ((ply - 1) / 2) + 2;
            fox = ply != 0 && (ply - 1) % 2 == 0;
        }

        string move = Rules.Geo.FormatMove(_plies[ply].Move);
        return string.Create(CultureInfo.InvariantCulture, $"{number}. {(fox ? "" : "... ")}{move}");
    }

    private string FormatLine(IReadOnlyList<Move> line) => string.Join(' ', line.Select(Rules.Geo.FormatMove));

    /// <summary>The loaded solver table when it is for these rules.</summary>
    private SolverTable? CurrentTable() => Table is { } table && table.Covers(Rules.Config) ? table : null;

    private GradedMove[] Grade(SolverTable table, in GameState s)
    {
        if (Rules.Evaluate(s).IsOver)
        {
            return [];
        }

        var buffer = new GradedMove[Core.Rules.MaxMoves];
        return buffer[..MoveGrading.Grade(table, s, buffer)];
    }

    /// <summary>Called by <see cref="Update"/>: keeps a finished analysis's result with its position.</summary>
    private void CollectAnalysis()
    {
        if (_analysisTask is not { IsCompleted: true } task)
        {
            return;
        }

        _analysisTask = null;
        if (task.IsCompletedSuccessfully)
        {
            _analyses[_analysisCursor] = task.Result;
        }
        else if (task.Exception?.GetBaseException() is { } ex and not OperationCanceledException)
        {
            _analysisError = ex.Message;
        }
    }

    /// <summary>Cancels a running analysis, waits for it and drops its result.</summary>
    private void CancelAnalysis()
    {
        if (_analysisTask is not { } task)
        {
            return;
        }

        _analysisCts?.Cancel();
        bool finished;
        try
        {
            finished = task.Wait(CancelWait);
        }
        catch (AggregateException)
        {
            finished = true; // failed: nothing to keep
        }

        if (!finished)
        {
            _analysisSearch = null; // still running: a new analysis must not share it
        }

        _analysisTask = null;
    }
}

/// <summary>A finished analysis: the move the search prefers and its statistics.</summary>
/// <param name="Move">The best move found.</param>
/// <param name="Info">Score, depth, nodes, time and line.</param>
internal sealed record AnalysisResult(Move Move, AgentInfo Info);
