namespace FoxHounds.App.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void App_under_test_is_referenced()
    {
        Assert.Equal("FoxHounds", typeof(FoxHounds.App.Program).Assembly.GetName().Name);
    }
}
