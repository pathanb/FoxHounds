"""Probe: how many distinct hounds-won positions exist k plies into an 8x8 Classic game,
and how likely a random (or noisy-solver) opening is to land on one.

Uses the reference prototype for rules and solver values (adjudication off, as for labels).
"""
import os
import sys
from collections import defaultdict

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import fh_proto as fp  # noqa: E402


def hounds_win(g, val, st):
    side_wins, _ = val(st)
    winner = st[2] if side_wins else ("H" if st[2] == "F" else "F")
    return winner == "H"


def layer_distribution(g, start, plies, policy):
    """Exact probability distribution over positions after `plies` plies (terminal lines dropped)."""
    dist = {start: 1.0}
    for _ in range(plies):
        nxt = defaultdict(float)
        for st, p in dist.items():
            if g.outcome(st) is not None:
                continue  # game ended early: opening is dropped
            for child, q in policy(st):
                nxt[child] += p * q
        dist = nxt
    return {s: p for s, p in dist.items() if g.outcome(s) is None}


def main():
    n = 8
    g = fp.Game(n, "ReachHomeRow", False, "F")
    val = fp.solver(g)

    def uniform(st):
        ms = g.moves(st)
        return [(g.apply(st, m), 1.0 / len(ms)) for m in ms]

    def noisy_solver(eps):
        def pol(st):
            ms = g.moves(st)
            children = [g.apply(st, m) for m in ms]
            # solver choice: shortest win, else longest loss; ties -> first in order
            best_i, best = None, None
            for i, c in enumerate(children):
                cw, cd = val(c)
                mine = (not cw, cd + 1)
                if best is None or (mine[0] and (not best[0] or mine[1] < best[1])) or \
                        (not mine[0] and not best[0] and mine[1] > best[1]):
                    best_i, best = i, mine
            out = [(c, eps / len(ms)) for c in children]
            out[best_i] = (out[best_i][0], out[best_i][1] + (1 - eps))
            return out
        return pol

    print("8x8 Classic, fox first, adjudication off. Per opening length k:")
    print("  distinct = distinct non-terminal positions after k plies (all 4 fox starts pooled)")
    print("  H-won    = how many of those the solver rates hounds-won")
    print("  P(H) uniform / noisy = chance one random opening lands hounds-won")
    for k in (2, 4, 6, 8, 10):
        pooled_u, pooled_n = defaultdict(float), defaultdict(float)
        for b in g.back:
            st0 = g.initial(b)
            for s, p in layer_distribution(g, st0, k, uniform).items():
                pooled_u[s] += p / len(g.back)
            for s, p in layer_distribution(g, st0, k, noisy_solver(0.1)).items():
                pooled_n[s] += p / len(g.back)
        hw = [s for s in pooled_u if hounds_win(g, val, s)]
        tot_u, tot_n = sum(pooled_u.values()), sum(pooled_n.values())
        pu = sum(pooled_u[s] for s in hw) / tot_u
        pn = sum(pooled_n[s] for s in hw) / tot_n

        def expected_distinct(pooled, tot, draws):
            # expected number of distinct hounds-won positions among `draws` raw openings
            return sum(1 - (1 - pooled[s] / tot) ** draws for s in hw)

        print(f"  k={k:2}: distinct {len(pooled_u):7,}  H-won {len(hw):6,}  "
              f"P(H) uniform {pu:8.4%}  noisy(eps=0.1) {pn:7.2%}")
        for draws in (10_000, 100_000, 1_000_000):
            print(f"         after {draws:>9,} raw draws, expected distinct H-won: "
                  f"uniform {expected_distinct(pooled_u, tot_u, draws):8,.0f}   "
                  f"noisy {expected_distinct(pooled_n, tot_n, draws):8,.0f}")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
