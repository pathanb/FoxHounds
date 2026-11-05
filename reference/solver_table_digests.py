"""SHA-256 digests of complete solver tables, computed independently with the reference prototype.

For every hound set, fox square and side to move, the prototype's own solver (memoised minimax, adjudication off)
gives the value, encoded exactly as spec 10.2/10.3 lay out the C# table:
  entry index = (rank * P + fox) * 2 + side, rank = sum C(s_i, i + 1) over the sorted hound squares
  byte 0 = fox on a hound square, or (ReachHomeRow) fox on row 0 with the fox to move
  1..127 = side to move loses in byte - 1;  128..255 = side to move wins in byte - 128
A C# table whose payload hashes to the same value agrees with the prototype on every single position.

Run: python3 reference/solver_table_digests.py > reference/solver_table_digests.txt   (about 2 minutes)
"""
import hashlib
import itertools
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fh_proto as fp  # noqa: E402
from math import comb  # noqa: E402


def table_bytes(n, win_cond):
    g = fp.Game(n, win_cond, False)
    val = fp.solver(g)
    P, H = g.P, g.H
    count = comb(P, H)
    out = bytearray(count * P * 2)
    for combo in itertools.combinations(range(P), H):
        rank = sum(comb(s, i + 1) for i, s in enumerate(combo))
        hounds = 0
        for s in combo:
            hounds |= 1 << s
        for fox in range(P):
            if hounds >> fox & 1:
                continue
            for side_index, side in enumerate("FH"):
                index = (rank * P + fox) * 2 + side_index
                if win_cond == "ReachHomeRow" and side == "F" and g.rc[fox][0] == 0:
                    continue  # stays 0
                wins, plies = val((fox, hounds, side))
                out[index] = 128 + plies if wins else 1 + plies
    return bytes(out)


def main():
    print("# n winCondition entries sha256-of-payload   (fh_proto.py, adjudication off)")
    for n in (4, 5, 6, 7, 8):
        for wc in ("ReachHomeRow", "HoundsImmobile"):
            data = table_bytes(n, wc)
            print(n, wc, len(data), hashlib.sha256(data).hexdigest())
            sys.stdout.flush()


if __name__ == "__main__":
    main()
