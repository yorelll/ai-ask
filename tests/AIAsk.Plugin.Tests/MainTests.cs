using System.Reflection;
using AIAsk.Plugin.Settings;
using AIAsk.Plugin.Skills;
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
    public async Task QueryAsync_CompactDynamicAdd_RoutesToDynamicCommand(string compactQuery)
    {
        var plugin = CreateConfiguredPlugin();
        SetPrivateField(plugin, "_skills", new SkillRepository(
            AppContext.BaseDirectory,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        var results = await plugin.QueryAsync(CreateGlobalQuery(compactQuery), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Dynamic skill error", result.Title);
    }

    [Theory]
    [InlineData("calc 1+2")]
    [InlineData("open chrome")]
    [InlineData("aix:/add:translate:question")]
    public async Task QueryAsync_CompactGlobalRoute_DoesNotClaimUnrelatedText(string foreignQuery)
    {
        var plugin = new Main();

        var results = await plugin.QueryAsync(CreateGlobalQuery(foreignQuery), CancellationToken.None);

        Assert.Empty(results);
    }

    private static Query CreateGlobalQuery(string text) => new()
    {
        ActionKeyword = string.Empty,
        Search = text,
        OriginalQuery = text,
        TrimmedQuery = text,
        SearchTerms = text.Split(Query.TermSeparator, StringSplitOptions.RemoveEmptyEntries)
    };

    private static Main CreateConfiguredPlugin()
    {
        var settings = new TestSettingsAccessor
        {
            Current = new AIAskPluginSettings
            {
                BaseUrl = "https://example.invalid/v1",
                ApiKey = "test-key"
            }
        };
        return new Main(new Answer.AnswerSession(), new ThrowingChatClientFactory(), settings);
    }

    private static void SetPrivateField<T>(Main plugin, string name, T value)
    {
        var field = typeof(Main).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(plugin, value);
    }

    private sealed class TestSettingsAccessor : IPluginSettingsAccessor
    {
        public AIAskPluginSettings Current { get; set; } = new();
        public void Save() { }
    }

    private sealed class ThrowingChatClientFactory : Main.IChatClientFactory
    {
        public Chat.OpenAiCompatibleChatClient Create() =>
            throw new InvalidOperationException("Chat must not start during query routing tests.");
    }
}
