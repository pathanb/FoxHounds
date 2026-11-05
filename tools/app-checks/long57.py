#!/usr/bin/env python3
# Builds a 57-ply game on 8x8 under the Gilith rules: the longest game the board allows (2 * 4 hounds * 7 rows + 1).
# Each hound marches down its own two-file lane in turn (b8-a1, d8-c1, h8-g1, f8-e1); the fox shuffles e1/f2, climbs
# to the top left once the left lanes are clear, then shuffles a7/b8. After the hounds' 28th move all four stand on
# row 1, the fox makes ply 57 and the hounds cannot move: the fox wins.
import json, sys
lanes = {
    "L1": "b8 a7 b6 a5 b4 a3 b2 a1".split(),
    "L2": "d8 c7 d6 c5 d4 c3 d2 c1".split(),
    "L4": "h8 g7 h6 g5 h4 g3 h2 g1".split(),
    "L3": "f8 e7 f6 e5 f4 e3 f2 e1".split(),
}
hound_moves = []
for lane in ("L1", "L2", "L4", "L3"):
    path = lanes[lane]
    hound_moves += [f"{a}-{b}" for a, b in zip(path, path[1:])]
fox_moves = []
fox = "e1"
for k in range(1, 16):                       # F1..F15: shuffle e1/f2, ending on f2
    to = "f2" if fox == "e1" else "e1"
    fox_moves.append(f"{fox}-{to}"); fox = to
for to in "e3 d4 c5 b6 a7".split():          # F16..F20: climb to the top left
    fox_moves.append(f"{fox}-{to}"); fox = to
while len(fox_moves) < 29:                   # F21..F29: shuffle a7/b8
    to = "b8" if fox == "a7" else "a7"
    fox_moves.append(f"{fox}-{to}"); fox = to
assert len(hound_moves) == 28 and len(fox_moves) == 29
plies = []
for i in range(29):
    plies.append(fox_moves[i])
    if i < 28:
        plies.append(hound_moves[i])
record = {
    "format": "fhg/1", "appVersion": "m7-check", "startedUtc": "2026-11-05T12:00:00Z",
    "ruleSet": {"boardSize": 8, "foxStart": "fixed", "firstMover": "fox", "foxWinCondition": "houndsImmobile", "adjudicateEscape": False},
    "start": "8/e1/b8,d8,f8,h8/F",
    "players": {"fox": {"kind": "human"}, "hounds": {"kind": "human"}},
    "plies": [{"move": m} for m in plies],
    "result": "foxWins", "reason": "houndsImmobile",
}
json.dump(record, open(sys.argv[1], "w"), indent=2)
print(len(plies), "plies")
