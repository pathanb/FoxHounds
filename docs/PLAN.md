# Plan and status

The working plan for building the [specification](spec/spec.md). Decisions live in
[DECISIONS.md](DECISIONS.md). Anyone (or any new session) picking up the work starts here.

## Milestones

| Milestone | Scope | Status |
|-----------|-------|--------|
| M0 Setup | solution skeleton, build rules, package pins, banned APIs, CI workflow, T-ARCH-01 | done (e6eaf99) |
| M1 Rules core | Geometry, RuleSet, Rules, GameState, Move, Zobrist, Rng, notation, GameRunner, RandomAgent, `fh perft`; T-CORE-01…08 | done (dbae502, after review fixes) |
| M2 Solver | table build, files, SolverAgent, `fh solve`; T-SOL-01…07 | done (dbae502, after review fixes) |
| M3 Search and evaluation | features, evaluators, weight files, search stages A, B and C (D-37), MinimaxAgent, EpsilonAgent, SolverEvaluator, `fh analyse`; T-ENG-01…04, T-ENG-06, T-EVAL-01…03 | done (483fc43, after review fixes) |
| M4 Desktop app | GameSession, Raylib view, presets, play, undo/redo, save/load; T-APP-01 | done (be7f77c) |
| M5 Simulation harness | openings and suites, experiment modes, statistics, `fh openings/suite/match/grade/report`; T-ENG-05, T-SIM-01…03; 8.9 baseline | done (see log; targets await Q-08) |
| M6 Tuner | regression tuner, SPSA, `fh tune`, promotion protocol; T-TUNE-01…02 | not started |
| M7 Replay and analysis | viewer, solver colouring, analyse, play from here | not started |
| M8 Hardening | n = 10 solver, self-contained builds, user guide | not started |

A milestone is done when its tests from spec 15.1 pass, its numbers match the reference prototype, the
format check is clean, and its commit is recorded in the table above.

## Working agreements

- Spec sections 3, 5 and 6 are normative. Gaps go into DECISIONS.md before code.
- `reference/fh_proto.py` is the oracle for every number in the spec. If C# disagrees, investigate both
  sides before changing either, and never edit the spec's numbers to match the code.
- Independent review at M1, M2, M3 (D-14).
- Long experiments run in resumable chunks: background processes do not survive between sessions here.

## Commands

```bash
dotnet build                                    # Debug build, warnings are errors
dotnet build -c Release
dotnet test --filter "Category!=Slow"           # fast tests
dotnet test                                     # everything, including Category=Slow
dotnet test --coverlet --coverlet-output-format cobertura   # coverage into ./TestResults
dotnet format --verify-no-changes               # format check, as in CI
dotnet run --project src/FoxHounds.Cli -c Release -- perft --n 8 --depth 10
dotnet run --project bench/FoxHounds.Bench -c Release -- --filter '*'
dotnet run --project src/FoxHounds.App -c Release             # the game; --data DIR, --load FILE.fhg, --size WxH
fh openings --n 8 --plies 6-16 --count 1000 --seed 7 --label --filter HoundsWon --out openings.txt
fh suite --n 8 --seed 11 --max-games 10000000 --out-dir experiments/suites/n8   # D-28 suite, about 15 min on 2 cores
fh match experiments/m5-hounds-conversion.json --out runs/m5-hounds        # rerun the same command to resume
fh grade experiments/m5-grade-lin-v0.json --out runs/m5-grade
fh report runs/m5-hounds [runs/other]
python3 reference/fh_proto.py all               # reference tables, about 4 minutes
```

## Fresh workspace setup (Claude sessions)

The .NET SDK does not persist between sessions. The owner's allowlist permits `builds.dotnet.microsoft.com`
and `api.nuget.org`.

```bash
curl -sSL -o /tmp/dotnet-install.sh https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

The desktop app can be run headless for screenshots; the options are listed in `src/FoxHounds.App/Program.cs`:

```bash
xvfb-run -a -s "-screen 0 1700x1100x24" src/FoxHounds.App/bin/Release/net10.0/FoxHounds --data /tmp/fh \
    --screenshot /tmp/shot.png --n 8 --moves "e1-d2 b8-c7" --select d2      # or --fox Hard --hounds Hard --frames 600
```

## Manual checks (spec 16.1)

What could be checked without a screen was checked headlessly; the owner's hands-on pass is still to do.

| Item | Checked so far |
|------|----------------|
| 1. Four end reasons, human vs human | T-APP-01 plays each reason through the session; not yet by hand |
| 2. Every preset plays both sides; the window stays responsive | AI-vs-AI runs of all four presets finished their games; slowest frames after start 35–42 ms (Xvfb software rendering, 2 vCPUs) |
| 3. Undo during a search cancels within 100 ms | T-APP-01 with the real search |
| 4. ChooseOnBackRow, human and AI fox | screenshot (human) and T-APP-01 (both) |
| 5. Sizes 4, 7, 8, 12, 16 readable at 800×600 | screenshots checked |
| 6. Save, quit, relaunch, load | headless save then `--load` relaunch, and T-APP-01 |
| 7. AI vs AI at every delay; Pause stops after the current ply | T-APP-01 with the manual clock |
| 8. Resize mid-game | layout tests at 25 window and board sizes; not yet by hand |

## Log

- 2026-11-05: spec reviewed against the prototype; all reference numbers reproduced. Seven issues raised
  and decided (D-13 … D-19), plus smaller decisions D-20 … D-31.
- 2026-11-05: M0 done. Tag pushes are rejected by this environment (HTTP 403), so milestone commits are
  recorded here instead of as tags.
- 2026-11-05: M1 implemented: all T-CORE tests pass (perft, reachable rules, Zobrist, notation, RNG vectors,
  game length); Core line coverage 93%. perft(10) = 16 ms. Independent review pending (D-14).
- 2026-11-05: M2 implemented. Every byte of the 4×4…8×8 tables (both win conditions) agrees with tables built
  independently by the prototype (reference/solver_table_digests.txt); 800 random-position values agree; 10×10
  reproduces the published "hounds win in 72". 8×8 build 149 ms single-threaded; Solver coverage 97%.
- 2026-11-05: independent reviews of M1 and M2 found no critical defects (rules and solver values agree with the
  prototype everywhere); 5 + 8 minor/major findings fixed with regression tests (D-32 … D-35). The reviews were
  interrupted once by the plan's 5-hour usage limit and resumed after it reset.
- 2026-11-05: M3 implemented. Alpha-beta equals plain minimax at depths 1–6 on 10,000 positions; exhaustive search
  equals the solver on every reachable 4×4 position and 5,000 late 8×8 positions; TT on/off agree on proven
  scores; features equal the prototype on 900 random positions of six board sizes. From the 8×8 start a 15.7M-node
  search proves "hounds win in 44" in about 2.3 s. Evaluation is ~19% of search time (SHOULD 10%; see 17.3).
- 2026-11-05: independent review of M3 found no wrong values (features, search against the solver with warm
  tables, budgets, allocation all checked) but two major findings: RandomAmongBest reported the best move's line
  and score for a different played move, and T-ENG-03/04 could not catch a broken TT score conversion or a missing
  D-39 check (shown by mutation). Fixed with D-40 … D-43; each new test was checked to fail on its mutant.
- 2026-11-05: M4 done. The app plays human and AI games on 4×4–16×16 with the 13.3 presets and custom specs,
  undo/redo, save/load and AI-vs-AI with pause and delay (D-44 … D-52). T-APP-01 found one real bug, the source
  generator filling init-only properties (D-52), which had broken the Easy and Medium presets. Coverage:
  GameSession 93%, Layout 97%, Agents 91%; the Raylib drawing code is checked with headless screenshots instead.
- 2026-11-05: M5 done. The simulation harness runs all four modes with deterministic, resumable output (T-SIM-01
  for 1 and 8 workers, T-SIM-02 by cancellation and by game cap, D-53 … D-59). The first competence baseline
  (D-60, docs/benchmarks/M5-competence.md) meets every 8.9 target at 2,000,000 nodes, with no error; mistakes appear
  below about 100,000 nodes. The D-28 suite came out at 34,370 / 6,872 / 13,757 positions, since some strata are
  rare. Open for the owner: Q-08, which targets to keep.
