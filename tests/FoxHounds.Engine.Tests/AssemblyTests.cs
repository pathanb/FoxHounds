namespace FoxHounds.Engine.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void Library_under_test_is_referenced()
    {
        Assert.Equal("FoxHounds.Engine", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
