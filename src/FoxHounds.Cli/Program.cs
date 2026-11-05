using System.CommandLine;

namespace FoxHounds.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = new RootCommand("fh: Fox and Hounds tools")
        {
            PerftCommand.Create(),
            SolveCommand.Create(),
            AnalyseCommand.Create(),
        };

        try
        {
            // Our handler below prints one clean line for expected input errors instead of a stack trace.
            return root.Parse(args).Invoke(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or NotSupportedException or IOException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }
}
