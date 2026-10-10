using Flow.Launcher.Plugin;
using Xunit;

namespace AIAsk.Plugin.Tests;

public sealed class MainTests
{
    [Fact]
    public void Query_ReturnsBootstrapResult_ForEmptyQuery()
    {
        var plugin = new Main();

        var results = plugin.Query(new Query { Search = string.Empty });

        var result = Assert.Single(results);
        Assert.Equal("AI Ask (C# bootstrap)", result.Title);
        Assert.Contains("bootstrap", result.SubTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Images\\plugin.png", result.IcoPath);
    }

    [Fact]
    public void Query_EchoesPrompt_InBootstrapSubtitle()
    {
        var plugin = new Main();

        var result = Assert.Single(plugin.Query(new Query { Search = "hello Flow" }));

        Assert.Contains("hello Flow", result.SubTitle, StringComparison.Ordinal);
    }
}
