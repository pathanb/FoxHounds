# Fox and Hounds

A desktop Fox and Hounds game in C# on .NET 10, for boards from 4×4 to 16×16, with an alpha-beta AI, an
exact solver that grades it, a reproducible simulation harness and an automatic weight tuner.

- Specification: [docs/spec/spec.md](docs/spec/spec.md) (original: `docs/spec/*.docx`)
- Decisions and recorded values: [docs/DECISIONS.md](docs/DECISIONS.md)
- Plan and status: [docs/PLAN.md](docs/PLAN.md)
- Reference prototype (the source of every test number): [reference/fh_proto.py](reference/fh_proto.py)
- User guide: [docs/user-guide.md](docs/user-guide.md)

## Layout

```
src/FoxHounds.Core      rules, state, moves, RNG, Zobrist, notation, game records, IAgent, GameRunner
src/FoxHounds.Engine    evaluation features and evaluators, alpha-beta search, MinimaxAgent, RandomAgent
src/FoxHounds.Solver    exact solver tables, SolverAgent, move grading
src/FoxHounds.Agents    AgentFactory and agent specs (one factory for app, CLI and experiments)
src/FoxHounds.Sim       match runner, opening sets, statistics, experiments
src/FoxHounds.Tuning    regression and SPSA weight tuners
src/FoxHounds.Cli       the `fh` command line tool
src/FoxHounds.App       the desktop app (Raylib-cs)
tests/                  one xUnit v3 test project per library
bench/                  BenchmarkDotNet suites
reference/              Python reference prototype and its outputs
tools/app-checks/       drives the app on a virtual display for the manual checks
tools/publish.sh        builds the release folders
```

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test --filter "Category!=Slow"
```

## Release builds

`tools/publish.sh` makes self-contained folders for Windows (win-x64), Linux (linux-x64) and macOS on Apple silicon
(osx-arm64) under `artifacts/release`, each with the game, the `fh` tool and the user guide. They run without .NET
installed.

See [docs/PLAN.md](docs/PLAN.md) for the full command list.
