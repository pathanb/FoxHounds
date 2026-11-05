using System.Globalization;
using FoxHounds.Core;
using FoxHounds.Solver;
using Raylib_cs;

namespace FoxHounds.App;

/// <summary>
/// One frame of the app (spec 13): keys, session update, board, control panel, the game-over banner, board clicks.
/// In play the panel shows, top to bottom, the game settings (board size, rules, fox start; changing one starts a new
/// game), the player cyclers, the status line, the evaluation line and the move list (or the saved-games list), with
/// the buttons at the bottom. In the replay viewer (spec 13.5) it shows the shown position, the solver's view, the
/// players (for Play from here), the stored info of the ply, the analysis and a clickable move list.
/// </summary>
internal sealed class AppController
{
    private const int BannerFont = 22;
    private const int ListFont = 16;
    private const int SmallFont = 14;
    private const int ListLine = 22;
    private const int FileRow = 26;
    private const string BlunderMark = "??";
    private static readonly int[] Sizes = [4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly Color PanelColor = new(252, 252, 252, 255);
    private static readonly Color CurrentPly = new(205, 222, 245, 255);

    private readonly GameSession _session;
    private readonly IReadOnlyList<PlayerOption> _allOptions;
    private readonly Ui _ui = new();
    private readonly InputController _input;
    private List<PlayerOption> _options = [];
    private Rules? _optionsRules;
    private bool _optionsSolver;
    private IReadOnlyList<MoveRow> _moveRows = [];
    private int _moveRowsVersion = -1;
    private IReadOnlyList<string> _savedGames = [];
    private int _fileScroll;
    private bool _showingLoadDialog;
    private int _bannerHiddenAt = -1;
    private int _listScroll;
    private int _listCursor = -1;

    public AppController(GameSession session, IReadOnlyList<PlayerOption> options)
    {
        _session = session;
        _allOptions = options;
        _input = new InputController(session, this);

        // A loaded record names its players by spec (with absolute weight paths); show them as the matching preset or
        // custom entry.
        session.ResolvePlayer = spec =>
        {
            string json = spec.WithAbsolutePaths().ToJson();
            return _allOptions.FirstOrDefault(o => o.Spec is not null && o.Spec.WithAbsolutePaths().ToJson() == json);
        };
    }

    /// <summary>True while the saved-games list replaces the move list (spec 13.4: no native file dialog).</summary>
    public bool ShowingLoadDialog
    {
        get => _showingLoadDialog;
        set
        {
            _showingLoadDialog = value;
            if (value)
            {
                _session.ExitReplay();
                _savedGames = _session.SavedGames();
                _fileScroll = 0;
            }
        }
    }

    /// <summary>True while the game-over banner is shown; Esc or a click on the board hides it.</summary>
    public bool BannerVisible => _session.IsOver && !_session.InReplay && _bannerHiddenAt != _session.Version;

    /// <summary>For headless screenshots: the square treated as being under the mouse (for the tooltip).</summary>
    public int? HoverOverride { get; set; }

    public void HideBanner() => _bannerHiddenAt = _session.Version;

    /// <summary>Opens the replay viewer on the current game (spec 13.5).</summary>
    public void EnterReplay()
    {
        HideBanner();
        _showingLoadDialog = false;
        _session.EnterReplay();
        _listCursor = -1;
    }

    public void Frame()
    {
        _ui.BeginFrame();
        _input.HandleKeys();
        _session.Update();
        var layout = Layout.Compute(Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), _session.Rules.Geo.N);
        var board = _session.Board();

        Raylib.BeginDrawing();
        Raylib.ClearBackground(RaylibView.Background);
        RaylibView.DrawBoard(layout, _session.Rules.Geo, board);
        if (_session.InReplay)
        {
            DrawReplayPanel(layout);
        }
        else
        {
            DrawPanel(layout);
        }

        if (BannerVisible)
        {
            DrawBanner(layout);
        }

        if (_session.InReplay)
        {
            DrawTooltip(layout, board);
        }

        _input.HandleBoardClick(layout, _ui);
        Raylib.EndDrawing();
    }

    /// <summary>
    /// Starts a new game under <paramref name="config"/> with the same players, except that a player needing a solver
    /// table that is not available for the new rules (spec 13.3) becomes Human.
    /// </summary>
    public void NewGame(RuleSet config)
    {
        _session.NewGame(config);
        foreach (var side in new[] { Side.Fox, Side.Hounds })
        {
            var player = _session.Player(side);
            if (player.NeedsSolver && !_session.SolverOffered)
            {
                _session.SetPlayer(side, PlayerOption.Human);
                _session.Notify($"{player.Label} has no solver table on {config.BoardSize} x {config.BoardSize}: {side} is Human");
            }
        }
    }

    public void NewGame() => NewGame(_session.Rules.Config);

    private static bool IsVariant(RuleSet config, RuleSet variant) =>
        config.FoxWinCondition == variant.FoxWinCondition && config.AdjudicateEscape == variant.AdjudicateEscape;

    private static string DelayText(int ms) =>
        string.Create(CultureInfo.InvariantCulture, $"{ms / 1000.0:0.##} s");

    private static string QualityWord(MoveQuality quality) => quality switch
    {
        MoveQuality.Fastest => "fastest",
        MoveQuality.Slower => "slower",
        MoveQuality.Blunder => "blunder",
        _ => "lost",
    };

    private static IEnumerable<string> PadLines(List<string> lines, int count) =>
        lines.Concat(Enumerable.Repeat("", Math.Max(0, count - lines.Count)));

    private static int RowOf(IReadOnlyList<MoveRow> rows, int ply)
    {
        for (int k = 0; k < rows.Count; k++)
        {
            if (rows[k].FoxPly == ply || rows[k].HoundsPly == ply)
            {
                return k;
            }
        }

        return 0;
    }

    private List<PlayerOption> Options()
    {
        bool solver = _session.SolverOffered;
        if (!ReferenceEquals(_optionsRules, _session.Rules) || solver != _optionsSolver)
        {
            _options = _allOptions.Where(o => !o.NeedsSolver || solver).ToList();
            _optionsRules = _session.Rules;
            _optionsSolver = solver;
        }

        return _options;
    }

    private IReadOnlyList<MoveRow> Rows()
    {
        if (_moveRowsVersion != _session.Version)
        {
            _moveRows = _session.MoveRows();
            _moveRowsVersion = _session.Version;
        }

        return _moveRows;
    }

    private void DrawPanel(Layout layout)
    {
        Raylib.DrawRectangleRec(Ui.ToRaylib(layout.Panel), PanelColor);
        Raylib.DrawLine((int)layout.Panel.X, 0, (int)layout.Panel.X, (int)layout.Panel.H, Ui.Border);
        var rules = _session.Rules;
        var config = rules.Config;
        int n = config.BoardSize;

        int step = _ui.Cycler(layout.PanelRow(0), "Board", $"{n} x {n}");
        if (step != 0)
        {
            int i = Math.Max(0, Array.IndexOf(Sizes, n));
            int next = Sizes[(i + step + Sizes.Length) % Sizes.Length];
            NewGame(config with { BoardSize = next, FixedFoxColumn = null });
        }

        string variant = IsVariant(config, RuleSet.Classic) ? "Classic" : IsVariant(config, RuleSet.Gilith) ? "Gilith" : "Custom";
        if (_ui.Cycler(layout.PanelRow(1), "Rules", variant) != 0)
        {
            var to = IsVariant(config, RuleSet.Classic) ? RuleSet.Gilith : RuleSet.Classic;
            NewGame(config with { FoxWinCondition = to.FoxWinCondition, AdjudicateEscape = to.AdjudicateEscape });
        }

        bool choose = config.FoxStart == FoxStart.ChooseOnBackRow;
        string start = choose ? "fox chooses" : $"fixed ({rules.Geo.SquareName(rules.DefaultFoxStart)})";
        if (_ui.Cycler(layout.PanelRow(2), "Fox start", start) != 0)
        {
            NewGame(config with { FoxStart = choose ? FoxStart.Fixed : FoxStart.ChooseOnBackRow });
        }

        PlayerCycler(layout.PanelRow(3), Side.Fox);
        PlayerCycler(layout.PanelRow(4), Side.Hounds);
        Ui.Label(layout.PanelRow(5), _session.Status(), Ui.Text);
        Ui.Label(layout.PanelRow(6), _session.Evaluation(), Ui.Muted, ListFont);

        var top = layout.PanelRow(7);
        float bottom = layout.PanelRowFromBottom(2).Y - 8;
        var list = new Rect(top.X, top.Y, top.W, Math.Max(0, bottom - top.Y));
        if (ShowingLoadDialog)
        {
            DrawLoadList(list);
        }
        else
        {
            DrawMoveList(list);
        }

        DrawButtons(layout);
    }

    private void PlayerCycler(Rect row, Side side)
    {
        var options = Options();
        var current = _session.Player(side);
        int index = options.IndexOf(current);
        int step = _ui.Cycler(row, side == Side.Fox ? "Fox" : "Hounds", index >= 0 ? options[index].Label : current.Label);
        if (step != 0)
        {
            int next = index >= 0 ? (index + step + options.Count) % options.Count : step > 0 ? 0 : options.Count - 1;
            _session.SetPlayer(side, options[next]);
        }
    }

    private void DrawMoveList(Rect area)
    {
        var moveRows = Rows();
        int rows = (int)(area.H / ListLine);
        int first = Math.Max(0, moveRows.Count - rows);
        int numberW = Raylib.MeasureText("000.", ListFont);
        int moveW = Raylib.MeasureText("m16-n15", ListFont) + 16;
        for (int k = first; k < moveRows.Count; k++)
        {
            var row = moveRows[k];
            int y = (int)area.Y + ((k - first) * ListLine);
            string number = string.Create(CultureInfo.InvariantCulture, $"{row.Number}.");
            Raylib.DrawText(number, (int)area.X + numberW - Raylib.MeasureText(number, ListFont), y, ListFont, Ui.Muted);
            Raylib.DrawText(row.Fox, (int)area.X + numberW + 10, y, ListFont, Ui.Text);
            Raylib.DrawText(row.Hounds, (int)area.X + numberW + 10 + moveW, y, ListFont, Ui.Text);
        }
    }

    private void DrawLoadList(Rect area)
    {
        Ui.Label(new Rect(area.X, area.Y, area.W, ListLine), "Saved games (Esc closes)", Ui.Muted, ListFont);
        if (_savedGames.Count == 0)
        {
            Ui.Label(new Rect(area.X, area.Y + ListLine + 4, area.W, ListLine), "no saved games yet", Ui.Muted, ListFont);
            return;
        }

        int rows = Math.Max(1, (int)((area.H - ListLine - 4) / FileRow));
        var mouse = _ui.Mouse;
        if (area.Contains(mouse.X, mouse.Y))
        {
            _fileScroll -= Math.Sign(Raylib.GetMouseWheelMove());
        }

        _fileScroll = Math.Clamp(_fileScroll, 0, Math.Max(0, _savedGames.Count - rows));
        for (int k = 0; k < Math.Min(rows, _savedGames.Count - _fileScroll); k++)
        {
            string path = _savedGames[_fileScroll + k];
            var row = new Rect(area.X, area.Y + ListLine + 4 + (k * FileRow), area.W, FileRow - 4);
            if (_ui.Button(row, Path.GetFileNameWithoutExtension(path)))
            {
                _session.Load(path);
                ShowingLoadDialog = false;
            }
        }
    }

    private void DrawButtons(Layout layout)
    {
        var top = layout.PanelRowFromBottom(2);
        if (_ui.Button(Layout.Split(top, 0, 3), "New game"))
        {
            NewGame();
        }

        if (_ui.Button(Layout.Split(top, 1, 3), "Undo", _session.CanUndo))
        {
            _session.Undo();
        }

        if (_ui.Button(Layout.Split(top, 2, 3), "Redo", _session.CanRedo))
        {
            _session.Redo();
        }

        // AI against AI: Play/Pause and the delay between plies (spec 13.4).
        var middle = layout.PanelRowFromBottom(1);
        var play = new Rect(middle.X, middle.Y, (middle.W - 8) / 3, middle.H);
        var delay = new Rect(play.X + play.W + 8, middle.Y, middle.W - play.W - 8, middle.H);
        bool aiOnly = !_session.HasHuman;
        if (_ui.Button(play, _session.Paused ? "Play" : "Pause", aiOnly && !_session.IsOver))
        {
            _session.TogglePause();
        }

        var choices = GameSession.DelayChoicesMs;
        int index = Math.Max(0, Array.IndexOf(choices, (int)_session.Delay.TotalMilliseconds));
        int step = _ui.Cycler(delay, "Delay", DelayText(choices[index]), aiOnly, Raylib.MeasureText("Delay", Ui.FontSize) + 10);
        if (step != 0)
        {
            _session.Delay = TimeSpan.FromMilliseconds(choices[(index + step + choices.Length) % choices.Length]);
        }

        var bottom = layout.PanelRowFromBottom(0);
        if (_ui.Button(Layout.Split(bottom, 0, 3), "Save"))
        {
            _session.Save();
        }

        if (_ui.Button(Layout.Split(bottom, 1, 3), ShowingLoadDialog ? "Close" : "Load"))
        {
            ShowingLoadDialog = !ShowingLoadDialog;
        }

        if (_ui.Button(Layout.Split(bottom, 2, 3), "Replay", _session.CanReplay))
        {
            EnterReplay();
        }
    }

    /// <summary>The replay viewer's panel (spec 13.5).</summary>
    private void DrawReplayPanel(Layout layout)
    {
        var panel = layout.Panel;
        Raylib.DrawRectangleRec(Ui.ToRaylib(panel), PanelColor);
        Raylib.DrawLine((int)panel.X, 0, (int)panel.X, (int)panel.H, Ui.Border);
        float x = panel.X + 12;
        float w = panel.W - 24;
        float y = panel.Y + 10;

        Rect Line(float height)
        {
            var r = new Rect(x, y, w, height);
            y += height;
            return r;
        }

        Ui.Label(Line(28), _session.Status(), Ui.Text);
        Ui.Label(Line(24), _session.ReplaySolverText(), Ui.Text, ListFont);
        string position = $"position {_session.Rules.ToNotation(_session.ReplayPosition)}";
        const string copyHint = "  (Ctrl+C copies)";
        Ui.Label(Line(20), Raylib.MeasureText(position + copyHint, SmallFont) <= w ? position + copyHint : position, Ui.Muted, SmallFont);
        DrawLegend(Line(24));
        y += 4;
        PlayerCycler(Line(Layout.RowHeight - 6), Side.Fox);
        y += 6;
        PlayerCycler(Line(Layout.RowHeight - 6), Side.Hounds);
        y += 8;

        var quality = _session.ReplayPlyQuality();
        Ui.Label(Line(22), _session.ReplayPlyText(), quality == MoveQuality.Blunder ? RaylibView.Blunder : Ui.Text, ListFont);
        foreach (string text in PadLines(Ui.Wrap(_session.ReplayInfoText(), (int)w, SmallFont, 2), 2))
        {
            Ui.Label(Line(18), text, Ui.Muted, SmallFont);
        }

        foreach (string text in PadLines(Ui.Wrap(_session.ReplayLineText(), (int)w, SmallFont, 2), 2))
        {
            Ui.Label(Line(18), text, Ui.Muted, SmallFont);
        }

        y += 4;
        Ui.Label(Line(22), _session.AnalysisText(), Ui.Text, ListFont);
        Ui.Label(Line(18), _session.AnalysisDetailText(), Ui.Muted, SmallFont);
        foreach (string text in PadLines(Ui.Wrap(_session.AnalysisLineText(), (int)w, SmallFont, 1), 1))
        {
            Ui.Label(Line(18), text, Ui.Muted, SmallFont);
        }

        y += 6;
        float bottom = layout.PanelRowFromBottom(2).Y - 8;
        DrawReplayMoveList(new Rect(x, y, w, Math.Max(0, bottom - y)));
        DrawReplayButtons(layout);
    }

    /// <summary>The four solver colours with their meaning (spec 13.5), or why there are none.</summary>
    private void DrawLegend(Rect row)
    {
        if (_session.Judgements() is null)
        {
            Ui.Label(row, "no solver colours on this board", Ui.Muted, SmallFont);
            return;
        }

        float x = row.X;
        foreach (var quality in new[] { MoveQuality.Fastest, MoveQuality.Slower, MoveQuality.Blunder, MoveQuality.AlreadyLost })
        {
            int size = SmallFont - 2;
            Raylib.DrawRectangle((int)x, (int)(row.Y + ((row.H - size) / 2)), size, size, RaylibView.ColourOf(quality));
            string word = QualityWord(quality);
            Raylib.DrawText(word, (int)x + size + 4, (int)(row.Y + ((row.H - SmallFont) / 2)), SmallFont, Ui.Muted);
            x += size + 4 + Raylib.MeasureText(word, SmallFont) + 12;
        }
    }

    /// <summary>The move list of the viewer: the shown ply highlighted, blunders marked "??", a click jumps there.</summary>
    private void DrawReplayMoveList(Rect area)
    {
        var moveRows = Rows();
        var judged = _session.Judgements();
        int visible = Math.Max(1, (int)(area.H / ListLine));
        int ply = _session.ReplayCursor - 1;
        int currentRow = ply < 0 ? 0 : RowOf(moveRows, ply);
        if (_session.ReplayCursor != _listCursor)
        {
            // Keep the shown ply in view when the cursor moves; the wheel scrolls freely in between.
            if (currentRow < _listScroll)
            {
                _listScroll = currentRow;
            }
            else if (currentRow >= _listScroll + visible)
            {
                _listScroll = currentRow - visible + 1;
            }

            _listCursor = _session.ReplayCursor;
        }

        var mouse = _ui.Mouse;
        if (area.Contains(mouse.X, mouse.Y))
        {
            _listScroll -= Math.Sign(Raylib.GetMouseWheelMove()) * 3;
        }

        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, moveRows.Count - visible));
        int numberW = Raylib.MeasureText("000.", ListFont);
        int cellW = Raylib.MeasureText("m16-n15 ??", ListFont) + 12;
        for (int k = _listScroll; k < Math.Min(moveRows.Count, _listScroll + visible); k++)
        {
            var row = moveRows[k];
            float y = area.Y + ((k - _listScroll) * ListLine);
            string number = string.Create(CultureInfo.InvariantCulture, $"{row.Number}.");
            Raylib.DrawText(number, (int)area.X + numberW - Raylib.MeasureText(number, ListFont), (int)y + 3, ListFont, Ui.Muted);
            MoveCell(new Rect(area.X + numberW + 6, y, cellW, ListLine), row.Fox, row.FoxPly, ply, judged);
            MoveCell(new Rect(area.X + numberW + 6 + cellW + 4, y, cellW, ListLine), row.Hounds, row.HoundsPly, ply, judged);
        }
    }

    private void MoveCell(Rect cell, string text, int ply, int current, IReadOnlyList<PlyJudgement>? judged)
    {
        if (ply < 0)
        {
            Raylib.DrawText(text, (int)cell.X + 4, (int)cell.Y + 3, ListFont, Ui.Muted);
            return;
        }

        bool hover = cell.Contains(_ui.Mouse.X, _ui.Mouse.Y);
        if (ply == current || hover)
        {
            Raylib.DrawRectangleRec(Ui.ToRaylib(cell), ply == current ? CurrentPly : Ui.Face);
        }

        Raylib.DrawText(text, (int)cell.X + 4, (int)cell.Y + 3, ListFont, Ui.Text);
        if (judged is not null && judged[ply].Blunder)
        {
            Raylib.DrawText(BlunderMark, (int)cell.X + 4 + Raylib.MeasureText(text + " ", ListFont), (int)cell.Y + 3, ListFont, RaylibView.Blunder);
        }

        if (_ui.TakeClick(cell))
        {
            _session.ReplayTo(ply + 1);
        }
    }

    private void DrawReplayButtons(Layout layout)
    {
        int cursor = _session.ReplayCursor;
        int end = _session.Plies.Count;
        var top = layout.PanelRowFromBottom(2);
        if (_ui.Button(Layout.Split(top, 0, 4), "|<", cursor > 0))
        {
            _session.ReplayTo(0);
        }

        if (_ui.Button(Layout.Split(top, 1, 4), "<", cursor > 0))
        {
            _session.ReplayStep(-1);
        }

        if (_ui.Button(Layout.Split(top, 2, 4), ">", cursor < end))
        {
            _session.ReplayStep(1);
        }

        if (_ui.Button(Layout.Split(top, 3, 4), ">|", cursor < end))
        {
            _session.ReplayTo(end);
        }

        var middle = layout.PanelRowFromBottom(1);
        if (_session.Analysing)
        {
            if (_ui.Button(Layout.Split(middle, 0, 2), "Stop"))
            {
                _session.StopAnalysis();
            }
        }
        else if (_ui.Button(Layout.Split(middle, 0, 2), "Analyse", _session.CanAnalyse))
        {
            _session.Analyse();
        }

        if (_ui.Button(Layout.Split(middle, 1, 2), "Play from here", _session.CanPlayFromHere))
        {
            _session.PlayFromHere();
        }

        var bottom = layout.PanelRowFromBottom(0);
        if (_ui.Button(Layout.Split(bottom, 0, 2), "Back to game"))
        {
            _session.ExitReplay();
        }

        if (_ui.Button(Layout.Split(bottom, 1, 2), "Save"))
        {
            _session.Save();
        }
    }

    /// <summary>In the viewer: the solver's value of every ply into the square under the mouse.</summary>
    private void DrawTooltip(Layout layout, BoardView board)
    {
        if (board.Graded.Count == 0)
        {
            return;
        }

        var geo = _session.Rules.Geo;
        var mouse = _ui.Mouse;
        int square = HoverOverride ?? layout.SquareAt(mouse.X, mouse.Y, geo);
        if (square < 0)
        {
            return;
        }

        var side = board.Position.SideToMove;
        var lines = board.Graded.Where(g => g.Move.To == square)
            .Select(g => ($"{geo.FormatMove(g.Move)}  {g.ValueForMover.Describe(side)}  {QualityWord(g.Quality)}{(g.Move == board.NextMove ? "  (played)" : "")}",
                RaylibView.ColourOf(g.Quality)))
            .ToList();
        if (lines.Count == 0)
        {
            return;
        }

        if (HoverOverride is int forced)
        {
            var cell = layout.CellRect(geo.Row[forced], geo.Col[forced]);
            mouse = new System.Numerics.Vector2(cell.X + (cell.W / 2), cell.Y + (cell.H / 2));
        }

        RaylibView.DrawTooltip(mouse, lines, ListFont);
    }

    /// <summary>Game over (spec 13.4): the winner and reason, with New game, Save and Replay.</summary>
    private void DrawBanner(Layout layout)
    {
        string text = _session.ResultText();
        float w = Math.Min(layout.Board.W - 16, Math.Max(Raylib.MeasureText(text, BannerFont) + 48, 360));
        const float h = 104;

        // The banner goes in the half of the board away from the fox, so the final position around it stays visible.
        var geo = _session.Rules.Geo;
        bool foxLow = !_session.AwaitingFoxPlacement && geo.Row[_session.Position.Fox] >= geo.N / 2;
        float centreY = layout.Board.Y + (layout.Board.H * (foxLow ? 0.25f : 0.75f));
        var box = new Rect(layout.Board.X + ((layout.Board.W - w) / 2), centreY - (h / 2), w, h);
        var textRow = new Rect(box.X + 12, box.Y + 10, box.W - 24, 40);
        RaylibView.DrawBannerBox(box, textRow, Ui.Fit(text, (int)textRow.W, BannerFont), BannerFont);
        var buttons = new Rect(box.X + 16, box.Y + box.H - 14 - 32, box.W - 32, 32);
        if (_ui.Button(Layout.Split(buttons, 0, 3), "New game"))
        {
            NewGame();
        }

        if (_ui.Button(Layout.Split(buttons, 1, 3), "Save"))
        {
            _session.Save();
        }

        if (_ui.Button(Layout.Split(buttons, 2, 3), "Replay", _session.CanReplay))
        {
            EnterReplay();
        }
    }
}

/// <summary>Turns key presses and board clicks into session commands (spec 13.1, 13.4, 13.5).</summary>
internal sealed class InputController(GameSession session, AppController app)
{
    public void HandleKeys()
    {
        bool ctrl = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);
        if (session.InReplay)
        {
            HandleReplayKeys(ctrl);
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            if (app.ShowingLoadDialog)
            {
                app.ShowingLoadDialog = false;
            }
            else if (app.BannerVisible)
            {
                app.HideBanner();
            }
            else
            {
                session.ClearSelection();
            }
        }

        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.Z))
        {
            session.Undo();
        }

        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.Y))
        {
            session.Redo();
        }

        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.S))
        {
            session.Save();
        }

        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.O))
        {
            app.ShowingLoadDialog = !app.ShowingLoadDialog;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Space) && !session.HasHuman && !session.IsOver)
        {
            session.TogglePause();
        }
    }

    /// <summary>A click on the board that no widget took: hides the game-over banner, or goes to the session.</summary>
    public void HandleBoardClick(Layout layout, Ui ui)
    {
        var mouse = ui.Mouse;
        if (!ui.TakeClick(layout.Board))
        {
            return;
        }

        if (app.BannerVisible)
        {
            app.HideBanner();
            return;
        }

        int square = layout.SquareAt(mouse.X, mouse.Y, session.Rules.Geo);
        if (square >= 0)
        {
            session.ClickSquare(square);
        }
    }

    private static bool Pressed(KeyboardKey key) => Raylib.IsKeyPressed(key) || Raylib.IsKeyPressedRepeat(key);

    /// <summary>The viewer (spec 13.5): arrows step a ply, Home and End jump, Esc closes, Ctrl+C copies the position.</summary>
    private void HandleReplayKeys(bool ctrl)
    {
        if (Pressed(KeyboardKey.Left))
        {
            session.ReplayStep(-1);
        }

        if (Pressed(KeyboardKey.Right))
        {
            session.ReplayStep(1);
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Home))
        {
            session.ReplayTo(0);
        }

        if (Raylib.IsKeyPressed(KeyboardKey.End))
        {
            session.ReplayTo(session.Plies.Count);
        }

        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.C))
        {
            Raylib.SetClipboardText(session.Rules.ToNotation(session.ReplayPosition));
            session.Notify("position copied");
        }

        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.S))
        {
            session.Save();
        }

        if (ctrl && Raylib.IsKeyPressed(KeyboardKey.O))
        {
            app.ShowingLoadDialog = true;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            session.ExitReplay();
        }
    }
}
