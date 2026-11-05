"""Solver values of random positions (not only reachable ones), computed with the reference prototype.

The C# solver table covers every legal position, so it is checked here well beyond the rows of spec 10.7.
Positions are sampled with Python's own seeded RNG; the list itself is the test vector, so the C# side never
needs to reproduce the sampling.

Run: python3 reference/solver_vectors.py > reference/solver_vectors.txt
Line format: <ruleset> <notation> <F|H><plies>   (winner with perfect play, adjudication off)
"""
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fh_proto as fp  # noqa: E402

PER_CASE = {4: 60, 5: 60, 6: 80, 7: 80, 8: 120}


def random_position(g, rnd):
    while True:
        squares = rnd.sample(range(g.P), g.H + 1)
        hounds = 0
        for s in squares[:g.H]:
            hounds |= 1 << s
        fox = squares[g.H]
        side = rnd.choice("FH")
        if g.win_cond == "ReachHomeRow" and side == "F" and g.rc[fox][0] == 0:
            continue  # cannot arise in play; the table stores 0 for it (spec 10.3)
        return (fox, hounds, side)


def main():
    rnd = random.Random(20261105)
    print("# fh_proto.py solver values of random legal positions, adjudication off")
    for n in sorted(PER_CASE):
        for wc, label in (("ReachHomeRow", "classic"), ("HoundsImmobile", "gilith")):
            g = fp.Game(n, wc, False)
            val = fp.solver(g)
            seen = set()
            while len(seen) < PER_CASE[n]:
                st = random_position(g, rnd)
                if st in seen:
                    continue
                seen.add(st)
                side_wins, plies = val(st)
                winner = st[2] if side_wins else ("H" if st[2] == "F" else "F")
                print(f"{label} {g.notation(st)} {winner}{plies}")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
