using System.Text.Json.Nodes;
using FoxHounds.Core;

namespace FoxHounds.Core.Tests;

/// <summary>Game record files, format fhg/1 (spec 14.2): round trip, and verification of the stored result.</summary>
public sealed class GameRecordFileTests
{
    private static readonly GameOptions Options = new()
    {
        FoxBudget = SearchBudget.Unlimited,
        HoundsBudget = SearchBudget.Unlimited,
        Clock = new ManualClock(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)),
    };

    private static GameRecord Played(RuleSet ruleSet, ulong seed) =>
        GameRunner.Play(new Rules(ruleSet), new UniformAgent(), new UniformAgent(), Options, new Rng(seed), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(8, false, 1UL)]
    [InlineData(6, true, 2UL)]
    [InlineData(10, false, 3UL)]
    public void A_record_survives_a_round_trip_through_its_file(int n, bool choose, ulong seed)
    {
        var ruleSet = RuleSet.Classic with { BoardSize = n, FoxStart = choose ? FoxStart.ChooseOnBackRow : FoxStart.Fixed };
        var record = Played(ruleSet, seed);
        string path = Path.Combine(Path.GetTempPath(), $"fh-record-{Guid.NewGuid():N}", "game.fhg");
        GameRecordFile.Write(record, path);
        var again = GameRecordFile.Read(path);

        Assert.Equal(record.RuleSet, again.RuleSet);
        Assert.Equal(record.Start, again.Start);
        Assert.Equal(record.Fox, again.Fox);
        Assert.Equal(record.Hounds, again.Hounds);
        Assert.Equal(record.Seed, again.Seed);
        Assert.Equal(record.StartedUtc, again.StartedUtc);
        Assert.Equal(record.Outcome, again.Outcome);
        Assert.Equal(record.Plies.Select(p => p.Move), again.Plies.Select(p => p.Move));
        Assert.Equal(GameRecordFile.ToJson(record), GameRecordFile.ToJson(again));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Agent_info_is_stored_with_its_line_and_exactness()
    {
        var rules = new Rules(RuleSet.Classic);
        Assert.True(rules.Geo.TryParseMove("e1-d2", out var first));
        Assert.True(rules.Geo.TryParseMove("b8-c7", out var reply));
        var record = new GameRecord
        {
            RuleSet = RuleSet.Classic,
            Start = rules.Initial(),
            Fox = new PlayerInfo(PlayerInfo.AgentKind, "hard", """{"type":"minimax","name":"hard"}"""),
            Hounds = PlayerInfo.Human,
            Plies =
            [
                new PlyRecord(first, new AgentInfo(-(Scores.Win - 44), 12, 345_678, TimeSpan.FromMilliseconds(250), [first, reply], AgentInfo.SourceSearch, Exact: true)),
                new PlyRecord(reply, null),
            ],
            Outcome = Outcome.Ongoing,
        };

        string json = GameRecordFile.ToJson(record);
        var ply = JsonNode.Parse(json)!["plies"]![0]!;
        Assert.Equal("e1-d2", (string?)ply["move"]);
        Assert.True((bool?)ply["info"]!["exact"]);
        Assert.Equal("e1-d2 b8-c7", string.Join(" ", ply["info"]!["pv"]!.AsArray().Select(m => (string?)m)));
        Assert.Null(JsonNode.Parse(GameRecordFile.ToJson(record with { Plies = [new PlyRecord(first, record.Plies[0].Info! with { Exact = false })] }))!["plies"]![0]!["info"]!["exact"]);

        var again = GameRecordFile.Parse(json);
        var info = again.Plies[0].Info!;
        Assert.Equal(-(Scores.Win - 44), info.ScoreFox);
        Assert.Equal(12, info.DepthReached);
        Assert.Equal(345_678, info.Nodes);
        Assert.Equal(250, info.Elapsed.TotalMilliseconds);
        Assert.Equal(new[] { first, reply }, info.PrincipalVariation);
        Assert.True(info.Exact);
        Assert.Null(again.Plies[1].Info);
        Assert.Equal("hard", again.Fox.Name);
        Assert.Equal(PlayerInfo.HumanKind, again.Hounds.Kind);
    }

    [Fact]
    public void Enum_values_are_written_in_camel_case()
    {
        var json = JsonNode.Parse(GameRecordFile.ToJson(Played(RuleSet.Gilith with { BoardSize = 6 }, 4)))!;
        Assert.Equal("fhg/1", (string?)json["format"]);
        Assert.Equal("houndsImmobile", (string?)json["ruleSet"]!["foxWinCondition"]);
        Assert.Matches("^(foxWins|houndsWin)$", (string?)json["result"]);
    }

    [Fact]
    public void A_record_whose_stored_result_does_not_match_the_game_is_rejected()
    {
        var record = Played(RuleSet.Classic with { BoardSize = 6 }, 5);
        var json = JsonNode.Parse(GameRecordFile.ToJson(record))!;
        json["result"] = record.Outcome.Result == GameResult.FoxWins ? "houndsWin" : "foxWins";
        var ex = Assert.Throws<InvalidDataException>(() => GameRecordFile.Parse(json.ToJsonString()));
        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);

        var reason = JsonNode.Parse(GameRecordFile.ToJson(record))!;
        reason["reason"] = record.Outcome.Reason == EndReason.FoxTrapped ? "foxReachedHome" : "foxTrapped";
        Assert.Throws<InvalidDataException>(() => GameRecordFile.Parse(reason.ToJsonString()));

        var unfinished = JsonNode.Parse(GameRecordFile.ToJson(record))!;
        unfinished["plies"]!.AsArray().RemoveAt(unfinished["plies"]!.AsArray().Count - 1);
        Assert.Throws<InvalidDataException>(() => GameRecordFile.Parse(unfinished.ToJsonString()));
    }

    [Fact]
    public void A_forfeit_is_accepted_only_for_an_unfinished_game_won_by_the_other_side()
    {
        var rules = new Rules(RuleSet.Classic);
        Assert.True(rules.Geo.TryParseMove("e1-d2", out var move));
        var record = new GameRecord
        {
            RuleSet = RuleSet.Classic,
            Start = rules.Initial(),
            Fox = PlayerInfo.Human,
            Hounds = PlayerInfo.Human,
            Plies = [new PlyRecord(move, null)],
            Outcome = Outcome.Win(Side.Fox, EndReason.Forfeit),
            Forfeit = new ForfeitInfo(Side.Hounds, ForfeitCause.IllegalMove, "played h8-h8"),
        };
        var again = GameRecordFile.Parse(GameRecordFile.ToJson(record));
        Assert.Equal(record.Forfeit, again.Forfeit);
        Assert.Equal(record.Outcome, again.Outcome);

        var wrongWinner = record with { Outcome = Outcome.Win(Side.Hounds, EndReason.Forfeit) };
        Assert.Throws<InvalidDataException>(() => GameRecordFile.Parse(GameRecordFile.ToJson(wrongWinner)));
        var noDetails = record with { Forfeit = null };
        Assert.Throws<InvalidDataException>(() => GameRecordFile.Parse(GameRecordFile.ToJson(noDetails)));
        var detailsWithoutForfeit = Played(RuleSet.Classic with { BoardSize = 6 }, 6) with { Forfeit = record.Forfeit };
        Assert.Throws<InvalidDataException>(() => GameRecordFile.Parse(GameRecordFile.ToJson(detailsWithoutForfeit)));
    }

    [Theory]
    [InlineData("format", "\"fhg/2\"")]
    [InlineData("format", "\"agent/1\"")]
    [InlineData("format", "null")]
    [InlineData("start", "\"8/z9/b8,d8,f8,h8/F\"")]
    [InlineData("result", "\"draw\"")]
    [InlineData("plies", "[{ \"move\": \"e1-e2\" }]")]
    [InlineData("plies", "[{ \"move\": \"e1-d2\", \"info\": { \"depth\": 1, \"nodes\": 1, \"pv\": [\"x\"] } }]")]
    public void Malformed_records_are_rejected(string field, string value)
    {
        var json = JsonNode.Parse(GameRecordFile.ToJson(Played(RuleSet.Classic, 7)))!;
        json[field] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => GameRecordFile.Parse(json.ToJsonString()));
    }

    [Fact]
    public void Unknown_fields_and_minor_versions_are_accepted()
    {
        var record = Played(RuleSet.Classic, 8);
        var json = JsonNode.Parse(GameRecordFile.ToJson(record))!;
        json["format"] = "fhg/1.2";
        json["comment"] = "added by a later version";
        json["plies"]![0]!["note"] = "ignored";
        Assert.Equal(record.Outcome, GameRecordFile.Parse(json.ToJsonString()).Outcome);
    }
}
