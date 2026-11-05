namespace FoxHounds.Solver.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void Library_under_test_is_referenced()
    {
        Assert.Equal("FoxHounds.Solver", typeof(SolverTable).Assembly.GetName().Name);
    }
}
