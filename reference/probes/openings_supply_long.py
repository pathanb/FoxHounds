"""Probe 2: supply of distinct hounds-won openings for longer openings, and for the fixed e1 start only."""
import os
import sys
from collections import defaultdict

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import fh_proto as fp  # noqa: E402


def main():
    g = fp.Game(8, "ReachHomeRow", False, "F")
    val = fp.solver(g)

    def hounds_won(st):
        w, _ = val(st)
        return (st[2] if w else ("H" if st[2] == "F" else "F")) == "H"

    def layers(start, kmax):
        """Distinct non-terminal positions at each exact depth 1..kmax (uniform-random reachable set)."""
        cur, out = {start}, {}
        for k in range(1, kmax + 1):
            nxt = set()
            for st in cur:
                if g.outcome(st) is not None:
                    continue
                for m in g.moves(st):
                    nxt.add(g.apply(st, m))
            cur = {s for s in nxt if g.outcome(s) is None}
            out[k] = cur
        return out

    kmax = 20
    per_start = {b: layers(g.initial(b), kmax) for b in g.back}
    e1 = g.sq[(7, 4)]
    print("distinct hounds-won non-terminal positions at exactly k plies (8x8 Classic, fox first)")
    print(" k   all 4 starts pooled   e1 start only")
    for k in range(2, kmax + 1, 2):
        pooled = set().union(*(per_start[b][k] for b in g.back))
        hw_all = sum(1 for s in pooled if hounds_won(s))
        hw_e1 = sum(1 for s in per_start[e1][k] if hounds_won(s))
        print(f"{k:2}   {hw_all:8,} of {len(pooled):7,}   {hw_e1:6,} of {len(per_start[e1][k]):7,}")
        sys.stdout.flush()
    # cumulative over a band of lengths
    for lo, hi in ((6, 12), (6, 16), (8, 20)):
        pooled = set()
        for b in g.back:
            for k in range(lo, hi + 1):
                pooled |= per_start[b][k]
        hw = sum(1 for s in pooled if hounds_won(s))
        print(f"lengths {lo}-{hi} pooled, all starts: {hw:,} distinct hounds-won positions")

    # check the three sample lines of spec 14.5
    for t in ("8/b4/b8,h8,e7,d6/F", "8/f2/f8,h8,e7,d6/F", "8/d2/b8,f8,e7,h6/F"):
        st = g.parse(t)
        print(t, fp.describe(st, val(st)))


if __name__ == "__main__":
    main()
