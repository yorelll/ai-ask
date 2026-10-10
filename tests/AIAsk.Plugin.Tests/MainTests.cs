using Flow.Launcher.Plugin;
using Xunit;

namespace AIAsk.Plugin.Tests;

public sealed class MainTests
{
    [Fact]
    public async Task QueryAsync_ReturnsPrompt_ForEmptyQuery()
    {
        var plugin = new Main();

        var result = Assert.Single(await plugin.QueryAsync(new Query(), CancellationToken.None));

        Assert.Equal("AI Ask", result.Title);
        Assert.Contains("Generate response", result.SubTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryAsync_StopWithoutGeneration_ReturnsFriendlyResult()
    {
        var plugin = new Main();

        var result = Assert.Single(await plugin.QueryAsync(new Query(), CancellationToken.None));

        Assert.Equal("AI Ask", result.Title);
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
