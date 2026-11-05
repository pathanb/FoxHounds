namespace FoxHounds.Solver.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void Library_under_test_is_referenced()
    {
        Assert.Equal("FoxHounds.Solver", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
