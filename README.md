# Fox and Hounds

A desktop Fox and Hounds game in C# on .NET 10, for boards from 4×4 to 16×16, with an alpha-beta AI, an
exact solver that grades it, a reproducible simulation harness and an automatic weight tuner.

- Specification: [docs/spec/spec.md](docs/spec/spec.md) (original: `docs/spec/*.docx`)
- Decisions and recorded values: [docs/DECISIONS.md](docs/DECISIONS.md)
- Plan and status: [docs/PLAN.md](docs/PLAN.md)
- Reference prototype (the source of every test number): [reference/fh_proto.py](reference/fh_proto.py)

## Layout

```
src/FoxHounds.Core      rules, state, moves, RNG, Zobrist, notation, game records, IAgent, GameRunner
src/FoxHounds.Engine    evaluation features and evaluators, alpha-beta search, MinimaxAgent, RandomAgent
src/FoxHounds.Solver    exact solver tables, SolverAgent
src/FoxHounds.Agents    AgentFactory and agent specs (one factory for app, CLI and experiments)
src/FoxHounds.Sim       match runner, opening sets, statistics, experiments
src/FoxHounds.Tuning    regression and SPSA weight tuners
src/FoxHounds.Cli       the `fh` command line tool
src/FoxHounds.App       the desktop app (Raylib-cs)
tests/                  one xUnit v3 test project per library
bench/                  BenchmarkDotNet suites
reference/              Python reference prototype and its outputs
```

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test --filter "Category!=Slow"
```

See [docs/PLAN.md](docs/PLAN.md) for the full command list.
