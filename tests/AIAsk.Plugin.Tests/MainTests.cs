using Flow.Launcher.Plugin;
using Xunit;

namespace AIAsk.Plugin.Tests;

public sealed class MainTests
{
    [Fact]
    public async Task QueryAsync_ReturnsPrompt_ForEmptyQuery()
    {
        var plugin = new Main();

        var result = Assert.Single(await plugin.QueryAsync(new Query { ActionKeyword = "ai" }, CancellationToken.None));

        Assert.Equal("AI Ask", result.Title);
        Assert.Contains("Generate response", result.SubTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryAsync_StopWithoutGeneration_ReturnsPromptForEmptyQuery()
    {
        var plugin = new Main();

        var result = Assert.Single(await plugin.QueryAsync(new Query { ActionKeyword = "ai" }, CancellationToken.None));

        Assert.Equal("AI Ask", result.Title);
    }

    [Fact]
    public async Task QueryAsync_DoesNotClaimUnrelatedGlobalQuery()
    {
        var plugin = new Main();

        var results = await plugin.QueryAsync(new Query(), CancellationToken.None);

        Assert.Empty(results);
    }

}
