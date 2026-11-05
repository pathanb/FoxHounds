using System.Globalization;
using System.Text.Json.Nodes;
using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.Sim;

/// <summary>Per-game metrics against the solver (spec 11.5): blunders, plies in won positions, grading.</summary>
internal static class GameMetrics
{
    /// <summary>The games.jsonl line of a finished game (spec 14.4).</summary>
    public static GameLineJson Line(Rules rules, SolverTable? table, ExperimentRunner.WorkItem item, LabelledPosition opening,
        ExperimentAgent fox, ExperimentAgent hounds, GameRecord record, long ms)
    {
        var label = opening.Value ?? table?.Get(opening.Position);
        long[] blunders = new long[2];
        long[] won = new long[2];
        long[] nodes = new long[2];
        long[] moves = new long[2];
        var s = record.Start;
        foreach (var ply in record.Plies)
        {
            var mover = s.SideToMove;
            nodes[(int)mover] += ply.Info?.Nodes ?? 0;
            moves[(int)mover]++;
            if (table is not null && MoveGrading.Judge(rules, table, s, ply.Move) is { MoverWasWinning: true } verdict)
            {
                won[(int)mover]++;
                blunders[(int)mover] += verdict.Blunder ? 1 : 0;
            }

            s = rules.Apply(s, ply.Move);
        }

        return new GameLineJson
        {
            Game = item.Index,
            Opening = item.Position,
            Fox = fox.Spec.Name,
            Hounds = hounds.Spec.Name,
            FoxRole = fox.Role,
            HoundsRole = hounds.Role,
            Result = JsonNames.Of(record.Outcome.Result),
            Reason = JsonNames.Of(record.Outcome.Reason),
            Plies = record.Plies.Count,
            OpeningLabel = label?.Label(opening.Position.SideToMove),
            Blunders = table is null ? null : new SidesJson { Fox = blunders[0], Hounds = blunders[1] },
            WonPlies = table is null ? null : new SidesJson { Fox = won[0], Hounds = won[1] },
            Moves = new SidesJson { Fox = moves[0], Hounds = moves[1] },
            Nodes = nodes[0] + nodes[1] == 0 ? null : new SidesJson { Fox = nodes[0] == 0 ? null : nodes[0], Hounds = nodes[1] == 0 ? null : nodes[1] },
            Forfeit = record.Forfeit is { } f ? $"{JsonNames.Of(f.Side)}: {JsonNames.Of(f.Cause)}" : null,
            Ms = ms,
        };
    }

    /// <summary>The line of a graded suite position (spec 11.1, 11.5).</summary>
    public static GradeLineJson Grade(Rules rules, SolverTable table, ExperimentRunner.WorkItem item, LabelledPosition p,
        ExperimentAgent agent, AgentDecision decision, long ms)
    {
        var side = p.Position.SideToMove;
        var value = table.Get(p.Position);
        if (p.Value is { } label && label != value)
        {
            throw new ExperimentException($"Suite position {item.Position} is labelled {label.Label(side)}, but the solver says {value.Label(side)}: the suite was made for other rules.");
        }

        Span<RatedMove> rated = stackalloc RatedMove[Rules.MaxMoves];
        int count = table.RateMoves(p.Position, rated);
        int chosen = -1;
        int longest = 0;
        for (int i = 0; i < count; i++)
        {
            if (rated[i].Move == decision.Move)
            {
                chosen = i;
            }

            if (!rated[i].ValueForMover.SideToMoveWins)
            {
                longest = Math.Max(longest, rated[i].ValueForMover.Plies);
            }
        }

        if (chosen < 0)
        {
            throw new ExperimentException($"Suite position {item.Position}: {agent.Role} returned a ply that is not legal.");
        }

        var after = rated[chosen].ValueForMover;
        return new GradeLineJson
        {
            Position = item.Position,
            Agent = agent.Spec.Name,
            Side = JsonNames.Of(side),
            Label = value.Label(side),
            Move = rules.Geo.FormatMove(decision.Move),
            Kept = value.SideToMoveWins ? after.SideToMoveWins : null,
            Longest = value.SideToMoveWins ? null : after.Plies == longest,
            Nodes = decision.Info?.Nodes ?? 0,
            Ms = ms,
        };
    }
}

/// <summary>summary.json (spec 11.4, 11.5): every metric with its interval, per agent and per role. No wall-clock
/// values (D-20), so runs with different worker counts give identical files.</summary>
internal static class Summary
{
    private static readonly string[] SideNames = ["fox", "hounds"];

    public static JsonObject Build(Experiment e, IReadOnlyList<GameLineJson> games, IReadOnlyList<GradeLineJson> grades,
        IReadOnlyList<ExperimentRunner.WorkItem> planned, bool stopped)
    {
        var plannedIndices = planned.Select(w => w.Index).ToHashSet();
        var lines = games.Where(g => plannedIndices.Contains(g.Game)).OrderBy(g => g.Game).ToList();
        var graded = grades.Where(g => plannedIndices.Contains(g.Position)).OrderBy(g => g.Position).ToList();
        int done = e.Mode == ExperimentMode.Grade ? graded.Count : lines.Count;
        var summary = new JsonObject
        {
            ["format"] = "summary/1",
            ["id"] = e.Id,
            ["mode"] = JsonNames.Of(e.Mode),
            ["sha256"] = e.Sha256,
            ["planned"] = planned.Count,
            ["done"] = done,
            ["complete"] = done == planned.Count || stopped,
        };

        if (e.Mode == ExperimentMode.Grade)
        {
            summary["grade"] = GradeSummary(graded);
            return summary;
        }

        var agents = new JsonObject();
        foreach (var agent in e.Agents)
        {
            var node = new JsonObject { ["name"] = agent.Spec.Name };
            for (int side = 0; side < 2; side++)
            {
                var stats = SideStats.Of(lines, agent.Role, (Side)side);
                if (stats.Games > 0)
                {
                    node[SideNames[side]] = stats.ToJson();
                }
            }

            agents[agent.Role] = node;
        }

        summary["agents"] = agents;
        if (e.Mode == ExperimentMode.VsReference)
        {
            var labelled = lines.Where(l => l.OpeningLabel is not null).ToList();
            summary["openings"] = new JsonObject
            {
                ["foxWon"] = labelled.Count(l => l.OpeningLabel![0] == 'F'),
                ["houndsWon"] = labelled.Count(l => l.OpeningLabel![0] == 'H'),
            };
        }

        if (e.Mode is ExperimentMode.HeadToHead or ExperimentMode.Gauntlet)
        {
            var matches = new JsonArray();
            foreach (var opponent in e.Agents.Skip(1))
            {
                var pairs = Pairs(lines, e.Agents[0].Role, opponent.Role);
                var h = Stats.HeadToHead(pairs.ToArray());
                var match = new JsonObject
                {
                    ["a"] = e.Agents[0].Role,
                    ["b"] = opponent.Role,
                    ["pairs"] = h.Pairs,
                    ["score"] = Number(h.Score),
                    ["scoreLow"] = Number(h.ScoreInterval.Low),
                    ["scoreHigh"] = Number(h.ScoreInterval.High),
                    ["elo"] = Number(h.Elo),
                    ["eloLow"] = Number(h.EloInterval.Low),
                    ["eloHigh"] = Number(h.EloInterval.High),
                };
                if (e.Sprt is { } sprt)
                {
                    var state = Stats.Sprt(pairs.ToArray(), sprt);
                    match["sprt"] = new JsonObject
                    {
                        ["llr"] = Number(state.Llr),
                        ["lower"] = Number(state.Lower),
                        ["upper"] = Number(state.Upper),
                        ["decision"] = JsonNames.Of(state.Decision),
                    };
                }

                matches.Add(match);
            }

            summary["headToHead"] = matches;
        }

        return summary;
    }

    /// <summary>The headline of a progress line (spec 11.3).</summary>
    public static string Headline(Experiment e, IReadOnlyList<GameLineJson> games, IReadOnlyList<GradeLineJson> grades)
    {
        switch (e.Mode)
        {
            case ExperimentMode.Grade:
                var won = grades.Where(g => g.Kept is not null).ToList();
                return "accuracy " + Text(Proportion.Of(won.Count(g => g.Kept == true), won.Count));
            case ExperimentMode.VsReference:
                var stats = SideStats.Of(games, e.Agents[0].Role, e.CandidateSide);
                return $"{e.Agents[0].Role} conversion {Text(Proportion.Of(stats.Conversions, stats.ConversionTrials))}";
            default:
                var pairs = e.Agents.Skip(1).SelectMany(o => Pairs(games, e.Agents[0].Role, o.Role)).ToArray();
                var h = Stats.HeadToHead(pairs);
                return string.Create(CultureInfo.InvariantCulture,
                    $"{e.Agents[0].Role} score {h.Score:0.000} [{h.ScoreInterval.Low:0.000}, {h.ScoreInterval.High:0.000}], Elo {Elo(h.Elo)} [{Elo(h.EloInterval.Low)}, {Elo(h.EloInterval.High)}] over {h.Pairs} pairs");
        }
    }

    /// <summary>A's points over a pair of games, one in each role: 0, ½ or 1.</summary>
    public static double PairScore(GameLineJson first, GameLineJson second, string roleA) =>
        (Points(first, roleA) + Points(second, roleA)) / 2;

    internal static string Text(Proportion p) => string.Create(CultureInfo.InvariantCulture,
        $"{p.Rate * 100:0.0#}% [{p.Interval.Low * 100:0.0#}, {p.Interval.High * 100:0.0#}] ({p.Successes}/{p.Trials})");

    internal static string Elo(double elo) => double.IsFinite(elo)
        ? elo.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)
        : elo > 0 ? "+inf" : "-inf";

    internal static JsonNode Number(double value) =>
        double.IsFinite(value) ? JsonValue.Create(Math.Round(value, 6)) : JsonValue.Create(value > 0 ? "inf" : "-inf");

    internal static JsonObject ToJson(Proportion p) => new()
    {
        ["successes"] = p.Successes,
        ["trials"] = p.Trials,
        ["rate"] = Number(p.Rate),
        ["low"] = Number(p.Interval.Low),
        ["high"] = Number(p.Interval.High),
    };

    private static double Points(GameLineJson game, string role)
    {
        var winner = game.Result == "foxWins" ? game.FoxRole : game.HoundsRole;
        return winner == role ? 1 : 0;
    }

    /// <summary>Pair scores of <paramref name="a"/> against <paramref name="b"/>: consecutive games with the sides swapped.</summary>
    private static List<double> Pairs(IReadOnlyList<GameLineJson> games, string a, string b)
    {
        var byIndex = games.ToDictionary(g => g.Game);
        var scores = new List<double>();
        foreach (var g in games.OrderBy(g => g.Game))
        {
            if (g.Game % 2 == 0 && g.FoxRole == a && g.HoundsRole == b && byIndex.TryGetValue(g.Game + 1, out var second))
            {
                scores.Add(PairScore(g, second, a));
            }
        }

        return scores;
    }

    private static JsonObject GradeSummary(List<GradeLineJson> graded)
    {
        var node = new JsonObject();
        foreach (string side in SideNames.Append("all"))
        {
            var lines = side == "all" ? graded : graded.Where(g => g.Side == side).ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            var won = lines.Where(g => g.Kept is not null).ToList();
            var lost = lines.Where(g => g.Longest is not null).ToList();
            node[side] = new JsonObject
            {
                ["positions"] = lines.Count,
                ["accuracy"] = ToJson(Proportion.Of(won.Count(g => g.Kept == true), won.Count)),
                ["resistance"] = ToJson(Proportion.Of(lost.Count(g => g.Longest == true), lost.Count)),
                ["nodes"] = lines.Sum(g => g.Nodes),
                ["nodesPerPosition"] = Number((double)lines.Sum(g => g.Nodes) / lines.Count),
            };
        }

        return node;
    }

    /// <summary>One agent's results on one side.</summary>
    private sealed record SideStats(long Games, long Wins, long ConversionTrials, long Conversions, long? Blunders,
        long? WonPlies, long Moves, long Nodes, long Forfeits)
    {
        public static SideStats Of(IEnumerable<GameLineJson> games, string role, Side side)
        {
            long n = 0, wins = 0, trials = 0, conversions = 0, blunders = 0, wonPlies = 0, moves = 0, nodes = 0, forfeits = 0;
            bool labelled = false;
            string sideName = side == Side.Fox ? "fox" : "hounds";
            foreach (var g in games)
            {
                if ((side == Side.Fox ? g.FoxRole : g.HoundsRole) != role)
                {
                    continue;
                }

                n++;
                bool won = (g.Result == "foxWins") == (side == Side.Fox);
                wins += won ? 1 : 0;
                if (g.OpeningLabel is { Length: > 0 } label && label[0] == (side == Side.Fox ? 'F' : 'H'))
                {
                    trials++;
                    conversions += won ? 1 : 0;
                }

                if (g.Blunders is { } b && g.WonPlies is { } w)
                {
                    labelled = true;
                    blunders += (side == Side.Fox ? b.Fox : b.Hounds) ?? 0;
                    wonPlies += (side == Side.Fox ? w.Fox : w.Hounds) ?? 0;
                }

                moves += (side == Side.Fox ? g.Moves?.Fox : g.Moves?.Hounds) ?? 0;
                nodes += (side == Side.Fox ? g.Nodes?.Fox : g.Nodes?.Hounds) ?? 0;
                forfeits += g.Forfeit?.StartsWith(sideName, StringComparison.Ordinal) == true ? 1 : 0;
            }

            return new SideStats(n, wins, trials, conversions, labelled ? blunders : null, labelled ? wonPlies : null, moves, nodes, forfeits);
        }

        public JsonObject ToJson()
        {
            var node = new JsonObject
            {
                ["games"] = Games,
                ["wins"] = Summary.ToJson(Proportion.Of(Wins, Games)),
            };
            if (ConversionTrials > 0)
            {
                node["conversion"] = Summary.ToJson(Proportion.Of(Conversions, ConversionTrials));
            }

            if (Blunders is long b && WonPlies is long w)
            {
                node["blunders"] = Summary.ToJson(Proportion.Of(b, w));
            }

            node["moves"] = Moves;
            node["nodes"] = Nodes;
            node["nodesPerMove"] = Number(Moves == 0 ? 0 : (double)Nodes / Moves);
            node["forfeits"] = Forfeits;
            return node;
        }
    }
}
