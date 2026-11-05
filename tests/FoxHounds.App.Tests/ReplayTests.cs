using FoxHounds.Core;
using FoxHounds.Solver;
using static FoxHounds.App.Tests.Sessions;

namespace FoxHounds.App.Tests;

/// <summary>The replay and analysis viewer's logic (spec 13.5, manual checklist 16.1 items 9–12) without a window.</summary>
public sealed class ReplayTests
{
    private static readonly RuleSet Six = RuleSet.Classic with { BoardSize = 6 };

    [Fact]
    public void The_viewer_steps_through_every_position_of_a_game_forward_and_back()
    {
        using var s = PlayOut(RuleSet.Classic);
        Assert.True(s.Plies.Count >= 10, $"only {s.Plies.Count} plies");
        StepThrough(s);
    }

    [Fact]
    public void The_longest_game_the_8x8_board_allows_57_plies_replays_forward_and_back()
    {
        // Manual checklist 16.1 item 9. Each hound marches down its own two-file lane in turn; the fox shuffles e1-f2,
        // climbs to the top left once the left lanes are clear, then shuffles a7-b8. Under the Gilith rules the hounds
        // are stuck after ply 57 (2 * 4 hounds * 7 rows + 1): the fox wins.
        string[] lanes = ["b8 a7 b6 a5 b4 a3 b2 a1", "d8 c7 d6 c5 d4 c3 d2 c1", "h8 g7 h6 g5 h4 g3 h2 g1", "f8 e7 f6 e5 f4 e3 f2 e1"];
        var hounds = lanes.SelectMany(lane =>
        {
            string[] path = lane.Split(' ');
            return path.Zip(path.Skip(1), (a, b) => $"{a}-{b}");
        }).ToList();
        var fox = new List<string>();
        string at = "e1";
        void Fox(string to)
        {
            fox.Add($"{at}-{to}");
            at = to;
        }

        for (int k = 0; k < 15; k++)
        {
            Fox(at == "e1" ? "f2" : "e1");
        }

        foreach (string to in new[] { "e3", "d4", "c5", "b6", "a7" })
        {
            Fox(to);
        }

        while (fox.Count < 29)
        {
            Fox(at == "a7" ? "b8" : "a7");
        }

        using var s = Create(out _, RuleSet.Gilith);
        Assert.Equal(57, s.Rules.MaxPliesFrom(s.Position));
        for (int i = 0; i < 29; i++)
        {
            Click(s, fox[i]);
            if (i < 28)
            {
                Click(s, hounds[i]);
            }
        }

        Assert.Equal(57, s.Plies.Count);
        Assert.Equal(EndReason.HoundsImmobile, s.Outcome.Reason);
        StepThrough(s);
    }

    /// <summary>Opens the viewer on a finished game and steps to the start and back to the end, checking each position.</summary>
    private static void StepThrough(GameSession s)
    {
        var positions = s.ToRecord().Replay(s.Rules);
        int n = s.Plies.Count;
        Assert.False(s.InReplay);
        Assert.True(s.CanReplay);

        s.EnterReplay();
        Assert.True(s.InReplay);
        Assert.False(s.CanReplay);
        Assert.Equal(n, s.ReplayCursor);                       // opens at the end of the game
        Assert.Equal(s.Position, s.ReplayPosition);
        for (int k = n; k >= 0; k--)
        {
            Assert.Equal(k, s.ReplayCursor);
            Assert.Equal(positions[k], s.ReplayPosition);
            Assert.Equal(positions[k], s.Board().Position);
            Assert.Equal(k > 0 ? s.Plies[k - 1].Move : null, s.Board().LastMove);
            Assert.Equal(k < n ? s.Plies[k].Move : null, s.Board().NextMove);
            s.ReplayStep(-1);
        }

        Assert.Equal(0, s.ReplayCursor);                       // clamped at the start
        Assert.Equal("start position", s.ReplayPlyText());
        for (int k = 0; k <= n; k++)
        {
            Assert.Equal(positions[k], s.ReplayPosition);
            s.ReplayStep(1);
        }

        Assert.Equal(n, s.ReplayCursor);                       // clamped at the end
        s.ReplayTo(-5);
        Assert.Equal(0, s.ReplayCursor);
        s.ReplayTo(n + 5);
        Assert.Equal(n, s.ReplayCursor);
        Assert.Equal($"Ply {n} of {n}, game over", s.Status());

        s.ExitReplay();
        Assert.False(s.InReplay);
        Assert.Equal(n, s.Plies.Count);                        // the game is untouched
        Assert.Equal(positions[n], s.Position);
    }

    [Fact]
    public void While_the_viewer_is_open_the_game_stands_still()
    {
        using var s = Create(out _);
        using var hounds = new GatedAgent();
        s.SetPlayer(Side.Hounds, Agent(hounds));
        Click(s, "e1-d2");
        RunUntil(s, () => hounds.Started.IsSet, "the hounds' search");

        s.EnterReplay();                                       // cancels the running search
        Assert.True(hounds.WasCancelled);
        Assert.False(s.AiThinking);
        for (int i = 0; i < 20; i++)
        {
            s.Update();
        }

        Assert.Equal(1, hounds.Calls);                         // no new AI turn while replaying
        Assert.False(s.CanUndo);
        Assert.False(s.Undo());
        s.ReplayTo(0);
        s.ClickSquare(Square(s, "e1"));                        // board clicks do not play
        Assert.Null(s.Selected);
        Assert.Single(s.Plies);

        s.ExitReplay();
        hounds.Gate.Set();
        RunUntil(s, () => s.Plies.Count == 2, "the hounds' ply after the viewer closed");
        Assert.Equal(2, hounds.Calls);
    }

    [Fact]
    public void Colours_blunders_and_ply_classes_come_from_the_solver()
    {
        // 6 x 6 Classic: the fox wins in 25 (spec 10.7). The fox plays fastest plies until it can blunder, then does.
        using var s = Create(out _, Six);
        RunUntil(s, () => s.Table is not null, "the 6x6 table");
        var table = s.Table!;
        while (true)
        {
            Assert.False(s.IsOver, "the fox never had a losing ply");
            var options = Graded(table, s.Position);
            var blunder = options.Where(g => g.Quality == MoveQuality.Blunder).ToList();
            if (s.Position.SideToMove == Side.Fox && blunder.Count > 0)
            {
                Click(s, s.Rules.Geo.FormatMove(blunder[0].Move));
                break;
            }

            var move = s.Position.SideToMove == Side.Fox ? options.First(g => g.Quality == MoveQuality.Fastest).Move : options[0].Move;
            Click(s, s.Rules.Geo.FormatMove(move));
        }

        int n = s.Plies.Count;
        var judged = s.Judgements()!;
        Assert.Equal(MoveGrading.JudgeGame(s.Rules, table, s.StartPosition!.Value, s.Plies.Select(p => p.Move).ToList()), judged);
        Assert.Equal(Enumerable.Range(0, n).Select(i => i == n - 1), judged.Select(j => j.Blunder));

        s.EnterReplay();
        var positions = s.ToRecord().Replay(s.Rules);
        for (int k = 0; k <= n; k++)
        {
            s.ReplayTo(k);
            var expected = Graded(table, positions[k]);
            Assert.Equal(expected, s.ReplayGrades());
            Assert.Equal(expected, s.Board().Graded);
            var played = k == 0 ? null : (MoveQuality?)Graded(table, positions[k - 1]).Single(g => g.Move == s.Plies[k - 1].Move).Quality;
            Assert.Equal(played, s.ReplayPlyQuality());
        }

        s.ReplayTo(n);                                         // the blunder that led here
        Assert.Equal(MoveQuality.Blunder, s.ReplayPlyQuality());
        Assert.EndsWith(": blunder", s.ReplayPlyText(), StringComparison.Ordinal);
        Assert.StartsWith("solver: Hounds win in ", s.ReplaySolverText(), StringComparison.Ordinal);
        Assert.Contains(s.ReplayGrades(), g => g.Quality == MoveQuality.Fastest); // the hounds now have a win to keep
        s.ReplayTo(n - 1);
        Assert.Equal(MoveQuality.AlreadyLost, s.ReplayPlyQuality()); // a hound ply in a lost position
        s.ReplayTo(n - 2);
        Assert.Equal(MoveQuality.Fastest, s.ReplayPlyQuality());
        Assert.StartsWith("solver: Fox wins in ", s.ReplaySolverText(), StringComparison.Ordinal);
        Assert.Equal("played by a human", s.ReplayInfoText());
        Assert.Equal("", s.ReplayLineText());
    }

    [Fact]
    public void Without_a_table_there_are_no_colours_and_the_panel_says_why()
    {
        using var s = Create(out _, RuleSet.Classic with { BoardSize = 12 });
        Click(s, s.Rules.Geo.FormatMove(FirstMove(s)));
        s.EnterReplay();
        Assert.Null(s.Judgements());
        Assert.Empty(s.ReplayGrades());
        Assert.Null(s.ReplayPlyQuality());
        Assert.Equal("solver: none for 12 x 12", s.ReplaySolverText());
        Assert.Equal("1. " + s.Rules.Geo.FormatMove(s.Plies[0].Move), s.ReplayPlyText());
    }

    [Fact]
    public void The_stored_agent_info_of_each_ply_is_shown()
    {
        using var s = Create(out _);
        s.SetPlayer(Side.Hounds, Agent(new ScriptedAgent("b8-c7")));
        Click(s, "e1-d2");
        RunUntil(s, () => s.Plies.Count == 2, "the hounds' reply");
        s.EnterReplay();
        Assert.Equal("1. ... b8-c7", s.ReplayPlyText()[..12]);
        Assert.Equal("score +7, depth 1, 1 nodes, 0.00 s", s.ReplayInfoText());
        Assert.Equal("line b8-c7", s.ReplayLineText());
        s.ReplayStep(-1);
        Assert.StartsWith("1. e1-d2", s.ReplayPlyText(), StringComparison.Ordinal);
        Assert.Equal("played by a human", s.ReplayInfoText());

        Assert.Equal("solver: Fox wins in 13", GameSession.DescribeInfo(new AgentInfo(Scores.Win - 13, 0, 0, TimeSpan.Zero, [], AgentInfo.SourceSolver, true)));
        Assert.Equal("random ply", GameSession.DescribeInfo(AgentInfo.Random));
        Assert.Equal("score Hounds win in <= 9, depth 12, 1.9M nodes, 0.41 s",
            GameSession.DescribeInfo(new AgentInfo(-(Scores.Win - 9), 12, 1_873_311, TimeSpan.FromMilliseconds(412), [], AgentInfo.SourceSearch)));
    }

    [Fact]
    public void Play_from_here_starts_a_game_from_the_shown_position_with_the_selected_players()
    {
        using var s = Create(out _);
        Click(s, "e1-d2");
        Click(s, "b8-c7");
        Click(s, "d2-e3");
        s.EnterReplay();
        s.ReplayTo(2);
        var shown = s.ReplayPosition;
        var fox = new ScriptedAgent("d2-c3");
        s.SetPlayer(Side.Fox, Agent(fox, "scripted fox"));    // chosen while the viewer is open
        Assert.True(s.CanPlayFromHere);
        s.PlayFromHere();

        Assert.False(s.InReplay);
        Assert.Equal(shown, s.StartPosition);
        Assert.Empty(s.Plies);
        Assert.False(s.CanRedo);
        Assert.Equal("scripted fox", s.Player(Side.Fox).Label);
        Assert.True(s.IsHuman(Side.Hounds));
        RunUntil(s, () => s.Plies.Count == 1, "the selected fox's first ply");
        Assert.Equal("d2-c3", s.Rules.Geo.FormatMove(s.Plies[0].Move));
        Assert.Equal(1, fox.Calls);
        Assert.Equal(new MoveRow(1, "d2-c3", "", 0), s.MoveRows()[0]);

        using var over = PlayOut(Six);                         // the end of a finished game: nothing to play
        over.EnterReplay();
        Assert.False(over.CanPlayFromHere);
        Assert.False(over.CanAnalyse);
        over.PlayFromHere();
        Assert.True(over.InReplay);
    }

    [Fact]
    public void Analyse_searches_the_shown_position_and_keeps_the_result_with_it()
    {
        using var s = Create(out _);
        Click(s, "e1-d2");
        Click(s, "b8-c7");
        s.AnalysisBudget = SearchBudget.Depth(6);
        s.EnterReplay();
        s.ReplayTo(1);
        Assert.True(s.CanAnalyse);
        s.Analyse();
        RunUntil(s, () => !s.Analysing, "the analysis");
        var first = s.ReplayAnalysis()!;
        Assert.Equal(6, first.Info.DepthReached);
        Assert.True(s.Rules.IsLegal(s.ReplayPosition, first.Move));
        Assert.Equal(first.Move, first.Info.PrincipalVariation[0]);
        Assert.Matches(@"^analysis: [a-h]\d-[a-h]\d, [+-]?\d+$", s.AnalysisText());
        Assert.Matches(@"^depth 6, [\d.]+[kM]? nodes, 0\.0 s$", s.AnalysisDetailText());
        Assert.StartsWith("line ", s.AnalysisLineText(), StringComparison.Ordinal);

        s.ReplayStep(1);                                       // the result belongs to its position
        Assert.Null(s.ReplayAnalysis());
        Assert.Equal("", s.AnalysisText());
        s.ReplayStep(-1);
        Assert.Same(first, s.ReplayAnalysis());

        s.Analyse();                                           // a fresh search: the same result again
        RunUntil(s, () => !s.Analysing, "the second analysis");
        Assert.Equal(first.Move, s.ReplayAnalysis()!.Move);
        Assert.Equal(first.Info.ScoreFox, s.ReplayAnalysis()!.Info.ScoreFox);
        Assert.Equal(first.Info.Nodes, s.ReplayAnalysis()!.Info.Nodes);
    }

    [Fact]
    public void An_analysis_is_dropped_when_the_cursor_moves_and_kept_when_stopped()
    {
        using var s = Create(out _);
        Click(s, "e1-d2");
        s.AnalysisBudget = SearchBudget.Unlimited;              // runs until cancelled
        s.EnterReplay();
        s.Analyse();
        Assert.True(s.Analysing);
        Assert.False(s.CanAnalyse);
        RunUntil(s, () => DepthInProgress(s) >= 3, "depth 3");
        s.ReplayStep(-1);                                      // cancels and waits for it
        Assert.False(s.Analysing);
        s.ReplayStep(1);
        Assert.Null(s.ReplayAnalysis());

        s.Analyse();
        RunUntil(s, () => DepthInProgress(s) >= 4, "depth 4");
        s.StopAnalysis();
        RunUntil(s, () => !s.Analysing, "the stopped analysis");
        Assert.True(s.ReplayAnalysis()!.Info.DepthReached >= 3);
        Assert.StartsWith("analysis: ", s.AnalysisText(), StringComparison.Ordinal);

        s.Analyse();
        s.ExitReplay();                                        // closing the viewer cancels it too
        Assert.False(s.Analysing);
    }

    [Fact]
    public void Loading_or_starting_a_game_closes_the_viewer()
    {
        string dir = TempDir();
        using var s = Create(out _, dataDir: dir);
        Click(s, "e1-d2");
        string path = s.Save()!;
        s.EnterReplay();
        Assert.True(s.Load(path));
        Assert.False(s.InReplay);
        s.EnterReplay();
        s.NewGame(RuleSet.Classic);
        Assert.False(s.InReplay);
        Assert.False(s.CanReplay);                             // no plies yet
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Viewer_commands_do_nothing_while_it_is_closed_and_its_end_shows_the_result()
    {
        using var s = Create(out _);
        s.EnterReplay();                                       // no plies yet: nothing to replay
        Assert.False(s.InReplay);
        s.ReplayTo(3);
        s.ReplayStep(1);
        s.PlayFromHere();
        s.Analyse();
        s.StopAnalysis();
        s.ExitReplay();
        Assert.False(s.InReplay);
        Assert.False(s.Analysing);
        Assert.All(new[] { s.ReplayStatus(), s.ReplaySolverText(), s.ReplayPlyText(), s.ReplayInfoText(), s.ReplayLineText(),
            s.AnalysisText(), s.AnalysisDetailText(), s.AnalysisLineText() }, text => Assert.Equal("", text));
        Assert.Empty(s.ReplayGrades());
        Assert.Null(s.ReplayPlyQuality());
        Assert.Null(s.ReplayAnalysis());
        Assert.False(s.CanPlayFromHere);
        Assert.False(s.CanAnalyse);
        Assert.Throws<InvalidOperationException>(() => s.ReplayPosition);

        using var over = PlayOut(Six);
        over.EnterReplay();
        Assert.Equal(over.ResultText(), over.ReplaySolverText());
        Assert.Equal(new[] { "fastest win", "slower win", "blunder", "already lost" }, Enum.GetValues<MoveQuality>().Select(GameSession.QualityText));
    }

    /// <summary>Both sides take their first legal ply until the game ends.</summary>
    private static GameSession PlayOut(RuleSet rules)
    {
        var s = Create(out _, rules);
        while (!s.IsOver)
        {
            Click(s, s.Rules.Geo.FormatMove(FirstMove(s)));
        }

        return s;
    }

    private static GradedMove[] Graded(SolverTable table, in GameState position)
    {
        var buffer = new GradedMove[Rules.MaxMoves];
        return buffer[..MoveGrading.Grade(table, position, buffer)];
    }

    /// <summary>The depth the running analysis is on, read from its progress text (0 when none runs).</summary>
    private static int DepthInProgress(GameSession s)
    {
        var match = System.Text.RegularExpressions.Regex.Match(s.AnalysisText(), @"^analysing - depth (\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static Move FirstMove(GameSession s)
    {
        var legal = new Move[Rules.MaxMoves];
        Assert.True(s.Rules.GenerateMoves(s.Position, legal) > 0);
        return legal[0];
    }
}
