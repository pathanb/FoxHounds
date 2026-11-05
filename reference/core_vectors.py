"""Expected values for C# Core tests that the spec does not tabulate, computed with the reference prototype.

1. RNG derivations fixed by decision D-24 (NextInt via Lemire's method, NextDouble, Split, ForGame).
2. End-of-game outcomes for the hand-built positions of T-CORE-04, under every ruleset variant.
3. More Zobrist keys (pins the HoundKey, FoxKey, SideKey draw order of spec 5.4) and the plies a uniformly random
   agent plays from Rng(5) (pins RandomAgent's use of NextInt).

Run: python3 reference/core_vectors.py > reference/core_vectors.txt
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fh_proto as fp  # noqa: E402

M64 = fp.M64


class Rng:
    """xoshiro256** from the prototype plus the D-24 derived operations."""

    def __init__(self, seed):
        self.x = fp.Xoshiro256StarStar(seed)

    def next_u64(self):
        return self.x.next()

    def next_int(self, s):
        x = self.next_u64()
        m = x * s
        lo = m & M64
        if lo < s:
            t = ((1 << 64) - s) % s
            while lo < t:
                x = self.next_u64()
                m = x * s
                lo = m & M64
        return m >> 64

    def next_double(self):
        return (self.next_u64() >> 11) * (1.0 / (1 << 53))

    def split(self):
        return Rng(self.next_u64())

    @staticmethod
    def for_game(experiment_seed, game_index):
        return Rng(experiment_seed ^ ((game_index * 0xD1B54A32D192ED03) & M64))


def rng_vectors():
    r = Rng(0)
    print("NextInt(10) x10 from seed 0:", [r.next_int(10) for _ in range(10)])
    r = Rng(42)
    print("NextInt(7) x5 from seed 42:", [r.next_int(7) for _ in range(5)])
    r = Rng(42)
    print("NextInt(2147483647) x3 from seed 42:", [r.next_int(2147483647) for _ in range(3)])
    r = Rng(0)
    print("NextDouble x3 from seed 0:", [repr(r.next_double()) for _ in range(3)])
    r = Rng.for_game(20261105, 17)
    print("ForGame(20261105, 17) NextU64 x2:", [hex(r.next_u64()) for _ in range(2)])
    r = Rng(7)
    a, b = r.split(), r.split()
    print("seed 7: Split() then NextU64 of each child:", hex(a.next_u64()), hex(b.next_u64()))
    z = fp.splitmix64(0x9E3779B97F4A7C15 ^ 8)
    hound = [next(z) for _ in range(32)]
    fox = [next(z) for _ in range(32)]
    side = next(z)
    print("Zobrist n=8 FoxKey[0], FoxKey[31], SideKey:", hex(fox[0]), hex(fox[31]), hex(side))
    g = fp.Game(8)
    st = g.initial()
    r = Rng(5)
    plies = []
    for _ in range(14):
        ms = g.moves(st)
        m = ms[r.next_int(len(ms))]
        plies.append(f"{g.name(m[0])}-{g.name(m[1])}")
        st = g.apply(st, m)
    print("uniform random plies from the 8x8 start with Rng(5):", " ".join(plies))


CASES = [
    # (label, notation)
    ("fox trapped in a corner by one hound", "8/a1/b2,b8,d8,f8/F"),
    ("fox trapped in the middle by four hounds", "8/d4/c5,e5,c3,e3/F"),
    ("fox on row 0, hounds to move", "8/d8/b8,f8,h8,e7/H"),
    ("fox on row 0, fox to move", "8/d8/b8,f8,h8,e7/F"),
    ("hounds immobile on the back row", "8/b4/a1,c1,e1,g1/H"),
    ("fox home and hounds immobile (rule order)", "8/b8/a1,c1,e1,g1/H"),
    ("escape exactly at fox.row = minHoundRow, fox to move", "8/c5/e5,b4,d4,h4/F"),
    ("escape exactly at fox.row = minHoundRow, hounds to move", "8/c5/e5,b4,d4,h4/H"),
    ("fox one row behind the rearmost hound", "8/c5/d6,b4,d4,h4/F"),
    ("fox ahead of hounds with minHoundRow = 2", "8/c7/b6,d6,f6,h6/F"),
    ("fox on row 0 with minHoundRow = 1, hounds to move", "8/d8/a7,c7,e7,g7/H"),
    ("fox on row 0 boxed in with minHoundRow = 1, fox to move", "8/d8/a7,c7,e7,g7/F"),
]

VARIANTS = [
    ("ReachHomeRow adj off", "ReachHomeRow", False),
    ("ReachHomeRow adj on", "ReachHomeRow", True),
    ("HoundsImmobile adj off", "HoundsImmobile", False),
    ("HoundsImmobile adj on", "HoundsImmobile", True),
]


def outcome_vectors():
    for label, text in CASES:
        print(f"{text:24} {label}")
        for vlabel, wc, adj in VARIANTS:
            g = fp.Game(8, wc, adj)
            st = g.parse(text)
            o = g.outcome(st)
            res = "Ongoing" if o is None else f"{'FoxWins' if o[0] == 'F' else 'HoundsWin'} {o[1]}"
            print(f"    {vlabel:24} {res:28} escaped={g.escaped(st)}")


if __name__ == "__main__":
    rng_vectors()
    print()
    outcome_vectors()
