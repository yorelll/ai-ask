using Flow.Launcher.Plugin;
using Xunit;

namespace AIAsk.Plugin.Tests;

public sealed class MainTests
{
    [Fact]
    public void Query_ReturnsBootstrapResult_ForEmptyQuery()
    {
        var plugin = new Main();

        var results = plugin.Query(new Query());

        var result = Assert.Single(results);
        Assert.Equal("AI Ask (C# bootstrap)", result.Title);
        Assert.Contains("bootstrap", result.SubTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Images\\plugin.png", result.IcoPath);
    }

    [Fact]
    public void Query_ReturnsIndependentResultLists()
    {
        var plugin = new Main();

        var first = plugin.Query(new Query());
        var second = plugin.Query(new Query());

        Assert.NotSame(first, second);
        Assert.Equal("AI Ask (C# bootstrap)", Assert.Single(second).Title);
    }
}
