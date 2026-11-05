namespace FoxHounds.Agents.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void Library_under_test_is_referenced()
    {
        Assert.Equal("FoxHounds.Agents", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
