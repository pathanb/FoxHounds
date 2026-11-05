using BenchmarkDotNet.Running;

namespace FoxHounds.Bench;

internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
