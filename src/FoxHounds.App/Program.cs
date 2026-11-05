using System.Globalization;
using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Solver;
using Raylib_cs;

namespace FoxHounds.App;

internal static class Program
{
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        string dataDir = options.DataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create) is { Length: > 0 } d ? d : Path.GetTempPath(),
            "FoxHounds");
        var tables = new SolverTableStore(Path.Combine(dataDir, "tables"));
        var factory = new AgentFactory(tables, SystemClock.Instance);
        var players = LoadPlayerOptions(Path.Combine(AppContext.BaseDirectory, "presets"), Path.Combine(dataDir, "agents"));

        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint);
        Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
        Raylib.InitWindow(options.Width, options.Height, "Fox and Hounds");
        Raylib.SetWindowMinSize(Layout.MinWidth, Layout.MinHeight);
        Raylib.SetTargetFPS(60);
        Raylib.SetExitKey(KeyboardKey.Null); // Esc clears the selection; it must not close the window

        using var session = new GameSession(factory, SystemClock.Instance, Path.Combine(dataDir, "games"));
        var app = new AppController(session, players);
        if (options.Load is not null && !session.Load(options.Load))
        {
            Console.Error.WriteLine(session.Message);
        }

        if (options.Screenshot is not null)
        {
            return Screenshot(app, session, players, options);
        }

        while (!Raylib.WindowShouldClose())
        {
            app.Frame();
        }

        Raylib.CloseWindow();
        return 0;
    }

    /// <summary>Human first, then the shipped presets in difficulty order, then the user's own agent specs.</summary>
    private static List<PlayerOption> LoadPlayerOptions(string presetDir, string customDir)
    {
        var list = new List<PlayerOption> { PlayerOption.Human };
        foreach (string name in new[] { "easy", "medium", "hard", "perfect" })
        {
            string path = Path.Combine(presetDir, name + ".json");
            if (File.Exists(path))
            {
                list.Add(new PlayerOption(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name), AgentSpec.Load(path)));
            }
        }

        if (Directory.Exists(customDir))
        {
            foreach (string path in Directory.GetFiles(customDir, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var spec = AgentSpec.Load(path);
                    list.Add(new PlayerOption("Custom: " + spec.Name, spec));
                }
                catch (InvalidDataException)
                {
                    // A broken custom spec is skipped, not fatal.
                }
            }
        }

        return list;
    }

    /// <summary>Headless check mode: set up a position, render a few frames, write a PNG, exit.</summary>
    private static int Screenshot(AppController app, GameSession session, List<PlayerOption> players, Options o)
    {
        if (o.Load is null)
        {
            var ruleSet = (o.Gilith ? RuleSet.Gilith : RuleSet.Classic) with
            {
                BoardSize = o.N,
                FoxStart = o.ChooseStart ? FoxStart.ChooseOnBackRow : FoxStart.Fixed,
            };
            app.NewGame(ruleSet);
            session.SetPlayer(Side.Fox, players.Find(p => p.Label == o.Fox) ?? PlayerOption.Human);
            session.SetPlayer(Side.Hounds, players.Find(p => p.Label == o.Hounds) ?? PlayerOption.Human);
        }

        var geo = session.Rules.Geo;
        foreach (string text in o.Moves)
        {
            if (!geo.TryParseMove(text, out var move))
            {
                Console.Error.WriteLine($"not a move: {text}");
                return 2;
            }

            int to = move.To;
            session.ClickSquare(move.From);
            session.ClickSquare(to);
        }

        if (o.Select is { } select && geo.TryParseSquare(select, out int sq))
        {
            session.ClickSquare(sq);
        }

        if (o.Save)
        {
            session.Save();
        }

        app.ShowingLoadDialog = o.LoadDialog;
        var clock = SystemClock.Instance;
        var times = new double[o.Frames];
        for (int i = 0; i < o.Frames; i++)
        {
            var before = clock.Elapsed;
            app.Frame();
            times[i] = (clock.Elapsed - before).TotalMilliseconds;
        }

        // The first frames include window creation and JIT compilation; report them apart from the rest.
        int warm = Math.Min(times.Length, 5);
        double slowest = times.Skip(warm).DefaultIfEmpty(0).Max();
        string worst = string.Join(", ", times.Select((t, i) => (t, i)).Skip(warm).OrderByDescending(x => x.t).Take(3)
            .Select(x => string.Create(CultureInfo.InvariantCulture, $"#{x.i} {x.t:F0} ms")));

        var image = Raylib.LoadImageFromScreen();
        bool ok = Raylib.ExportImage(image, o.Screenshot!);
        Raylib.UnloadImage(image);
        Raylib.CloseWindow();
        Console.WriteLine(ok
            ? string.Create(CultureInfo.InvariantCulture, $"wrote {o.Screenshot}: {session.Status()}; {session.Plies.Count} plies; slowest frames after the first {warm}: {worst}")
            : "screenshot failed");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Command line: <c>--data DIR</c> keeps tables, games and custom agents in DIR instead of the user data folder;
    /// <c>--size WxH</c> sets the window size; <c>--load FILE</c> opens a saved game. The rest drive the headless screenshot mode used to check the drawing:
    /// <c>--screenshot PNG</c> with <c>--n</c>, <c>--gilith</c>, <c>--choose-start</c>, <c>--fox</c>/<c>--hounds</c>
    /// (a player label), <c>--moves "e1-d2 b8-c7"</c>, <c>--select SQUARE</c>, <c>--save</c>, <c>--load-dialog</c>
    /// and <c>--frames</c>.
    /// </summary>
    private sealed record Options(int Width, int Height, string? DataDirectory, string? Load, string? Screenshot, int N, string Fox,
        string Hounds, IReadOnlyList<string> Moves, string? Select, bool ChooseStart, bool Gilith, bool Save,
        bool LoadDialog, int Frames)
    {
        public static Options Parse(string[] args)
        {
            string Get(string name, string fallback)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            }

            string size = Get("--size", "1100x760");
            int x = size.IndexOf('x', StringComparison.Ordinal);
            return new Options(
                int.Parse(size.AsSpan(0, x), CultureInfo.InvariantCulture),
                int.Parse(size.AsSpan(x + 1), CultureInfo.InvariantCulture),
                args.Contains("--data") ? Path.GetFullPath(Get("--data", ".")) : null,
                args.Contains("--load") ? Path.GetFullPath(Get("--load", "")) : null,
                args.Contains("--screenshot") ? Get("--screenshot", "screenshot.png") : null,
                int.Parse(Get("--n", "8"), CultureInfo.InvariantCulture),
                Get("--fox", "Human"),
                Get("--hounds", "Human"),
                Get("--moves", "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
                args.Contains("--select") ? Get("--select", "") : null,
                args.Contains("--choose-start"),
                args.Contains("--gilith"),
                args.Contains("--save"),
                args.Contains("--load-dialog"),
                int.Parse(Get("--frames", "3"), CultureInfo.InvariantCulture));
        }
    }
}
