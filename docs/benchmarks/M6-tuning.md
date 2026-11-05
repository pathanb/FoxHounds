# M6: first tuning runs and a promotion (spec 12)

All runs are on 8×8 Classic: Release build, 2 workers, 2-vCPU Linux machine. Specs are in `experiments/tuning/`
and `experiments/m6/`. Reports, inputs and weights are in `M6/`.

## Regression tuner (12.2, D-17)

The fits use the 34,370 train positions of the D-28 suite, with early stopping on its 6,872 validation positions.
Losses and accuracies include the fitted intercept. The weight file drops the intercept, which shifts every
evaluation by the same amount and changes no search decision. Rerunning either spec gives a byte-identical
weights.json and report.json (12.1).

| Run | Weights | Steps (best) | Validation loss | Validation accuracy | Time |
|---|---:|---:|---:|---:|---:|
| `m6-regression-lin-n8` → lin-r1-n8 | 11 | 3,818 (3,318) | 0.352 | 83.3% | 26 s |
| `m6-regression-tapered-n8` → tap-r1-n8 | 22 | 6,826 (6,326) | 0.334 | 84.8% | 50 s |

The fitted weights look very different from lin-v0. For example, lin-r1 has FoxAdvance −562, GoalReachable 943
and EscapeDistance −460, while lin-v0 has 8, 250 and −10. They predict the solver's winner well, but they do not
play better:

| Evaluator | Hounds conversion, 1,000 nodes | Fox conversion, 1,000 nodes | Grade (validation), 1,000 nodes | Hounds conversion, 5,000 nodes | Grade (validation), 5,000 nodes |
|---|---|---|---|---|---|
| lin-v0 | 89.6% [87.55, 91.34] | 97.9% [96.81, 98.62] | 98.04% | 98.4% | 99.33% |
| lin-r1-n8 | 84.2% [81.81, 86.33] | 97.2% [95.98, 98.06] | 97.46% | 98.5% | 99.30% |
| tap-r1-n8 | 87.9% [85.73, 89.78] | 97.9% [96.81, 98.62] | 98.19% | 98.4% | 99.38% |
| lin-s1-n8 (SPSA, below) | 89.3% [87.23, 91.07] | 98.1% [97.05, 98.78] | 98.53%* | 98.3% | 99.35% |

The conversion columns are vsReference games against the solver from the M5 openings (summaries in
`M6/compare/`). The grade columns use the
validation split, pooled over both sides. *The SPSA run chose lin-s1-n8 by its validation score, so its validation
figure is biased upward.

As 12.2 warns, a better static fit is not better play.

## SPSA tuner (12.3, D-16)

`m6-spsa-lin-n8-1k`: start lin-v0, GradeAccuracy objective at 1,000 nodes per position (see D-60 for why not 20,000),
batch of 2,000 won train positions, 500 iterations, seed 20261110. The calibrated gain was a = 6,432.6. The run took
about 1 min 45 s, and a rerun gave a byte-identical weights.json and tuning.csv (12.1).

Validation score (equal weight per side) went from 0.9762 to 0.9823 at best. The resulting lin-s1-n8 has
FoxAdvance 6, FoxMobility 6, HoundMobility −7, FoxRegion 3, GoalReachable 198, GoalCloseness 31, HoundsPassed 13,
HoundSpread 26, HoundMovesLeft −5, EscapeDistance −5, FoxToMove −2. HoundsPassed fell from 80 to 13, and
HoundSpread rose from 15 to 26.

## Promotion protocol (12.4) for lin-s1-n8

Steps 2 and 3 run at 1,000 nodes per ply, the budget the candidate was tuned for; step 4 runs at the 8.9 budget.

| Step | Experiment | Result | Verdict |
|---|---|---|---|
| 2. Grade on the test split (8,074 won positions) | `m6-promotion-grade-test-*-1000` | lin-s1-n8 98.37% [98.06, 98.62] vs lin-v0 97.80% [97.45, 98.09], +0.57 pp. The intervals overlap by 0.03 pp. The test split has 13,757 positions, so the "≥ 0.5 pp over ≥ 20,000 positions" route does not apply | **fails** |
| 3. Gauntlet vs lin-v0 (the only earlier default), SPRT elo₀ = 0, elo₁ = 10 | `m6-promotion-gauntlet-lin-s1-vs-lin-v0-1000` | H1 accepted after 530 pairs: score 0.516 [0.503, 0.529], Elo +11.1 [+2.2, +20.1] | passes |
| 4. 8.9 competence at 2,000,000 nodes | `m6-promotion-competence-*-lin-s1-2m` | hounds 100.0% (1000/1000), fox 100.0% (1000/1000), no blunders (0/17,158 and 0/4,964) | passes |

The candidate fails step 2, so it is not promoted and lin-v0 stays the default (D-62). The owner can overrule at
step 5 (Q-09).
