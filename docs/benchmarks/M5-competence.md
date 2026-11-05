# M5: first competence baseline (spec 8.9)

These runs measured lin-v0 (the hand-set weights of 9.3) on 8×8 Classic against the solver. Each was run with
`fh match` or `fh grade` from `experiments/`, using 2 workers on a 2-vCPU Linux machine with a Release build. The
summaries are in `M5/`, and every run can be reproduced with the commands in [PLAN.md](../PLAN.md).

## The 8.9 metrics at the default UI budget (2,000,000 nodes per ply)

Candidate `lin-v0-2m`: minimax, lin-v0, best root move, 16 MB table, `maxNodes` 2,000,000. Reference: `solver` with
random ties; when losing it always plays the longest resistance.

| Metric (8.9) | Openings | Result, 95% Wilson interval | Provisional target |
|---|---|---|---|
| Hounds conversion vs solver fox | 1,000 hounds-won D-15 openings (6–16 plies, seed 7) | **100.0%** [99.62, 100] (1000/1000) | ≥ 99% |
| Fox conversion vs solver hounds | 1,000 fox-won positions of the D-28 test split | **100.0%** [99.62, 100] (1000/1000) | ≥ 95% |
| Fox conversion vs solver hounds | 1,000 fox-won D-15 openings (seed 8) | **100.0%** [99.62, 100] (1000/1000) | (extra) |
| Hounds blunder rate | the hounds-conversion games | **0.00%** [0, 0.02] (0/17,158 plies in won positions) | ≤ 0.5% |
| Fox blunder rate | the suite games / the opening games | **0.00%** [0, 0.13] (0/2,847) / [0, 0.08] (0/4,946) | ≤ 0.5% |

The hounds' searches used about 202,000 nodes per move on average, not 2,000,000. Most searches stop early on a
proven result that fits the searched depth (D-39). The fox's wins are short: in Classic most fox-won positions end
by escape adjudication within a few plies. So the fox metrics hardly test the evaluator.

## Budget sweep

The same openings and reference, at lower budgets. These are the budgets where lin-v0 still makes mistakes, so they
are where the M6 tuner can show an effect.

| Nodes per ply | Hounds conversion | Hounds blunders | Fox conversion (openings) | Fox blunders |
|---:|---|---|---|---|
| 1,000 | 89.6% [87.55, 91.34] | 0.63% (104/16,425) | 97.9% [96.81, 98.62] | 0.44% (21/4,796) |
| 5,000 | 98.4% [97.42, 99.01] | 0.09% (16/17,386) | 100.0% [99.62, 100] | 0.00% |
| 20,000 | 98.8% [97.91, 99.31] | 0.07% (12/16,992) | 99.6% [98.98, 99.84] | 0.08% (4/4,900) |
| 100,000 | 99.6% [98.98, 99.84] | 0.02% (4/17,096) | 100.0% [99.62, 100] | 0.00% |
| 500,000 | 100.0% [99.62, 100] | 0.00% (0/17,158) | 100.0% [99.62, 100] | 0.00% |
| 2,000,000 | 100.0% [99.62, 100] | 0.00% (0/17,158) | 100.0% [99.62, 100] | 0.00% |

The fox's results are not monotonic in the budget (100% at 5,000 nodes, 99.6% at 20,000). A different budget
changes which move is played, and 4 losses in 1,000 games is within noise.

## Grade mode (the SPSA objective of D-19)

`lin-v0-20k` (`maxNodes` 20,000) chose one ply in each of the 13,757 positions of the D-28 test split:

| Side to move | Positions | Accuracy (won positions kept) | Resistance (lost positions, longest loss) |
|---|---:|---|---|
| fox | 6,987 | 99.98% [99.88, 100] (4804/4805) | 99.36% [98.93, 99.62] (2168/2182) |
| hounds | 6,770 | 99.36% [99.02, 99.58] (3248/3269) | 99.49% [99.19, 99.67] (3483/3501) |
| all | 13,757 | 99.73% [99.59, 99.82] (8052/8074) | 99.44% [99.21, 99.60] (5651/5683) |

Accuracy at 20,000 nodes leaves only 22 errors in 8,074 won positions. An objective built on it gives SPSA very
little signal; see D-60.

## The suite (D-28)

`fh suite --n 8 --seed 11 --workers 2 --max-games 10000000` played 10,000,000 games in 28 minutes. Several strata
could not be filled within that cap:

| Split | Wanted | Got |
|---|---:|---:|
| train | 50,000 | 34,370 |
| validation | 10,000 | 6,872 |
| test | 20,000 | 13,757 |

The missing positions are in the late phase (HoundMovesLeft 0–7, both winners) and in the hounds-won strata of
phases 1 and 3. Late positions are rare because hound wins usually end, and fox wins escape, before the hounds run
out of moves; early hounds-won positions are few and repeat often. See the D-28 amendment in D-60.
