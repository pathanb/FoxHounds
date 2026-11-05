# Reference prototype

`fh_proto.py` is the independent source of truth for the numbers in the specification: perft counts,
reachable-position counts, solver results, feature vectors and RNG vectors (spec 15.4). It is deliberately
naive pure Python and is not production code.

```bash
python3 fh_proto.py all          # every reference table, about 4 minutes
python3 fh_proto.py solve 8
python3 fh_proto.py perft 8 10
python3 fh_proto.py features "8/d4/a7,c7,e7,f6/H"
python3 fh_proto.py rng
```

`fh_proto_all.txt` is the output of `all` from 2026-11-05. Every value in spec sections 9.5, 10.7, 15.2 and
15.3 matches it.

`probes/` holds the scripts behind decision D-15 (supply of distinct opening positions), with their outputs.
