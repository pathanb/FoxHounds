# Plan and status

The working plan for building the [specification](spec/spec.md). Decisions live in
[DECISIONS.md](DECISIONS.md). Anyone (or any new session) picking up the work starts here.

## Milestones

| Milestone | Scope | Status |
|-----------|-------|--------|
| M0 Setup | solution skeleton, build rules, package pins, banned APIs, CI workflow, T-ARCH-01 | done (`m0`) |
| M1 Rules core | Geometry, RuleSet, Rules, GameState, Move, Zobrist, Rng, notation, GameRunner, RandomAgent, `fh perft`; T-CORE-01…08 | not started |
| M2 Solver | table build, files, SolverAgent, `fh solve`; T-SOL-01…07 | not started |
| M3 Search and evaluation | features, evaluators, weight files, search stages A and B, MinimaxAgent; T-ENG-01…04, T-EVAL-01…03 | not started |
| M4 Desktop app | GameSession, Raylib view, presets, play, undo/redo, save/load; T-APP-01 | not started |
| M5 Simulation harness | stage C search, openings and suites, experiment modes, statistics, `fh match/grade/report`; T-ENG-05…06, T-SIM-01…03 | not started |
| M6 Tuner | regression tuner, SPSA, `fh tune`, promotion protocol; T-TUNE-01…02 | not started |
| M7 Replay and analysis | viewer, solver colouring, analyse, play from here | not started |
| M8 Hardening | n = 10 solver, self-contained builds, user guide | not started |

A milestone is done when its tests from spec 15.1 pass, its numbers match the reference prototype, the
format check is clean, and it is tagged `mN`.

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

The desktop app can be run headless for screenshots with `xvfb-run -a -s "-screen 0 1280x800x24" …`.

## Log

- 2026-11-05: spec reviewed against the prototype; all reference numbers reproduced. Seven issues raised
  and decided (D-13 … D-19), plus smaller decisions D-20 … D-31.
