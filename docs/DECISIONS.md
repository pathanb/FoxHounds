# Decisions, open questions and recorded values

This file is Section 17 of the [specification](spec/spec.md). Anything the spec does not cover, or gets
wrong, is decided here **before** it goes into code. Where a decision below amends a spec section, the
decision wins.

## 17.1 Decision log

D-01 to D-12 are the spec's own decisions, unchanged:

| ID   | Decision (summary)                                                                    |
|------|---------------------------------------------------------------------------------------|
| D-01 | C# on .NET 10 LTS                                                                     |
| D-02 | Raylib-cs with three home-made widgets, no raygui                                     |
| D-03 | Hounds with no legal ply lose, under both win conditions                              |
| D-04 | Default ruleset Classic: fox on e1, fox first, fox wins on reaching row 0             |
| D-05 | H = ⌊n/2⌋, n = 4…16, odd n allowed                                                    |
| D-06 | Rules is a concrete class; interfaces only at strategy points (IAgent, IEvaluator, IClock) |
| D-07 | Immutable 32-byte GameState struct; ply count kept outside it                         |
| D-08 | Escape adjudication on by default for play and experiments, always off in the solver  |
| D-09 | Experiments use node or depth budgets and our own RNG only                            |
| D-10 | No null-move pruning                                                                  |
| D-11 | Solver = full-table potential-ordered sweep, 1 byte per position                      |
| D-12 | Humans are not IAgents; the app handles their turns                                   |

Decisions taken during the project (owner accepted all recommendations on 2026-11-05):

**D-13 Project references (amends 4.2).** One `AgentFactory` must build minimax agents (Engine) and solver
agents (Solver) for the app, the CLI and experiments, while Engine and Solver stay independent and the App
may not reference Sim. A new library, `FoxHounds.Agents`, holds the factory, AgentSpec loading and
`SolverEvaluator`. Test projects may also reference the libraries their oracle tests compare against.
T-ARCH-01 enforces exactly this table:

| Project                | May reference                               |
|------------------------|---------------------------------------------|
| FoxHounds.Core         | nothing                                     |
| FoxHounds.Engine       | Core                                        |
| FoxHounds.Solver       | Core                                        |
| FoxHounds.Agents       | Core, Engine, Solver                        |
| FoxHounds.Sim          | Core, Engine, Solver, Agents                |
| FoxHounds.Tuning       | Core, Engine, Solver, Agents, Sim           |
| FoxHounds.Cli (`fh`)   | all libraries                               |
| FoxHounds.App          | Core, Engine, Solver, Agents                |
| FoxHounds.Bench        | Core, Engine, Solver                        |
| FoxHounds.Core.Tests   | Core                                        |
| FoxHounds.Engine.Tests | Engine, Core, Solver, Agents (T-ENG-03)     |
| FoxHounds.Solver.Tests | Solver, Core                                |
| FoxHounds.Agents.Tests | Agents, Core                                |
| FoxHounds.Sim.Tests    | Sim, Core                                   |
| FoxHounds.Tuning.Tests | Tuning, Core                                |
| FoxHounds.App.Tests    | App, Core (T-APP-01 tests GameSession)      |

Nothing else references App or Cli.

**D-14 Process (amends 4.3 CI and 16.2).** One developer (Claude) commits directly to `main` and records the
commit of each finished milestone in PLAN.md (this environment's GitHub access rejects tag pushes with HTTP 403,
so there are no milestone tags on GitHub). The GitHub Actions workflow exists but runs only when started by hand.
Performance floors are measured on the development workspace (2 vCPU, 7 GB RAM, Linux x64); this answers
Q-04. Instead of review by a second person, M1, M2 and M3 get an independent review by a fresh agent that
has not seen the author's reasoning; other milestones are self-reviewed.

**D-15 Opening sets (amends 11.2 and the 8.9 procedure).** `--plies` takes one value or a range `lo-hi`;
each opening's length is drawn uniformly from the range. Competence runs use `6-16`. Evidence
(`reference/probes/`): after exactly 6 plies there are only 544 distinct positions, 311 of them hounds-won,
so 1,000 or 2,000 distinct 6-ply openings cannot exist. The premise in 11.2 is also reversed: 69.5% of
uniform random 6-ply openings are hounds-won. The noisy-solver generator gives even fewer distinct positions
(about 174 hounds-won after 10⁶ draws). Lengths 6–16 give 12,363 distinct hounds-won positions. The
`noisySolver` generator stays available but is not the default.

**D-16 SPSA update (amends 12.3).** The update runs in scaled coordinates:
θᵢ ← θᵢ + a_k · sᵢ · (J⁺ − J⁻) / (2 · c_k · Δᵢ); the perturbation stays c_k · sᵢ · Δᵢ. Unless a tuning spec
sets `a` explicitly, the gain is calibrated before iteration 0 (Spall 1998): ḡ = mean of |J⁺ − J⁻| / (2c₀)
over 10 trial perturbations, then a = TargetStep · (A + 1)^0.602 / ḡ with TargetStep = 1, so the first
steps move each weight by about one scale unit. Δ and the batch of iteration k come from one
`Rng.ForGame(seed, k)` stream, Δ first. Evidence: with the original defaults (a = 1, A = 50, c = 2) and an
objective in [0, 1], a weight moves less than one unit over 500 iterations even when every step agrees, so
rounding returns the starting weights; and dividing by sᵢ made large weights move slower, the opposite of
the stated intent.

**D-17 Regression tuner scale (amends 12.2).** Optimise β = w / K (logit units): loss = mean cross-entropy
+ λ‖β‖² (λ = 1e−4, intercept not penalised), Adam on β with learning rate 0.01; output wᵢ = round(K · βᵢ).
Evidence: on raw weights the penalty for lin-v0 alone is 7.0, against a cross-entropy that cannot exceed
ln 2 ≈ 0.69 for a useful model, and Adam at 0.01 per step can move a raw weight only about 200 units.

**D-18 Weight files and board size (amends 9.3 and 13.3).** `lin-v0` carries no `boardSize` (it is hand-set
and applies to every n). Tuned files carry their `boardSize`. App presets use the default tuned file for the
board size when one exists, otherwise `lin-v0`.

**D-19 Compute budgets.** The SPSA GradeAccuracy objective grades with `maxNodes` 20,000 per position unless
the tuning spec says otherwise. The HeadToHead objective is implemented but only smoke-tested (a default run
is about 100,000 games). Long experiments run in resumable chunks. Competence baselines use the specified
2,000,000 nodes per ply.

**D-20 Determinism comparisons (amends T-SIM-01, T-SIM-02).** Wall-clock fields (`ms` in games.jsonl,
`startedUtc` and `info.ms` in records, `AgentInfo.Elapsed`) are excluded when comparing runs.
summary.json contains no wall-clock values.

**D-21 Time limits in experiments (amends 11.3).** Agent specs may carry `hardTimeMs`. In an experiment it
is only a watchdog: a game that ends in a timeout forfeit aborts the experiment with an error, so every
completed run is reproducible. `softTimeMs` in an experiment still requires `allowNonDeterministic`.

**D-22 Where budgets come from (amends 7.1 and 14.3).** The budget passed to `ChooseMove` is the AgentSpec's
`budget`. An experiment spec may override it per role with `"budgets": { "fox": {…}, "hounds": {…} }`.

**D-23 One random stream per agent (amends 7.3).** `GameRunner` gives each agent its own `Rng`, split from
the game's `Rng` in the order fox, hounds. One agent's random draws can never shift the other's.

**D-24 Exact RNG derivations (completes 5.5).** Results must be reproducible bit for bit, so the derived
operations are fixed:
- `NextInt(s)`: Lemire's multiply-shift with rejection. x = NextU64(); m = x · s (128-bit); l = low64(m);
  if l < s then t = (2⁶⁴ − s) mod s and while l < t: redraw x, recompute m and l. Return high64(m).
- `NextDouble()` = (NextU64() >> 11) · 2⁻⁵³.
- `Split()` = new Rng(NextU64()).

**D-25 Geometry tables are read-only (amends the 5.1 member types).** One cached `Geometry` per n is shared
by every thread, so its tables are exposed as `ReadOnlySpan<T>` instead of public arrays.

**D-26 Analyzer exceptions.** Listed in `.editorconfig` files, each with its reason. Currently: CA1707 (no
underscores in names) and CA1861 (constant array arguments) are off in test projects, whose test names read as sentences and whose expected values sit inline; CA1051 (visible
instance fields) is suppressed on `GameState`, whose public readonly fields spec 5.2 prescribes; CA1031 (catch
general exceptions) is suppressed in `GameRunner`, where any agent exception is a forfeit by spec 7.3.

**D-27 Test platform (amends 4.1).** xunit.v3 4.0.2 (4.x is the current stable line since August 2026) on
Microsoft.Testing.Platform, enabled in `global.json`; coverage through `coverlet.MTP`. The platform's v2 no
longer supports the VSTest mode of `dotnet test` on the .NET 10 SDK.

**D-28 Position suite (completes 11.2; provisional until M5).** Positions come from games between
EpsilonAgent(ε = 0.15) wrapping MinimaxAgent(lin-v0, depth 4) on both sides, starting from D-15 openings.
One position is sampled uniformly per game, with equal quotas per phase bucket × solver winner, split by
hash into train 50,000 / validation 10,000 / test 20,000 on 8×8.

**D-29 Time APIs are banned too (extends 4.3).** BannedSymbols.txt also bans `DateTime.Now/UtcNow/Today`,
`DateTimeOffset.Now/UtcNow`, `Stopwatch` and `Environment.TickCount/TickCount64`. Only the system
implementation of `IClock` suppresses the rule.

**D-30 Rules.Create and Rules.CreateChecked (extends 6).** The solver, generators and tests need positions
built from a hound set, a fox square and a side to move. Rules creates them: `Create` asserts validity in
debug builds, `CreateChecked` validates and throws. "Only Rules creates states" still holds.

**D-31 No legal plies in a finished game (completes 6).** `IsLegal` returns false for every ply of a
position whose `Evaluate` is not Ongoing, so `ApplyChecked` rejects plies after the end of a game (needed
by the load check in 14.2). `GenerateMoves` keeps its contract from 6.1 and does not check.

Decisions from the independent reviews of M1 and M2 (2026-11-05):

**D-32 Table files carry a win-condition check (complements 10.6).** The SHA-256 covers only the payload, so a
flipped win-condition byte in the header used to load a Classic table as Gilith. `Load` now checks the payload's
own signature: with all hounds on the back row and the fox on row 0 with the fox to move, ReachHomeRow stores 0
(spec 10.3) and HoundsImmobile stores a value. Format version 1 is unchanged.

**D-33 Table store semantics (completes 10.6).** Concurrent requests for one table share one build; each caller
waits with its own cancellation token and gets its own progress reports, and the build is cancelled only when every
waiting caller has given up. A table that cannot be saved is still returned, with a warning. Saves go to a uniquely
named temporary file that is flushed to disk before the rename. When the platform reports no user data folder, the
default store uses the temp folder, so its path is always absolute.

**D-34 Runner and budget robustness (completes 7.1, 7.3).** An `InvariantViolationException` raised inside an agent
is a rules bug and propagates; it is never a forfeit. A given start position is validated and recorded before the
agents' `NewGame`. The 20% hard-time tolerance is checked without overflow. `SearchBudget` rejects negative limits
and a zero node limit (use null for "no limit"). `GameRecord.Replay` requires rules for the record's own ruleset.
`Rules.ToNotation` refuses a position of another board size; the other per-position methods assert it in debug.

**D-35 GameState.GetHashCode folds the Zobrist hash.** Equal states have equal hashes (5.2), and unlike
`HashCode.Combine` the value is the same in every process, so hash sets of positions iterate reproducibly.

Decisions taken while building M3 (2026-11-05):

**D-36 Weight-file format tag (completes 9.3 and 14).** Weight files are written with `"format": "weights/1"`;
files without a format field, like the example in 9.3, are read as weights/1. Feature names match exactly and
case-sensitively.

**D-37 Search stage C is built in M3 (amends the 8 stage table).** Node and time budgets, abort handling and both
root-selection modes ship with stages A and B, because the M4 presets need node budgets, a hard time cap and
cancellation. T-ENG-06 (cancellation and allocation) is tested in M3; T-ENG-05 (golden determinism file) stays
with M5.

**D-38 MinimaxAgent's fox start (completes 7.1).** Under ChooseOnBackRow the agent searches each start position
with `SearchOptions.FoxStartBudget` (default depth 8) and takes the best score for the fox; ties go to the first
square.

**D-39 Proven results stop iterative deepening only when they fit (refines 8.6).** The search stops early on a
proven root score only when its distance is at most the depth just completed. A proof grafted from a deeper
transposition can be longer than the current depth and might not be the shortest; deepening on until the proof fits
guarantees the exact shortest-win and longest-loss distances (T-ENG-03 and T-ENG-04).

Fixes from the independent M3 review (2026-11-05), each with a regression test in
`tests/FoxHounds.Engine.Tests/ReviewRegressionTests.cs`; every test was checked to fail on the mutant it targets:

**D-40 RandomAmongBest reports the move it plays (completes 7.1, 8.7).** The score and line were the best move's
even when another move within the margin was chosen. Every root move is searched with a full window in this mode,
so the search now keeps each root move's line and reports the chosen move's own value and line.

**D-41 Agent info says whether a proven score is exact (completes 7.1, refines D-39).** A proof carried over in the
table from an earlier, deeper search has the right winner, but its distance is only an upper bound (the review
found "fox wins in 9" where the solver says 5). `AgentInfo.Exact` is true for the solver and for search results
that are exact under D-39; `Scores.DescribeFox(score, exact: false)` shows such a proof as "Fox wins in <= 9", and
`fh analyse` uses it. `SearchResult.Exact` is documented accordingly.

**D-42 Weight files reject duplicate keys and case-misspelt fields (completes 9.3, 14).** A duplicate feature key
used to win silently, and `"boardsize"` silently dropped the board-size guard. Duplicate keys are now an error, and
so is a top-level field that differs from a known one only in letter case. Other unknown fields are still ignored,
as 14 requires; rejecting every unknown field (the reviewer's alternative) would break that forward compatibility.

**D-43 Search tests keep tables across moves (strengthens T-ENG-03, T-ENG-04).** Within one search every node is
the same number of plies from the root, so a broken 8.3 conversion cancels out and no proof outgrows the depth: the
old tests, which clear the table before each position, could not catch either bug. A differential test now plays
whole games in which each side keeps its table, with depth budgets and node budgets that abort mid-iteration, on
5×5–7×7 Classic and 6×6 Gilith; every exact result must equal the solver, every exact move must keep the solver's
value, and every other proof must have the right winner and a distance no shorter than the solver's. Further
tests check that lines are full length with the root score at their leaf, that RandomAmongBest never plays a
proven loss (on a position that has one), and that tapered scores are truncated toward zero.

## 17.2 Open questions, answered

| ID   | Answer                                                                                   |
|------|------------------------------------------------------------------------------------------|
| Q-01 | Fixed start (e1) by default.                                                             |
| Q-02 | No pass variant in v1.                                                                   |
| Q-03 | Provisional targets stand until the M5 baseline.                                         |
| Q-04 | The development workspace defines the performance floors (D-14).                        |
| Q-05 | One developer, no dates; milestones in order.                                            |
| Q-06 | Self-contained folders only.                                                             |
| Q-07 | Build tables for n = 9–11 on first use, with a progress bar.                            |
| —    | The optional PlayoutEvaluator (9.2) is not built in v1 unless the owner asks for it.     |

## 17.3 Recorded values

| Milestone | Value | Result |
|-----------|-------|--------|
| M1 | perft(10) time, Classic 8×8 | 16.0 ms (floor: 1 s). BenchmarkDotNet ShortRun, Release, .NET 10.0.14, 2-vCPU Xeon 2.1 GHz; report in docs/benchmarks/M1-perft.md |
| M1 | plies generated per second | about 205 million (perft(10) generates 3,289,142 plies, counting the leaves, in 16.0 ms) |
| M2 | solver build time, n = 8 | 149 ms single-threaded (floor: 10 s); BenchmarkDotNet ShortRun, docs/benchmarks/M2-solver.md |
| M2 | solver build time and result, n = 10 (published: hounds win in 72) | 9–10 s on 2 threads, 212 MB. Gilith: hounds win in 72 from every start (matches the published solve); Classic: hounds win in 72 (fox first) / 71 (hounds first) |
| M2 | other sizes (2 threads, Release) | n = 9: 0.6 s; Classic fox wins in 27 / 40, Gilith fox wins in 65 / 64. n = 11: 31–63 s, 655 MB; Classic fox wins in 51 / 48, Gilith fox wins in 101 / 100 (fox first / hounds first). Under HoundsImmobile every odd board is a fox win in exactly MaxPlies |
| M3 | search nodes per second at fixed depth | 10.9 M nodes/s: depth 10 on the 20 benchmark positions, 1 MB table, 9.31 ms per pass (docs/benchmarks/M3-search.md). A 15.7M-node search from the 8×8 start proves "hounds win in 44" in 2.2–2.5 s (6.4–7.0 M nodes/s) |
| M3 | evaluation cost (spec 9.4) | 50 ns per lin-v0 evaluation, 36 ns of it the flood fill (64-bit path for n ≤ 11; 86 ns before). Leaves are 35% of nodes, so evaluation is about 19% of search time in the benchmark profile: the SHOULD target of 10% is not met. A shift-based flood fill measured slower (80 ns); an evaluation cache is the next candidate, to try with the M5 harness |
| M5 | competence baseline for lin-v0 and the agreed 8.9 targets | _pending_ |
| M6 | promoted weight files and their experiment ids | _pending_ |
