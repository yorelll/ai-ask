using Flow.Launcher.Plugin;
using Xunit;

namespace AIAsk.Plugin.Tests;

public sealed class MainTests
{
    [Fact]
    public void QueryAsync_ReturnsSafeConfigurationError_WithoutSettings()
    {
        var plugin = new Main();

        var result = Assert.Single(plugin.QueryAsync(new Query { Search = "hello" }, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("AI Ask configuration error", result.Title);
        Assert.DoesNotContain("https://", result.SubTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryAsync_EmptyQuery_ReturnsPrompt()
    {
        var plugin = new Main();

        var result = Assert.Single(plugin.QueryAsync(new Query(), CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("AI Ask", result.Title);
        Assert.Contains("Generate response", result.SubTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryAsync_StopWithoutGeneration_ReturnsFriendlyResult()
    {
        var plugin = new Main();

        var result = Assert.Single(plugin.QueryAsync(new Query { Search = "/stop" }, CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("Stop requested", result.Title);
        Assert.Contains("No generation", result.SubTitle, StringComparison.OrdinalIgnoreCase);
    }
}
