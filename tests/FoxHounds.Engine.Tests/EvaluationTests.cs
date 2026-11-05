using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Solver;

namespace FoxHounds.Engine.Tests;

/// <summary>T-EVAL-01 (features), T-EVAL-02 (weight files), T-EVAL-03 (evaluator hygiene).</summary>
public sealed class EvaluationTests
{
    // Spec 9.5, columns as rows: FoxAdvance … FoxToMove.
    public static TheoryData<string, int[]> SpecVectors => new()
    {
        { "8/e1/b8,d8,f8,h8/F", [0, 2, 7, 27, 0, 0, 0, 0, 28, 7, 1] },
        { "8/d4/a7,c7,e7,f6/H", [3, 4, 6, 25, 1, 2, 0, 1, 23, 3, 0] },
        { "8/c5/b6,d6,f6,h6/F", [4, 2, 5, 19, 0, 0, 0, 0, 20, 1, 1] },
        { "8/g3/b6,d6,e5,g5/F", [2, 4, 7, 17, 0, 0, 0, 1, 18, 3, 1] },
    };

    [Theory]
    [MemberData(nameof(SpecVectors))]
    public void Features_equal_spec_9_5(string notation, int[] expected)
    {
        var s = new Rules(RuleSet.Classic).FromNotation(notation);
        Span<int> f = stackalloc int[FeatureExtractor.Count];
        FeatureExtractor.Extract(s, f);
        Assert.Equal(expected, f.ToArray());
    }

    [Fact]
    public void Features_equal_the_prototype_on_900_random_positions_of_six_board_sizes()
    {
        // reference/feature_vectors.txt: n = 4, 5, 7, 8, 11, 16, 150 positions each, computed by fh_proto.py.
        int compared = 0;
        Span<int> f = stackalloc int[FeatureExtractor.Count];
        foreach (string line in File.ReadLines(RepoRoot.File("reference/feature_vectors.txt")))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split(' ');
            int n = int.Parse(parts[0].AsSpan(0, parts[0].IndexOf('/')), System.Globalization.CultureInfo.InvariantCulture);
            var s = new Rules(RuleSet.Classic with { BoardSize = n }).FromNotation(parts[0]);
            FeatureExtractor.Extract(s, f);
            int[] expected = parts[1..].Select(int.Parse).ToArray();
            if (!expected.AsSpan().SequenceEqual(f))
            {
                Assert.Fail($"{parts[0]}: expected [{string.Join(' ', expected)}], got [{string.Join(' ', f.ToArray())}]");
            }

            compared++;
        }

        Assert.Equal(900, compared);
    }

    [Fact]
    public void The_64_bit_and_128_bit_paths_agree_on_every_small_board()
    {
        var rng = new Rng(64);
        Span<int> fast = stackalloc int[FeatureExtractor.Count];
        Span<int> general = stackalloc int[FeatureExtractor.Count];
        for (int n = 4; n <= 11; n++)
        {
            var rules = new Rules(RuleSet.Classic with { BoardSize = n });
            for (int i = 0; i < 3_000; i++)
            {
                var s = Positions.RandomLegal(rules, rng);
                FeatureExtractor.Extract(s, fast);
                FeatureExtractor.Extract128(s, general);
                if (!fast.SequenceEqual(general))
                {
                    Assert.Fail($"{s}: 64-bit [{string.Join(' ', fast.ToArray())}] vs 128-bit [{string.Join(' ', general.ToArray())}]");
                }
            }
        }
    }

    [Fact]
    public void Feature_names_follow_FeatureId_order()
    {
        Assert.Equal(FeatureExtractor.Count, Enum.GetValues<FeatureId>().Length);
        Assert.Equal("FoxAdvance", FeatureExtractor.FeatureNames[0]);
        Assert.Equal("FoxToMove", FeatureExtractor.FeatureNames[10]);
    }

    [Fact]
    public void Linear_evaluator_is_the_clamped_weighted_sum()
    {
        var eval = (LinearEvaluator)WeightFile.LinV0.CreateEvaluator(8);
        var s = new Rules(RuleSet.Classic).FromNotation("8/d4/a7,c7,e7,f6/H");
        // 8·3 + 12·4 − 4·6 + 3·25 + 250·1 + 25·2 + 80·0 + 15·1 − 2·23 − 10·3 + 5·0
        Assert.Equal(362, eval.EvaluateFox(s));
        Assert.Equal("lin-v0", eval.Name);

        var huge = new LinearEvaluator("huge", [0, 0, 0, 0, 0, 0, 0, 0, 1_000_000, 0, 0]);
        Assert.Equal(Scores.EvalMax, huge.EvaluateFox(s));
        var negative = new LinearEvaluator("neg", [0, 0, 0, 0, 0, 0, 0, 0, -1_000_000, 0, 0]);
        Assert.Equal(-Scores.EvalMax, negative.EvaluateFox(s));
    }

    [Fact]
    public void Tapered_evaluator_uses_early_weights_at_the_start_and_late_weights_at_the_end()
    {
        var rules = new Rules(RuleSet.Classic);
        int[] early = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        int[] late = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100];
        var tapered = new TaperedEvaluator("t", early, late);
        var start = rules.Initial();                                   // phase 256: early only → FoxAdvance 0
        Assert.Equal(0, tapered.EvaluateFox(start));
        var end = rules.FromNotation("8/b4/a1,c1,e1,g1/F");            // phase 0: late only → FoxToMove·100
        Assert.Equal(100, tapered.EvaluateFox(end));
        var mid = rules.FromNotation("8/d4/a7,c7,e7,f6/F");            // HoundMovesLeft 23: phase 256·23/28 = 210
        // (210·FoxAdvance 3 + 46·100) / 256 = (630 + 4600) / 256 = 20 (truncated)
        Assert.Equal(20, tapered.EvaluateFox(mid));
    }

    [Fact]
    public void Weight_file_on_disk_equals_the_built_in_lin_v0()
    {
        var file = WeightFile.Load(RepoRoot.File("weights/lin-v0.json"));
        Assert.Equal(WeightFile.LinV0.Name, file.Name);
        Assert.Null(file.BoardSize);
        Assert.Equal(WeightFile.ToVector(WeightFile.LinV0.Weights, "built-in"), WeightFile.ToVector(file.Weights, "file"));
        Assert.Equal(WeightFile.LinV0.ToJson(), file.ToJson());
    }

    [Fact]
    public void An_unknown_feature_name_is_a_load_error()
    {
        const string json = """{ "name": "x", "evaluator": "linear", "weights": { "FoxAdvanse": 3 } }""";
        var ex = Assert.Throws<InvalidDataException>(() => WeightFile.Parse(json));
        Assert.Contains("FoxAdvanse", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => WeightFile.Parse("""{ "name": "x", "evaluator": "linear", "weights": { "foxadvance": 3 } }"""));
        Assert.Throws<InvalidDataException>(() => WeightFile.Parse("""{ "name": "x", "evaluator": "linear", "weights": { "3": 3 } }"""));
        Assert.Throws<InvalidDataException>(() => WeightFile.Parse("""{ "name": "x", "evaluator": "linear", "weights": { "FoxAdvance, FoxMobility": 3 } }"""));
    }

    [Fact]
    public void A_board_size_mismatch_is_a_load_error_but_missing_features_are_zero()
    {
        const string json = """{ "name": "tuned", "evaluator": "linear", "boardSize": 8, "weights": { "GoalReachable": 300 } }""";
        var file = WeightFile.Parse(json);
        Assert.Throws<InvalidDataException>(() => file.CreateEvaluator(10));
        var eval = (LinearEvaluator)file.CreateEvaluator(8);
        Assert.Equal(300, eval.Weights[(int)FeatureId.GoalReachable]);
        Assert.Equal(0, eval.Weights[(int)FeatureId.FoxAdvance]);
    }

    [Theory]
    [InlineData("""{ "evaluator": "linear", "weights": {} }""")]                                    // no name
    [InlineData("""{ "name": "x", "evaluator": "cubic", "weights": {} }""")]                       // unknown evaluator
    [InlineData("""{ "name": "x", "evaluator": "linear" }""")]                                     // no weights
    [InlineData("""{ "name": "x", "evaluator": "tapered", "weights": {} }""")]                     // tapered needs early/late
    [InlineData("""{ "name": "x", "evaluator": "linear", "weights": {}, "early": {} }""")]         // mixed
    [InlineData("""{ "format": "weights/2", "name": "x", "evaluator": "linear", "weights": {} }""")] // future version
    [InlineData("""{ "name": "x", "evaluator": "linear", "boardSize": 3, "weights": {} }""")]      // bad size
    [InlineData("""{ "name": "x", "evaluator": "linear", "weights": { "FoxAdvance": "high" } }""")] // not a number
    [InlineData("not json")]
    public void Malformed_weight_files_are_rejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => WeightFile.Parse(json));
    }

    [Fact]
    public void Tapered_files_round_trip_and_unknown_fields_are_ignored()
    {
        const string json = """
            { "name": "tap", "evaluator": "tapered", "early": { "FoxAdvance": 2 }, "late": { "FoxToMove": 9 },
              "comment": "ignored", "provenance": "test" }
            """;
        var file = WeightFile.Parse(json);
        var again = WeightFile.Parse(file.ToJson());
        Assert.Equal(file.ToJson(), again.ToJson());
        Assert.IsType<TaperedEvaluator>(EvaluatorRegistry.Create("tapered", again, 8));
        Assert.Throws<InvalidDataException>(() => EvaluatorRegistry.Create("linear", again, 8));
        Assert.Throws<ArgumentException>(() => EvaluatorRegistry.Create("nope", null, 8));
        Assert.Throws<InvalidDataException>(() => EvaluatorRegistry.Create("linear", null, 8));
    }

    public static TheoryData<string> Evaluators => new(EvaluatorRegistry.Names.Append(SolverEvaluator.TypeName));

    [Theory]
    [MemberData(nameof(Evaluators))]
    public void Every_registered_evaluator_is_deterministic_bounded_and_allocation_free(string type)
    {
        var rules = new Rules(RuleSet.Classic);
        IEvaluator eval = type switch
        {
            SolverEvaluator.TypeName => new SolverEvaluator(SolverTable.Build(rules, null, TestContext.Current.CancellationToken)),
            WeightFile.Tapered => EvaluatorRegistry.Create(type, WeightFile.Parse("""{ "name": "t", "evaluator": "tapered", "early": { "FoxAdvance": 9 }, "late": { "HoundsPassed": 50 } }"""), 8),
            _ => EvaluatorRegistry.Create(type, WeightFile.LinV0, 8),
        };

        var rng = new Rng(17);
        var positions = new List<GameState>();
        while (positions.Count < 2_000)
        {
            var s = Positions.RandomPlayed(rules, rng);
            if (!rules.Evaluate(s).IsOver)
            {
                positions.Add(s);
            }
        }

        var first = positions.Select(p => eval.EvaluateFox(p)).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            int v = eval.EvaluateFox(positions[i]);
            checksum ^= v;
            if (v != first[i])
            {
                Assert.Fail($"{eval.Name} gave {first[i]} then {v} for {positions[i]}");
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.All(first, v => Assert.InRange(v, -Scores.EvalMax, Scores.EvalMax));
        Assert.NotEqual(int.MinValue, checksum);
    }
}
