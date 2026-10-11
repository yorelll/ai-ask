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

    [Theory]
    [InlineData("ai:/add:translate:question")]
    [InlineData("ai：/add：translate：question")]
    public async Task QueryAsync_CompactDynamicAdd_ReturnsSendResult(string compactQuery)
    {
        var plugin = new Main();
        var query = new Query
        {
            ActionKeyword = string.Empty,
            Search = compactQuery,
            OriginalQuery = compactQuery,
            TrimmedQuery = compactQuery
        };

        var results = await plugin.QueryAsync(query, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Generate response", result.Title);
    }

    [Theory]
    [InlineData("calc 1+2")]
    [InlineData("open chrome")]
    [InlineData("aix:/add:translate:question")]
    public async Task QueryAsync_CompactGlobalRoute_DoesNotClaimUnrelatedText(string foreignQuery)
    {
        var plugin = new Main();
        var query = new Query
        {
            ActionKeyword = string.Empty,
            Search = foreignQuery,
            OriginalQuery = foreignQuery,
            TrimmedQuery = foreignQuery
        };

        var results = await plugin.QueryAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

}
