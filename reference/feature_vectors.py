"""Feature vectors (spec 9.1) of random positions on several board sizes, computed with the reference prototype.

Run: python3 reference/feature_vectors.py > reference/feature_vectors.txt
Line format: <notation> <11 feature values in FeatureId order>
"""
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fh_proto as fp  # noqa: E402

ORDER = ["FoxAdvance", "FoxMobility", "HoundMobility", "FoxRegion", "GoalReachable", "GoalCloseness",
         "HoundsPassed", "HoundSpread", "HoundMovesLeft", "EscapeDistance", "FoxToMove"]


def main():
    rnd = random.Random(911)
    print("# fh_proto.py feature vectors: notation, then " + " ".join(ORDER))
    for n in (4, 5, 7, 8, 11, 16):
        g = fp.Game(n)
        for _ in range(150):
            squares = rnd.sample(range(g.P), g.H + 1)
            hounds = 0
            for s in squares[:g.H]:
                hounds |= 1 << s
            st = (squares[g.H], hounds, rnd.choice("FH"))
            f = fp.features(g, st)
            print(g.notation(st), " ".join(str(f[k]) for k in ORDER))


if __name__ == "__main__":
    main()
