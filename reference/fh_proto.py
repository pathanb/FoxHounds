#!/usr/bin/env python3
"""
Fox and Hounds - reference prototype (oracle for the numbers in the implementation spec).

NOT production code. It exists so the C# implementation has an independent source of truth
for: perft counts, reachable-position counts, solver results, feature vectors, RNG vectors.

Conventions follow the spec exactly:
  - row 0 = top = hounds' home row; playable squares have (r + c) odd
  - square index = (r*n + c) >> 1
  - hounds = floor(n/2), on every playable square of row 0
  - default fox start = playable square of row n-1 with the largest column <= floor(n/2)
  - all distances are in plies

Usage:
  python3 fh_proto.py all            # print every reference table (about 5 minutes; pure Python)
  python3 fh_proto.py solve 8        # Classic and Gilith results for one board size
  python3 fh_proto.py perft 8 10     # perft 1..10, Classic, fox first
  python3 fh_proto.py features "8/d4/a7,c7,e7,f6/H"
  python3 fh_proto.py rng
"""
import sys
import functools
from collections import deque

sys.setrecursionlimit(100000)
M64 = (1 << 64) - 1


# ----------------------------------------------------------------------------- rules
class Game:
    def __init__(self, n, win_cond="ReachHomeRow", adjudicate=False, first="F", fox_col=None):
        assert 4 <= n <= 16
        self.n, self.win_cond, self.adj, self.first = n, win_cond, adjudicate, first
        self.H = n // 2
        self.sq = {}
        for r in range(n):
            for c in range(n):
                if (r + c) % 2 == 1:
                    self.sq[(r, c)] = (r * n + c) >> 1
        self.P = len(self.sq)
        assert sorted(self.sq.values()) == list(range(self.P)), "square index not bijective"
        self.rc = [None] * self.P
        for (r, c), i in self.sq.items():
            self.rc[i] = (r, c)
        # neighbour lists in the spec's normative order
        self.foxn, self.houndf = [], []
        for i in range(self.P):
            r, c = self.rc[i]
            self.foxn.append([self.sq[(r + dr, c + dc)]
                              for dr, dc in ((-1, -1), (-1, 1), (1, -1), (1, 1))
                              if 0 <= r + dr < n and 0 <= c + dc < n])
            self.houndf.append([self.sq[(r + 1, c + dc)]
                                for dc in (-1, 1)
                                if 0 <= r + 1 < n and 0 <= c + dc < n])
        self.home = [self.sq[(0, c)] for c in range(n) if (0, c) in self.sq]
        assert len(self.home) == self.H
        back_cols = [c for c in range(n) if (n - 1, c) in self.sq]
        self.back = [self.sq[(n - 1, c)] for c in back_cols]
        if fox_col is None:
            fox_col = max(c for c in back_cols if c <= n // 2)
        self.fox0 = self.sq[(n - 1, fox_col)]

    # state = (fox_square, hound_bitmask, side) with side in {"F", "H"}
    def initial(self, fox=None):
        h = 0
        for s in self.home:
            h |= 1 << s
        return (self.fox0 if fox is None else fox, h, self.first)

    def moves(self, st):
        f, h, side = st
        occ = h | (1 << f)
        if side == "F":
            return [(f, t) for t in self.foxn[f] if not occ >> t & 1]
        out = []
        for s in range(self.P):
            if h >> s & 1:
                out.extend((s, t) for t in self.houndf[s] if not occ >> t & 1)
        return out

    def apply(self, st, m):
        f, h, side = st
        if side == "F":
            return (m[1], h, "H")
        return (f, (h & ~(1 << m[0])) | (1 << m[1]), "F")

    def min_row(self, h):
        return min(self.rc[s][0] for s in range(self.P) if h >> s & 1)

    def escaped(self, st):
        f, h, _ = st
        fr, m = self.rc[f][0], self.min_row(h)
        if self.win_cond == "ReachHomeRow":
            return fr <= m
        return fr < m and m >= 2

    def outcome(self, st):
        """None if ongoing, else (winner, reason). Order = spec section 3.4."""
        f, h, side = st
        if self.win_cond == "ReachHomeRow" and self.rc[f][0] == 0:
            return ("F", "FoxReachedHome")
        if not self.moves(st):
            return ("H", "FoxTrapped") if side == "F" else ("F", "HoundsImmobile")
        if self.adj and self.escaped(st):
            return ("F", "FoxEscaped")
        return None

    def name(self, i):
        r, c = self.rc[i]
        return f"{chr(97 + c)}{self.n - r}"

    def notation(self, st):
        f, h, side = st
        hs = ",".join(self.name(s) for s in range(self.P) if h >> s & 1)
        return f"{self.n}/{self.name(f)}/{hs}/{side}"

    def parse(self, text):
        n, f, hs, side = text.split("/")
        assert int(n) == self.n
        to_sq = lambda a: self.sq[(self.n - int(a[1:]), ord(a[0]) - 97)]
        h = 0
        for a in hs.split(","):
            h |= 1 << to_sq(a)
        return (to_sq(f), h, side)


# ----------------------------------------------------------------------------- solver
def solver(g):
    """Memoised value: (side_to_move_wins, plies). Winner minimises plies, loser maximises."""
    memo = {}

    def val(st):
        if st in memo:
            return memo[st]
        o = g.outcome(st)
        if o is not None:
            r = (o[0] == st[2], 0)
            memo[st] = r
            return r
        best = None
        for m in g.moves(st):
            cw, cd = val(g.apply(st, m))
            mine = (not cw, cd + 1)
            if best is None:
                best = mine
            elif mine[0] and (not best[0] or mine[1] < best[1]):
                best = mine
            elif not mine[0] and not best[0] and mine[1] > best[1]:
                best = mine
        memo[st] = best
        return best
    return val


def describe(st, v):
    winner = st[2] if v[0] else ("H" if st[2] == "F" else "F")
    return f"{'Fox wins' if winner == 'F' else 'Hounds win'} in {v[1]}"


def reachable(g, start):
    seen, dq = {start}, deque([start])
    while dq:
        st = dq.popleft()
        if g.outcome(st) is not None:
            continue
        for m in g.moves(st):
            t = g.apply(st, m)
            if t not in seen:
                seen.add(t)
                dq.append(t)
    return seen


def perft_list(g, start, depth):
    @functools.lru_cache(maxsize=None)
    def pf(st, d):
        if d == 0:
            return 1
        if g.outcome(st) is not None:
            return 0
        return sum(pf(g.apply(st, m), d - 1) for m in g.moves(st))
    return [pf(start, d) for d in range(1, depth + 1)]


def longest_game(g, start):
    @functools.lru_cache(maxsize=None)
    def lg(st):
        if g.outcome(st) is not None:
            return 0
        return 1 + max(lg(g.apply(st, m)) for m in g.moves(st))
    return lg(start)


# ----------------------------------------------------------------------------- features
def features(g, st):
    f, h, side = st
    n = g.n
    fr = g.rc[f][0]
    occ = h | (1 << f)
    hounds = [s for s in range(g.P) if h >> s & 1]
    rows = [g.rc[s][0] for s in hounds]
    dist, dq = {f: 0}, deque([f])
    while dq:
        u = dq.popleft()
        for t in g.foxn[u]:
            if t not in dist and not h >> t & 1:
                dist[t] = dist[u] + 1
                dq.append(t)
    goal = [d for s, d in dist.items() if g.rc[s][0] == 0]
    return {
        "FoxAdvance": n - 1 - fr,
        "FoxMobility": sum(1 for t in g.foxn[f] if not occ >> t & 1),
        "HoundMobility": sum(1 for s in hounds for t in g.houndf[s] if not occ >> t & 1),
        "FoxRegion": len(dist) - 1,
        "GoalReachable": 1 if goal else 0,
        "GoalCloseness": (n - min(goal)) if goal else 0,
        "HoundsPassed": sum(1 for r in rows if r > fr),
        "HoundSpread": max(rows) - min(rows),
        "HoundMovesLeft": sum(n - 1 - r for r in rows),
        "EscapeDistance": max(0, fr - min(rows)),
        "FoxToMove": 1 if side == "F" else 0,
    }


# ----------------------------------------------------------------------------- rng
def splitmix64(state):
    while True:
        state = (state + 0x9E3779B97F4A7C15) & M64
        z = state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M64
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M64
        yield z ^ (z >> 31)


def rotl(x, k):
    return ((x << k) | (x >> (64 - k))) & M64


class Xoshiro256StarStar:
    def __init__(self, seed):
        g = splitmix64(seed)
        self.s = [next(g) for _ in range(4)]

    def next(self):
        s = self.s
        result = (rotl((s[1] * 5) & M64, 7) * 9) & M64
        t = (s[1] << 17) & M64
        s[2] ^= s[0]; s[3] ^= s[1]; s[1] ^= s[2]; s[0] ^= s[3]; s[2] ^= t
        s[3] = rotl(s[3], 45)
        return result


# ----------------------------------------------------------------------------- commands
def cmd_solve(n):
    for wc, label in (("ReachHomeRow", "Classic"), ("HoundsImmobile", "Gilith")):
        for first in ("F", "H"):
            g = Game(n, wc, False, first)
            val = solver(g)
            res = "; ".join(f"{g.name(b)}: {describe(g.initial(b), val(g.initial(b)))}" for b in g.back)
            print(f"{label:8} n={n} {'fox' if first == 'F' else 'hounds'} first: {res}")


def cmd_perft(n, depth):
    g = Game(n)
    print(f"Classic n={n} fox {g.name(g.fox0)}: perft 1..{depth} = {perft_list(g, g.initial(), depth)}")


def cmd_reachable(n):
    for wc, adj, label in (("ReachHomeRow", False, "Classic adj off"),
                           ("ReachHomeRow", True, "Classic adj on"),
                           ("HoundsImmobile", False, "Gilith")):
        g = Game(n, wc, adj)
        R = reachable(g, g.initial())
        term = sum(1 for s in R if g.outcome(s) is not None)
        print(f"{label:16} n={n}: reachable {len(R):,} (terminal {term:,})")


def cmd_escape_soundness(n):
    for wc in ("ReachHomeRow", "HoundsImmobile"):
        g = Game(n, wc, False)
        val = solver(g)
        fired = bad = 0
        for first in ("F", "H"):
            g.first = first
            for b in g.back:
                for s in reachable(g, g.initial(b)):
                    if g.outcome(s) is None and g.escaped(s):
                        fired += 1
                        v = val(s)
                        fox_wins = v[0] if s[2] == "F" else not v[0]
                        bad += 0 if fox_wins else 1
        print(f"escape soundness {wc:14} n={n}: fired {fired:,}, unsound {bad}")


def cmd_features(text):
    g = Game(int(text.split("/")[0]))
    st = g.parse(text)
    print(text, features(g, st), "solver:", describe(st, solver(g)(st)))


def cmd_rng():
    print("splitmix64(0) first:", hex(next(splitmix64(0))))
    for seed in (0, 42):
        x = Xoshiro256StarStar(seed)
        print(f"xoshiro256** seed {seed}:", [hex(x.next()) for _ in range(3)])
    z = splitmix64(0x9E3779B97F4A7C15 ^ 8)
    print("Zobrist n=8 HoundKey[0], HoundKey[1]:", [hex(next(z)) for _ in range(2)])


def main(argv):
    if not argv or argv[0] == "all":
        cmd_rng()
        for n in (4, 5, 6, 7, 8):
            cmd_solve(n)
        for n in (4, 6, 8):
            cmd_perft(n, 10)
            g = Game(n)
            print(f"longest game n={n}: {longest_game(g, g.initial())} plies")
        for n in (4, 5, 6, 7, 8):
            cmd_reachable(n)
        for n in (4, 6, 8):
            cmd_escape_soundness(n)
        for t in ("8/e1/b8,d8,f8,h8/F", "8/d4/a7,c7,e7,f6/H", "8/c5/b6,d6,f6,h6/F", "8/g3/b6,d6,e5,g5/F"):
            cmd_features(t)
    elif argv[0] == "solve":
        cmd_solve(int(argv[1]))
    elif argv[0] == "perft":
        cmd_perft(int(argv[1]), int(argv[2]))
    elif argv[0] == "reachable":
        cmd_reachable(int(argv[1]))
    elif argv[0] == "features":
        cmd_features(argv[1])
    elif argv[0] == "rng":
        cmd_rng()
    else:
        print(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
