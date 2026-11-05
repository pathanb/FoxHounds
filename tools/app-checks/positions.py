#!/usr/bin/env python3
# Prints the position (spec 5.6 notation) after each ply of an fhg record: "plies<TAB>notation", from the start (0).
# The hounds keep the record's order, so the notation may list them differently from the app; the position is the same.
import json, sys
rec = json.load(open(sys.argv[1]))
n, fox, hounds, side = rec["start"].split("/")
hounds = hounds.split(",")
print(f"0\t{rec['start']}")
for k, ply in enumerate(rec["plies"], 1):
    a, b = ply["move"].split("-")
    if a == fox:
        fox = b
    else:
        hounds[hounds.index(a)] = b
    side = "H" if side == "F" else "F"
    print(f"{k}\t{n}/{fox}/{','.join(hounds)}/{side}")
