using System.CommandLine;

namespace FoxHounds.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = new RootCommand("fh: Fox and Hounds tools (perft, solve, match, tune, analyse)");
        return root.Parse(args).Invoke();
    }
}
