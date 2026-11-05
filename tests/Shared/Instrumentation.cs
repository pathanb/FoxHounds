using System.Reflection;

namespace FoxHounds.TestSupport;

/// <summary>Detects code-coverage instrumentation, whose hit counters allocate and would fail allocation tests.</summary>
internal static class Instrumentation
{
    /// <summary>True when coverlet has instrumented <paramref name="assembly"/> (it injects a tracker type).</summary>
    public static bool IsCoverageInstrumented(Assembly assembly) =>
        assembly.GetTypes().Any(t => t.Namespace?.StartsWith("Coverlet", StringComparison.Ordinal) == true);
}
