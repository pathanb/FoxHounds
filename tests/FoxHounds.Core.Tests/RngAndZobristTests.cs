namespace FoxHounds.Core.Tests;

/// <summary>T-CORE-07 (fixed values of spec 15.3), the D-24 derivations, and T-CORE-05 (incremental Zobrist).</summary>
public sealed class RngAndZobristTests
{
    [Fact]
    public void SplitMix64_seeded_with_0_gives_the_published_first_output()
    {
        ulong state = 0;
        Assert.Equal(0xE220A8397B1DCDAFUL, SplitMix64.Next(ref state));
    }

    [Fact]
    public void Xoshiro_matches_spec_15_3()
    {
        var r0 = new Rng(0);
        Assert.Equal(0x99EC5F36CB75F2B4UL, r0.NextU64());
        Assert.Equal(0xBF6E1F784956452AUL, r0.NextU64());
        Assert.Equal(0x1A5F849D4933E6E0UL, r0.NextU64());
        var r42 = new Rng(42);
        Assert.Equal(0x15780B2E0C2EC716UL, r42.NextU64());
        Assert.Equal(0x6104D9866D113A7EUL, r42.NextU64());
        Assert.Equal(0xAE17533239E499A1UL, r42.NextU64());
    }

    [Fact]
    public void Zobrist_keys_match_spec_15_3()
    {
        var keys = Geometry.For(8).Zobrist;
        Assert.Equal(0x9CA8A164477D7801UL, keys.HoundKey[0]);
        Assert.Equal(0xB0643A4E15E67E01UL, keys.HoundKey[1]);
    }

    // Values from reference/core_vectors.py, which implements D-24 on top of the prototype's xoshiro256**.
    [Fact]
    public void NextInt_matches_the_reference_derivation()
    {
        var r0 = new Rng(0);
        Assert.Equal(new[] { 6, 7, 1, 4, 7, 9, 4, 5, 8, 9 }, Enumerable.Range(0, 10).Select(_ => r0.NextInt(10)));
        var r42 = new Rng(42);
        Assert.Equal(new[] { 0, 2, 4, 6, 6 }, Enumerable.Range(0, 5).Select(_ => r42.NextInt(7)));
        var big = new Rng(42);
        Assert.Equal(new[] { 180094358, 813853890, 1460382104 }, Enumerable.Range(0, 3).Select(_ => big.NextInt(int.MaxValue)));
    }

    [Fact]
    public void NextDouble_ForGame_and_Split_match_the_reference_derivation()
    {
        var r0 = new Rng(0);
        Assert.Equal(0.6012629994179048, r0.NextDouble());
        Assert.Equal(0.7477740925472398, r0.NextDouble());
        Assert.Equal(0.10301998939503632, r0.NextDouble());

        var game = Rng.ForGame(20261105, 17);
        Assert.Equal(0xF6A691E5EE4F5153UL, game.NextU64());
        Assert.Equal(0xE5E8859D59452F86UL, game.NextU64());

        var parent = new Rng(7);
        var a = parent.Split();
        var b = parent.Split();
        Assert.Equal(0x214C58958CA2A8A5UL, a.NextU64());
        Assert.Equal(0x82F0E307ADA2DC5EUL, b.NextU64());
    }

    [Fact]
    public void Rng_is_a_pure_function_of_its_seed()
    {
        var a = new Rng(12345);
        var b = new Rng(a.Seed);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextU64(), b.NextU64());
        }

        Assert.Equal(Rng.ForGame(1, 2).Seed, Rng.ForGame(1, 2).Seed);
        Assert.NotEqual(Rng.ForGame(1, 2).Seed, Rng.ForGame(1, 3).Seed);
    }

    [Fact]
    public void NextInt_stays_in_range_and_is_roughly_uniform()
    {
        var rng = new Rng(2026);
        var counts = new int[6];
        for (int i = 0; i < 60_000; i++)
        {
            counts[rng.NextInt(6)]++;
        }

        // Each bucket expects 10,000; ±500 is about 5 standard deviations.
        Assert.All(counts, c => Assert.InRange(c, 9_500, 10_500));
        Assert.Equal(0, rng.NextInt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rng.ForGame(1, -1));
    }

    [Fact]
    public void NextDouble_is_in_the_unit_interval()
    {
        var rng = new Rng(3);
        for (int i = 0; i < 10_000; i++)
        {
            Assert.InRange(rng.NextDouble(), 0.0, 1.0 - double.Epsilon);
        }
    }

    public static TheoryData<int> ZobristSizes => new(4, 8, 11, 16);

    [Theory]
    [MemberData(nameof(ZobristSizes))]
    public void Incremental_hash_equals_recomputation_after_every_ply_of_10000_random_games(int n)
    {
        var rules = TestPositions.Classic(n);
        var keys = rules.Geo.Zobrist;
        var rng = new Rng(0x2B0B0000UL + (ulong)n);
        for (int game = 0; game < 10_000; game++)
        {
            var start = rules.Initial(rules.FoxStartSquares[rng.NextInt(rules.FoxStartSquares.Count)]);
            TestPositions.RandomGame(rules, start, rng, s =>
            {
                if (s.Hash != keys.Compute(s.Hounds, s.Fox, s.SideToMove))
                {
                    Assert.Fail($"hash mismatch at {s}");
                }
            });
        }
    }

    [Fact]
    public void Hash_includes_the_side_key_only_when_hounds_are_to_move()
    {
        var rules = TestPositions.Classic(8);
        var fox = rules.FromNotation("8/c5/d6,b4,d4,h4/F");
        var hounds = rules.FromNotation("8/c5/d6,b4,d4,h4/H");
        Assert.Equal(rules.Geo.Zobrist.SideKey, fox.Hash ^ hounds.Hash);
    }
}
