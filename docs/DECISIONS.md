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

Desktop app (M4):

**D-44 Panel and game settings (completes 13.2, 13.4).** 13.2 lists the panel from the player cyclers down, but a
new game also needs a board size, a rule variant and a fox start. Three cyclers above the players set them: Board
(4–16), Rules (Classic or Gilith; a loaded record that matches neither shows "Custom") and Fox start (fixed or fox
chooses). Changing one starts a new game at once with the same players, so the panel always describes the game on
the board; the settings are read from the session's rules each frame, with no second copy. The buttons are New game,
Undo, Redo; Play/Pause and the delay (shown in seconds); Save and Load.

**D-45 Player choices (completes 13.3).** Each side cycles through Human, the four presets (`presets/*.json` next to
the executable) and one entry per agent/1 file in `{data}/agents` ("Custom: name"; a file that does not parse is
skipped). That list stands in for "Custom… (loads an AgentSpec JSON)", as there is no native file dialog (13.4).
Perfect is offered on every board the solver supports (n ≤ 11): tables up to 8×8 load with each game, and 9×9–11×11
tables are built on first use with progress on the evaluation line, as Q-07 says. On 12×12 and larger a Perfect
player becomes Human, with a notice. A loaded record's players are matched to these choices by their spec JSON.

**D-46 Changing a player mid-game (refines 13.3).** "Takes effect at that side's next turn" is read literally: a
search already running for that side completes and its ply is played, and the old agent is then dropped. A side
waiting for a human gets the new player at once, since its next move request is now.

**D-47 Undo and redo edge cases (refines 13.4).** With a human playing, Undo goes back to the latest earlier
position with a human to move. When there is none (an AI opened and only the other side is human), Undo is
disabled rather than letting the AI replay its first move. The fox's placement under ChooseOnBackRow is not a ply
and is not undone; New game repeats it. Undo and Redo cancel a running search first, and a cancelled search's late
answer is discarded.

**D-48 AI failures in the app (refines 7.3 for the app).** An illegal ply or a fox start off the back row forfeits,
as in the game runner. An agent that throws (a bug, or a custom spec whose weight file fails to load) does not: the
game pauses with a message, and that side is not asked again until the user changes the player, undoes, redoes,
resumes or starts a new game. Asking again every frame would only repeat the error, and the user can still rescue
the game. The game runner keeps the 7.3 forfeit.

**D-49 Status line and game-over banner (refines 13.2, 13.4).** Text is ASCII only, because raylib's built-in font
has no em dash: "thinking - depth 9, 1.2M nodes". Notices (saved, loaded, a player reset) show for 4 s; problems stay
until the next action. The game-over banner (winner and reason; New game, Save and Replay, which waits for M7) sits
in the half of the board away from the fox, so the final position stays visible, and Esc or a click on the board
hides it. The evaluation line shows a proven but inexact AI score as an upper bound (D-41) and drops the solver
verdict once the game is over.

**D-50 Files and folders (completes 13.4, 14).** The data folder is `{LocalApplicationData}/FoxHounds`
(`%LOCALAPPDATA%\FoxHounds` on Windows, `~/.local/share/FoxHounds` on Linux), with `tables/`, `games/` and
`agents/`; `--data DIR` replaces it. A saved game is named after its start time in UTC
(`2026-11-05T12-00-00Z.fhg`), and saving the same game again replaces its file. `--load FILE` opens a record at
start. Records store `"exact": true` in agent info when D-41 says so; older readers ignore the field (14).

**D-51 Rendering structure (refines 13.1).** `RaylibView` reads the `GameSession` directly rather than a copied
snapshot. Every change to the session happens on the main thread, in `Update` and the input handlers, so a frame
always sees one consistent state; only the AI's progress counters are read across threads, with `Volatile`. Only
`Program`, `AppController`, `InputController`, `RaylibView` and `Ui` use Raylib. `GameSession` and `Layout` do not,
and are unit-tested (T-APP-01).

**D-52 JSON spec types use set accessors.** System.Text.Json's source generator fills every init-only property of a
type, so a field missing from the JSON got default(T) instead of its initializer: `rootSelection` became null, which
broke the Easy and Medium presets (found by T-APP-01). The parts of agent/1 specs therefore use `set` accessors, and
a test pins their defaults. Agent specs and game records also reject duplicate keys, like weight files (D-42).

Simulation harness (M5):

**D-53 Experiment specs (completes 14.3).** Each entry under `agents` is a path to an agent/1 file or an inline
agent/1 object. Roles per mode: `candidate` and `reference` (vsReference), `a` and `b` (headToHead), `candidate` and
`opponents` (a list; gauntlet), `candidate` (grade, whose positions come from `suite`). The spec hash covers the
rules, seed, mode, agents (with the SHA-256 of any weight file), the positions file's content, the per-side budgets,
the SPRT settings and the record policy, but not `workers`, `maxGames` or `maxMemoryMb`: a run can be resumed with
more workers, or grown by resuming with a larger cap. An agent spec without `ttMegabytes` takes the context's
default, 16 MB in experiments (11.3) and 64 MB in the app. `keepTtBetweenGames`, like a soft time limit, needs
`allowNonDeterministic`, since the results would depend on which worker played which game. `sprt` is for headToHead.

**D-54 Output in game order (refines 11.3).** Games are appended to games.jsonl in game-index order: a game that
finishes early waits in memory until the games before it are written. So games.jsonl is the same for every worker
count, a stopped run leaves a contiguous prefix, and a resume simply continues it; a cut-off last line is dropped
and that game is played again. The SPRT is checked on pairs in that order, so it stops at the same pair with any
worker count, and games past the stopping point are discarded. Records are written by the same writer, and
summary.json is rewritten after every session, finished or not.

**D-55 Line and summary fields (completes 14.4, 11.4).** Each line adds `foxRole` and `houndsRole`, `wonPlies` and
`moves` per side (the denominators of the blunder rate and nodes per move) and `forfeit`. summary.json holds, per
agent and side: games, wins, conversion, blunders (successes = blunders, trials = plies made in won positions),
moves, nodes, nodes per move and forfeits, every proportion with its Wilson interval; per head-to-head pairing:
pairs, score, Elo and their intervals, and the SPRT state; in grade mode: accuracy and resistance per side. Numbers
are rounded to 6 decimals, and an infinite Elo is written as the string "inf" or "-inf".

**D-56 Opening and suite files (completes 11.2, 14.5).** The ruleset comment is written as JSON
(`# ruleSet={…}`); the 14.5 form (`classic adjudicateEscape=false`) is read as well. Loading checks the board size
and win condition against the experiment's. Labels are the solver's, solved with adjudication off (10.6); an
experiment that adjudicates escapes gets the same winners (T-SOL-04). The opening generator draws from one stream
seeded by `--seed`, picks each opening's length uniformly from the range, uses the start squares in turn under
ChooseOnBackRow, and checks duplicates before the filter. Suites (D-28) use equal-width phase buckets of
HoundMovesLeft (0–7, 8–14, 15–21, 22–28 on 8×8), draw one position per game uniformly from the positions before the
end, and split by Zobrist hash, so a position can only ever be in one split. The late bucket is rare, about one game
in 200 yields a position for it, so the full 80,000-position suite takes millions of games.

**D-57 Grade mode (completes 11.1, 11.5).** The candidate chooses one ply per suite position, after `NewGame` with
the stream of the position's index. Accuracy counts won positions where the ply keeps the win; resistance counts
lost positions where its loss distance equals the longest available. A suite label that disagrees with the solver
stops the run: the suite was made for other rules.

**D-58 Forfeits in experiments (refines 7.3, D-21).** A watchdog timeout stops the run with an error, as D-21
says. Other forfeits (an agent that throws or plays an illegal ply) are results like any other: the summary counts
them per side, and the run goes on.

**D-59 T-ENG-05 golden file.** 50 positions from random play whose result is not proven at the searched depth: 30
on 8×8 at depth 10, 10 on 10×10 at depth 8, and 10 on 16×16 at depth 7, the 128-bit path. lin-v0 with a 4 MB
table; 331,777 nodes in all. The file is regenerated only on purpose, with `FH_WRITE_GOLDEN=1`, and the commit says
why.

**D-60 First competence baseline, and what it means for M6 (8.9, amends D-28).** Measured on 8×8 Classic
against the solver (details in docs/benchmarks/M5-competence.md). At the default UI budget of 2,000,000 nodes per
ply, lin-v0 converts 1,000 of 1,000 hounds-won openings and every fox-won test position and opening, with no
blunder in 17,158 hound plies and 7,793 fox plies made in won positions. The provisional targets are met with room
to spare and cannot tell agents apart at that budget. The hounds' searches average 202,000 nodes per move: most
stop early on a proof (D-39). lin-v0 first makes mistakes below about 100,000 nodes: hounds conversion is 89.6% at
1,000 nodes, 98.4% at 5,000 and 98.8% at 20,000. Grade accuracy at 20,000 nodes (the SPSA objective of D-19) is
99.73% on the test split, 22 errors in 8,074 won positions, too few to steer a tuner. Until the owner agrees
targets (open question Q-08), measurements for M6 use 1,000–20,000 nodes per ply, where the errors are. The fox
side hardly tests the evaluator in Classic, because fox wins are mostly escapes within a few plies.

D-28 is amended by the suite run. In 10,000,000 games (28 minutes on 2 cores) the equal quotas could not all be
filled. The late phase (HoundMovesLeft 0–7, both winners) and the hounds-won strata of phases 1 and 3 ran short:
hound wins usually end, and fox wins escape, before the hounds use up their moves, and early hounds-won positions
are few and repeat. The suite keeps what was found: train 34,370, validation 6,872, test 13,757. It is committed
under `experiments/suites/n8/` and can be regenerated exactly with the command in its header.

Tuner (M6):

**D-61 Tuning specs and outputs (completes 12).** A tuning spec (format tuning/1) names the id, seed, ruleset
preset and board size, the `tuner` (`regression` or `spsa`), the `evaluator` (`linear` or `tapered`), the output
`name`, and the `train` and `validation` position files. An SPSA run may also name its `start` weights (default
lin-v0; a linear start seeds both halves of a tapered run). Sections `regression` (k, lambda, learningRate,
maxSteps, patience) and `spsa` (iterations, a, c, stability, targetStep, validateEvery, per-weight `scales`,
`objective`, batch, budget, openings, pairs, workers) override the 12.2 and 12.3 defaults. SPSA budgets are nodes or
depth only. There is no test field: tuners never read the test split (12.1). `fh tune SPEC --out DIR` writes:
- `weights.json`, with the board size, and the run id as provenance;
- `report.json`;
- `run.json`: the spec, its SHA-256, the commit, and the SHA-256 of every input file;
- `tuning.csv`, for SPSA.

The regression starts from β = 0 and sums rows in order on one thread, so it is deterministic. Its reported loss
and accuracy are those of the rounded weights with the fitted intercept, and the loss without the intercept is
reported too. Dropping the intercept shifts every evaluation by the same amount, which changes no search decision.
The GradeAccuracy batch is drawn without replacement from the won train positions; each position is searched with
a fresh 1 MB table, so the score does not depend on the order. The HeadToHead objective validates by playing the
weights against the start weights from the validation positions, and is only smoke-tested (D-19).

**D-62 First tuning results and the first promotion attempt (12.4, M6).** Details are in
docs/benchmarks/M6-tuning.md.
- **Regression fits** of the 34,370 train positions predict the solver's winner well (validation accuracy 83.3%
  linear, 84.8% tapered), but they play no better than lin-v0: 84.2% and 87.9% hounds conversion at 1,000 nodes,
  against 89.6%.
- **SPSA** from lin-v0 (GradeAccuracy at 1,000 nodes, per D-60) gave lin-s1-n8. The protocol ran at 1,000 nodes,
  the budget it was tuned for, except step 4:
  - Step 2 fails narrowly: 98.37% [98.06, 98.62] against 97.80% [97.45, 98.09]. The intervals overlap by 0.03 pp,
    and the 20,000-position route needs a larger test split than D-60's 13,757 positions.
  - Step 3 passes: SPRT accepted H1 after 530 pairs, Elo +11 [+2, +20].
  - Step 4 passes: 100% conversion on both sides, no blunders.

lin-s1-n8 is therefore not promoted, and lin-v0 stays the default. No default-weights lookup (D-18) is built until a
file is promoted. The owner decides step 5 and can overrule (Q-09).

Replay and analysis (M7):

**D-63 The replay and analysis viewer (completes 13.5; amends D-49 and D-51).**

- **Opening it.** The viewer opens on the current game: the one just finished, or a record loaded with Load. It is
  reached from the Replay button in the panel or in the game-over banner, and `--replay FILE` opens a record
  straight in it. It opens at the last ply.
- **The game stands still.** While the viewer is open, a running search is cancelled, no AI turn starts, and board
  clicks, undo and redo do nothing. Back to game (or Esc) resumes the game. Loading or starting a game closes the
  viewer.
- **Moving.** ←/→ step one ply, with key repeat. Home and End jump to either end. Clicking a ply in the move list
  shows the position after it.
- **The panel** describes the ply that led to the shown position: its number, the solver's class for it, the stored
  agent info (score, depth, nodes and time, or human, solver or random), and the line. It also shows the position in
  5.6 notation; Ctrl+C copies it for `fh analyse`.
- **Solver colours.** Every legal ply of the shown position gets a marker in its target square.
  - The marker is shifted towards the moving piece, so two hounds that can reach one square get two markers.
  - The ply the game went on with is ringed. Without a table, that next ply is drawn in blue instead.
  - Distances are the table's, which is solved without escape adjudication (spec 10.1). Adjudication only ends
    games the fox has already won, so it never changes who wins after a ply, and red means the same under both
    settings. MoveGradingTests check this after every ply of every 6×6 position (both win conditions, adjudication
    on and off) and every 7×7 Classic position.
  - Hovering a square shows its plies with their values.
- **Blunders** are marked "??" with the harness's definition, through the same function (D-64).
- **Analyse** runs a fresh search of the shown position: lin-v0, its own 64 MB table cleared first, the best root
  move, a 2 s hard limit. Stop keeps the deepest completed iteration. Moving to another position cancels the search
  and drops its result. Each finished result stays with its position while the viewer is open.
- **Play from here** starts a new game from the shown position, under the same rules, with the players now in the
  cyclers. The cyclers stay usable in the viewer. The button is disabled on a finished position.
- **Drawing (amends D-51).** The board is now drawn from a `BoardView` snapshot that the session builds, the same
  for play and replay; the panel still reads the session.
- **Play panel (amends D-49).** Its bottom row is now Save, Load and Replay. The Ctrl+S and Ctrl+O hints left the
  labels to make room.
- **Checklist item 9.** Its 57-ply game is the longest an 8×8 game can last (2 · 4 · 7 + 1). The Classic rules
  cannot produce one: before the last hound steps onto the bottom row, the fox would have to stand on that row's one
  free square to avoid counting as escaped, and that is the square the hound needs. The check therefore uses a
  Gilith game (docs/benchmarks/M7-viewer.md).

**D-64 Shared move grading, `fh analyse --solver`, and weight paths in records.**

- **One grading function.** `MoveGrading`, in FoxHounds.Solver, holds the viewer's four classes and the 11.5 blunder
  judgement. The harness's per-game metrics now call the same `Judge`, so the "??" markers and the games.jsonl
  counts cannot drift apart. A rerun of an M6 experiment after the switch gave a byte-identical summary.
- **`fh analyse --solver`** now also lists every legal ply with its value after the ply (counting the ply) and its
  colour, for the hand check of item 10. `--rules classic|gilith` was added; the command had used the Classic win
  condition only.
- **Weight paths in records.** Records now store a player's weight file as an absolute path
  (`AgentSpec.WithAbsolutePaths`). A record has no folder for a relative path to resolve against, so a recorded
  player with its own weights could not be rebuilt, for example for Play from here. The app matches recorded players
  to its presets in that form. Records written earlier keep their relative paths; replaying them is unaffected.

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
| Q-08 | _Open._ After the M5 baseline (D-60): which 8.9 targets should stand, given that lin-v0 meets all three with no error at 2,000,000 nodes? |
| Q-09 | _Open._ lin-s1-n8 passes promotion steps 3 and 4 but misses step 2 by 0.03 pp of interval overlap (D-62). Keep lin-v0 (the protocol as written), promote lin-s1-n8, or enlarge the test split and grade again? |

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
| M5 | competence baseline for lin-v0 (8.9) | At 2,000,000 nodes per ply, 8×8 Classic vs solver: hounds conversion 100.0% [99.62, 100] (1000/1000 hounds-won D-15 openings); fox conversion 100.0% [99.62, 100] from 1,000 fox-won test-split positions and from 1,000 fox-won openings; blunders 0/17,158 (hounds) and 0/7,793 (fox). Budget sweep and grade results in docs/benchmarks/M5-competence.md; see D-60 |
| M5 | agreed 8.9 targets | _awaiting the owner (Q-08)_; proposed: keep the three targets at 2,000,000 nodes, and track the hounds conversion at 20,000 nodes (98.8% for lin-v0) as the development metric |
| M6 | promoted weight files and their experiment ids | None yet. Candidate lin-s1-n8 (`m6-spsa-lin-n8-1k`; promotion runs `m6-promotion-*`) failed step 2 of 12.4 (D-62); the decision awaits Q-09 |
