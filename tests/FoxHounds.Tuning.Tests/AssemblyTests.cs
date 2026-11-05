namespace FoxHounds.Tuning.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void Library_under_test_is_referenced()
    {
        Assert.Equal("FoxHounds.Tuning", typeof(Spsa).Assembly.GetName().Name);
    }
}
