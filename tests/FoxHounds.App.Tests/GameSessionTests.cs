using FoxHounds.Agents;
using FoxHounds.Core;
using static FoxHounds.App.Tests.Sessions;

namespace FoxHounds.App.Tests;

/// <summary>T-APP-01: the app's game logic (spec 13.3, 13.4) without a window.</summary>
public sealed class GameSessionTests
{
    private static readonly RuleSet Choose = RuleSet.Classic with { FoxStart = FoxStart.ChooseOnBackRow };

    [Fact]
    public void Human_plies_are_validated_and_selection_follows_the_click_rules()
    {
        using var s = Create(out _);
        s.ClickSquare(Square(s, "b8"));                       // a hound while the fox is to move
        Assert.Null(s.Selected);
        s.ClickSquare(Square(s, "e1"));
        Assert.Equal(Square(s, "e1"), (int?)s.Selected);
        Assert.Equal(new[] { Square(s, "d2"), Square(s, "f2") }.Order(), s.Targets().Order());
        s.ClickSquare(Square(s, "e3"));                       // not a target: nothing is played
        Assert.Empty(s.Plies);
        Assert.Null(s.Selected);

        Click(s, "e1-f2");
        Assert.Single(s.Plies);
        Assert.Equal(Side.Hounds, s.Position.SideToMove);

        s.ClickSquare(Square(s, "c7"));                       // b8 and d8 can both go there: ambiguous
        Assert.Single(s.Plies);
        s.ClickSquare(Square(s, "a7"));                       // only b8 can: played directly
        Assert.Equal("b8-a7", s.Rules.Geo.FormatMove(s.Plies[^1].Move));

        s.ClickSquare(Square(s, "f2"));
        Assert.NotNull(s.Selected);
        s.ClearSelection();                                   // Esc
        Assert.Null(s.Selected);
        Assert.Empty(s.Targets());
    }

    [Fact]
    public void A_human_fox_places_itself_on_a_highlighted_back_row_square()
    {
        using var s = Create(out _, Choose);
        Assert.True(s.AwaitingFoxPlacement);
        Assert.Equal("Fox: choose a start square", s.Status());
        Assert.Equal(s.Rules.FoxStartSquares.Select(b => (int)b).Order(), s.Targets().Order());

        s.ClickSquare(Square(s, "d2"));                       // not on the back row
        Assert.True(s.AwaitingFoxPlacement);
        s.ClickSquare(Square(s, "c1"));
        Assert.False(s.AwaitingFoxPlacement);
        Assert.Equal(Square(s, "c1"), s.Position.Fox);
        Assert.Empty(s.Plies);
        Assert.Equal("c1", s.Rules.Geo.SquareName(s.ToRecord().Start.Fox));
    }

    [Fact]
    public void An_AI_fox_places_itself_and_a_square_off_the_back_row_forfeits()
    {
        using var s = Create(out _, Choose);
        s.SetPlayer(Side.Fox, Agent(new ScriptedAgent { FoxStart = (byte)Square(s, "g1") }));
        RunUntil(s, () => !s.AwaitingFoxPlacement, "the fox's placement");
        Assert.Equal(Square(s, "g1"), s.Position.Fox);

        using var bad = Create(out _, Choose);
        bad.SetPlayer(Side.Fox, Agent(new BrokenAgent(illegal: true)));
        RunUntil(bad, () => bad.IsOver, "the forfeit");
        Assert.Equal(GameResult.HoundsWin, bad.Outcome.Result);
        Assert.Equal(ForfeitCause.InvalidFoxStart, bad.Forfeit!.Cause);
        Assert.StartsWith("Hounds win: the fox forfeited", bad.ResultText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_goes_back_to_the_last_position_with_a_human_to_move_and_redo_replays_until_a_new_ply()
    {
        using var s = Create(out _);
        var hounds = new ScriptedAgent("b8-c7", "d8-e7", "f8-g7");
        s.SetPlayer(Side.Hounds, Agent(hounds));
        Click(s, "e1-d2");
        RunUntil(s, () => s.Plies.Count == 2, "the hounds' reply");
        Click(s, "d2-e3");
        RunUntil(s, () => s.Plies.Count == 4, "the second reply");

        Assert.True(s.Undo());                                // back over the AI's ply to the fox's turn
        Assert.Equal(2, s.Plies.Count);
        Assert.Equal(Side.Fox, s.Position.SideToMove);
        Assert.True(s.Undo());
        Assert.Empty(s.Plies);
        Assert.Equal(s.Rules.Initial(), s.Position);

        Assert.True(s.Redo());                                // the fox's ply and the AI's reply
        Assert.Equal(2, s.Plies.Count);
        Assert.Equal(Side.Fox, s.Position.SideToMove);
        Assert.True(s.CanRedo);

        Click(s, "d2-c3");                                    // a new ply ends redo
        Assert.False(s.CanRedo);
        Assert.False(s.Redo());
    }

    [Fact]
    public void Undo_does_nothing_when_no_earlier_position_has_a_human_to_move()
    {
        using var s = Create(out _);
        s.SetPlayer(Side.Fox, Agent(new ScriptedAgent("e1-d2")));
        RunUntil(s, () => s.Plies.Count == 1, "the fox's first ply");
        Assert.False(s.CanUndo);
        Assert.False(s.Undo());
        Assert.Single(s.Plies);
    }

    [Fact]
    public void Without_human_players_undo_pauses_and_steps_back_one_ply_and_redo_steps_forward_one()
    {
        using var s = Create(out var clock);
        s.SetPlayer(Side.Fox, Agent(new ScriptedAgent()));
        s.SetPlayer(Side.Hounds, Agent(new ScriptedAgent()));
        s.Delay = TimeSpan.FromSeconds(1); // with the manual clock, a ply happens only when the test advances it
        for (int i = 1; i <= 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            RunUntil(s, () => s.Plies.Count == i, $"AI ply {i}");
        }

        Assert.False(s.AiThinking);
        int count = s.Plies.Count;

        Assert.True(s.Undo());
        Assert.True(s.Paused);
        Assert.Equal(count - 1, s.Plies.Count);
        Assert.True(s.Redo());
        Assert.Equal(count, s.Plies.Count);

        clock.Advance(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 20; i++)
        {
            s.Update();
        }

        Assert.Equal(count, s.Plies.Count);                    // still paused
        Assert.Equal("paused", s.Status());
    }

    [Fact]
    public void Pause_stops_after_the_current_ply_and_the_delay_spaces_AI_plies()
    {
        using var s = Create(out var clock);
        using var fox = new GatedAgent();
        s.SetPlayer(Side.Fox, Agent(fox));
        s.SetPlayer(Side.Hounds, Agent(new ScriptedAgent()));
        s.Delay = TimeSpan.FromMilliseconds(500);

        s.Update();
        Assert.False(s.AiThinking);                            // the delay runs from the start too
        clock.Advance(TimeSpan.FromMilliseconds(500));
        s.Update();
        Assert.True(fox.Started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        s.TogglePause();                                       // while the fox's agent is thinking
        fox.Gate.Set();
        RunUntil(s, () => s.Plies.Count == 1, "the ply in progress");
        clock.Advance(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 20; i++)
        {
            s.Update();
        }

        Assert.Single(s.Plies);                                // no new ply while paused

        s.TogglePause();
        s.Update();
        Assert.False(s.AiThinking);                            // resuming waits for the delay again
        clock.Advance(TimeSpan.FromMilliseconds(499));
        s.Update();
        Assert.False(s.AiThinking);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        RunUntil(s, () => s.Plies.Count == 2, "the hounds' ply after the delay");
    }

    [Fact]
    public void Undo_during_an_AI_search_cancels_it_within_100_ms_and_restores_the_position()
    {
        // Spec 16.1 item 3, with the real search: a node budget far beyond what can finish meanwhile.
        using var s = Create(out _);
        var slow = AgentSpec.Parse("""
            { "format": "agent/1", "type": "minimax", "name": "slow", "evaluator": { "type": "linear" },
              "search": { "ttMegabytes": 4 }, "budget": { "maxNodes": 4000000000 } }
            """);
        s.SetPlayer(Side.Hounds, new PlayerOption("slow", slow));
        Click(s, "e1-d2");
        RunUntil(s, () => s.Status().StartsWith("thinking - depth", StringComparison.Ordinal), "the search to start");

        var before = SystemClock.Instance.Elapsed;
        Assert.True(s.Undo());
        var took = SystemClock.Instance.Elapsed - before;
        Assert.True(took < TimeSpan.FromMilliseconds(100), $"undo took {took.TotalMilliseconds:F0} ms");
        Assert.False(s.AiThinking);
        Assert.Empty(s.Plies);
        Assert.Equal(s.Rules.Initial(), s.Position);
        Assert.Equal("Fox to move", s.Status());
    }

    [Fact]
    public void Undo_cancels_a_gated_agent_and_its_late_answer_is_discarded()
    {
        using var s = Create(out _);
        using var hounds = new GatedAgent();
        s.SetPlayer(Side.Hounds, Agent(hounds));
        Click(s, "e1-d2");
        s.Update();
        Assert.True(hounds.Started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(s.Undo());
        Assert.True(hounds.WasCancelled);
        Assert.Empty(s.Plies);
        for (int i = 0; i < 10; i++)
        {
            s.Update();                                        // the fox (human) is to move: nothing starts
        }

        Assert.False(s.AiThinking);
        Assert.Empty(s.Plies);
    }

    [Fact]
    public void A_new_player_takes_over_at_that_sides_next_turn()
    {
        using var s = Create(out _);
        using var first = new GatedAgent();
        var second = new ScriptedAgent();
        s.SetPlayer(Side.Hounds, Agent(first, "first"));
        Click(s, "e1-d2");
        s.Update();
        Assert.True(first.Started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        s.SetPlayer(Side.Hounds, Agent(second, "second"));      // mid-search: the running search still plays
        Assert.True(s.AiThinking);
        first.Gate.Set();
        RunUntil(s, () => s.Plies.Count == 2, "the first agent's ply");
        Assert.Equal(0, second.Calls);

        Click(s, "d2-e3");
        RunUntil(s, () => s.Plies.Count == 4, "the second agent's ply");
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public void An_agent_that_throws_stops_and_is_not_asked_again_until_the_user_acts()
    {
        using var s = Create(out _);
        var broken = new PlayerOption("broken", Create: () => new BrokenAgent(illegal: false));
        s.SetPlayer(Side.Hounds, broken);
        Click(s, "e1-d2");
        RunUntil(s, () => s.Message is not null, "the error");
        Assert.Equal("AI error: broken on purpose", s.Status());
        for (int i = 0; i < 10; i++)
        {
            s.Update();
            Assert.False(s.AiThinking);
        }

        s.SetPlayer(Side.Hounds, Agent(new ScriptedAgent()));
        RunUntil(s, () => s.Plies.Count == 2, "the replacement's ply");
    }

    [Fact]
    public void An_illegal_AI_ply_forfeits_the_game()
    {
        using var s = Create(out _);
        s.SetPlayer(Side.Hounds, Agent(new BrokenAgent(illegal: true)));
        Click(s, "e1-d2");
        RunUntil(s, () => s.IsOver, "the forfeit");
        Assert.Equal(GameResult.FoxWins, s.Outcome.Result);
        Assert.Equal(EndReason.Forfeit, s.Outcome.Reason);
        Assert.Equal(ForfeitCause.IllegalMove, s.Forfeit!.Cause);
        Assert.StartsWith("Fox wins: the hounds forfeited", s.ResultText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_and_load_give_the_same_position_move_list_and_players()
    {
        // Spec 16.1 item 6.
        string data = TempDir();
        using var s = Create(out var clock, dataDir: data);
        var easy = AgentSpec.Parse("""
            { "format": "agent/1", "type": "minimax", "name": "easy", "evaluator": { "type": "linear" },
              "search": { "ttMegabytes": 1 }, "budget": { "maxDepth": 2 }, "epsilon": 0.25 }
            """);
        s.SetPlayer(Side.Hounds, new PlayerOption("Easy", easy));
        Click(s, "e1-d2");
        RunUntil(s, () => s.Plies.Count == 2, "the hounds' reply");
        Click(s, "d2-e3");
        RunUntil(s, () => s.Plies.Count == 4, "the second reply");
        string path = s.Save()!;
        Assert.Equal("2026-11-05T12-00-00Z.fhg", Path.GetFileName(path));
        Assert.Equal(new[] { path }, s.SavedGames());

        using var again = Create(out _, dataDir: data);
        var options = new[] { PlayerOption.Human, new PlayerOption("Easy", easy) };
        again.ResolvePlayer = spec => options.FirstOrDefault(o => o.Spec?.ToJson() == spec.ToJson());
        Assert.True(again.Load(path));
        Assert.Equal(s.Position, again.Position);
        Assert.Equal(s.MoveRows(), again.MoveRows());
        Assert.Equal(s.Plies.Select(p => Describe(s, p)), again.Plies.Select(p => Describe(again, p)));
        Assert.True(again.IsHuman(Side.Fox));
        Assert.Same(options[1], again.Player(Side.Hounds));
        Assert.Equal(Sessions.Start, again.StartedUtc);
        Assert.StartsWith("loaded ", again.Status(), StringComparison.Ordinal);
        Assert.False(again.Paused);
    }

    [Fact]
    public void A_record_whose_stored_result_is_wrong_fails_to_load_and_the_game_is_kept()
    {
        // T-APP-01, last item.
        string data = TempDir();
        using var s = Create(out _, RuleSet.Classic with { BoardSize = 4 }, data);
        s.SetPlayer(Side.Fox, Agent(new ScriptedAgent()));
        s.SetPlayer(Side.Hounds, Agent(new ScriptedAgent()));
        RunUntil(s, () => s.IsOver, "the game to end");
        string path = s.Save()!;
        string json = File.ReadAllText(path);
        string winner = s.Outcome.Result == GameResult.FoxWins ? "foxWins" : "houndsWin";
        string other = winner == "foxWins" ? "houndsWin" : "foxWins";
        Assert.Contains($"\"{winner}\"", json, StringComparison.Ordinal);
        File.WriteAllText(path, json.Replace($"\"{winner}\"", $"\"{other}\"", StringComparison.Ordinal));

        using var t = Create(out _, dataDir: data);
        Click(t, "e1-d2");
        Assert.False(t.Load(path));
        Assert.StartsWith("cannot load", t.Status(), StringComparison.Ordinal);
        Assert.Single(t.Plies);                                   // the current game is untouched
        Assert.Equal(8, t.Rules.Geo.N);
    }

    [Fact]
    public void The_move_list_pairs_the_fox_and_hounds_plies()
    {
        using var s = Create(out _);
        Click(s, "e1-d2");
        Click(s, "b8-c7");
        Click(s, "d2-e3");
        Assert.Equal(new[] { new MoveRow(1, "e1-d2", "b8-c7"), new MoveRow(2, "d2-e3", "") }, s.MoveRows());

        s.NewGameFrom(s.Rules.FromNotation("8/e3/c7,d8,f8,h8/H"));
        Click(s, "c7-b6");
        Click(s, "e3-d4");
        Assert.Equal(new[] { new MoveRow(1, "...", "c7-b6"), new MoveRow(2, "e3-d4", "") }, s.MoveRows());
    }

    [Fact]
    public void Status_and_evaluation_lines_follow_the_game()
    {
        using var s = Create(out var clock, RuleSet.Classic with { BoardSize = 6 });
        Assert.Equal("Fox to move", s.Status());
        RunUntil(s, () => s.Table is not null, "the 6x6 table");
        Assert.Matches(@"^AI: -   solver: (Fox wins|Hounds win) in \d+$", s.Evaluation());

        s.SetPlayer(Side.Hounds, Agent(new ScriptedAgent()));
        Click(s, s.Rules.Geo.FormatMove(FirstMove(s)));
        RunUntil(s, () => s.Plies.Count == 2, "the reply");
        Assert.StartsWith("AI: +7", s.Evaluation(), StringComparison.Ordinal);

        s.Notify("hello");
        Assert.Equal("hello", s.Status());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("Fox to move", s.Status());               // notices expire
    }

    [Fact]
    public void Perfect_is_offered_where_the_solver_supports_the_board()
    {
        using var s = Create(out _);
        foreach (int n in new[] { 4, 8, 9, 11 })
        {
            s.NewGame(RuleSet.Classic with { BoardSize = n });
            Assert.True(s.SolverOffered, $"n = {n}");
        }

        s.NewGame(RuleSet.Classic with { BoardSize = 12 });
        Assert.False(s.SolverOffered);
        Assert.Null(s.Table);
    }

    [Theory]
    [InlineData(EndReason.FoxReachedHome, false, "Fox wins: the fox reached the hounds' home row")]
    [InlineData(EndReason.FoxTrapped, false, "Hounds win: the fox is trapped")]
    [InlineData(EndReason.FoxEscaped, false, "Fox wins: the fox has escaped")]
    [InlineData(EndReason.HoundsImmobile, true, "Fox wins: the hounds cannot move")]
    public void Each_end_reason_is_reported_with_the_winner(EndReason reason, bool gilith, string text)
    {
        // Spec 16.1 item 1, through the session: a position one human ply from the end, found by seeded play.
        var config = gilith ? RuleSet.Gilith : RuleSet.Classic;
        using var s = Create(out _, config);
        var (position, move) = FindEnding(s.Rules, reason);
        s.NewGameFrom(position);
        Click(s, s.Rules.Geo.FormatMove(move));
        Assert.True(s.IsOver);
        Assert.Equal(reason, s.Outcome.Reason);
        Assert.Equal(text, s.ResultText());
        Assert.Equal(text, s.Status());
        if (reason == EndReason.FoxTrapped)
        {
            var steps = s.Rules.Geo.FoxStepList(s.Position.Fox).ToArray().Select(b => (int)b).Order();
            Assert.Equal(steps, s.TrappingSquares().Order());
        }
        else
        {
            Assert.Empty(s.TrappingSquares());
        }
    }

    [Fact]
    public void Every_change_to_the_game_changes_the_version()
    {
        using var s = Create(out _);
        int v0 = s.Version;
        Click(s, "e1-d2");
        int v1 = s.Version;
        Assert.NotEqual(v0, v1);
        s.Undo();
        Assert.NotEqual(v1, s.Version);
        int v2 = s.Version;
        s.Redo();
        Assert.NotEqual(v2, s.Version);
        int v3 = s.Version;
        s.NewGame(RuleSet.Classic);
        Assert.NotEqual(v3, s.Version);
    }

    private static string Describe(GameSession s, PlyRecord ply) => ply.Info is not { } info
        ? s.Rules.Geo.FormatMove(ply.Move)
        : $"{s.Rules.Geo.FormatMove(ply.Move)} {info.ScoreFox} {info.DepthReached} {info.Nodes} {info.Exact} {info.Source} "
          + string.Join(" ", info.PrincipalVariation.Select(s.Rules.Geo.FormatMove));

    private static Move FirstMove(GameSession s)
    {
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        s.Rules.GenerateMoves(s.Position, moves);
        return moves[0];
    }

    private static (GameState Position, Move Move) FindEnding(Rules rules, EndReason reason)
    {
        var rng = new Rng(0xE7D + (ulong)reason);
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        for (int game = 0; game < 10_000; game++)
        {
            var s = rules.Initial();
            while (!rules.Evaluate(s).IsOver)
            {
                int count = rules.GenerateMoves(s, moves);
                for (int i = 0; i < count; i++)
                {
                    if (rules.Evaluate(rules.Apply(s, moves[i])).Reason == reason)
                    {
                        return (s, moves[i]);
                    }
                }

                s = rules.Apply(s, moves[rng.NextInt(count)]);
            }
        }

        throw new InvalidOperationException($"no game ended by {reason}");
    }
}
