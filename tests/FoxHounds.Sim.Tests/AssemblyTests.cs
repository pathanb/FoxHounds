namespace FoxHounds.Sim.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void Library_under_test_is_referenced()
    {
        Assert.Equal("FoxHounds.Sim", typeof(Stats).Assembly.GetName().Name);
    }
}
