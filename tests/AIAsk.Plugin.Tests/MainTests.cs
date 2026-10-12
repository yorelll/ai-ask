using System.Reflection;
using AIAsk.Plugin.Answer;
using AIAsk.Plugin.Chat;
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
        Assert.DoesNotContain("configuration", result.Title, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task QueryAsync_BareAdd_SkillActionFillsQueryWithoutStartingChat()
    {
        var plugin = CreateConfiguredPlugin();
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var skillPath = Path.Combine(root, "translate.md");
        File.WriteAllText(skillPath, "Translate only.");

        var (flowApi, recorder) = ChangeQueryRecordingApi.Create();
        // The picker only needs Flow's public API for ChangeQuery; avoid
        // depending on PluginMetadata's internal path setters in a unit test.
        SetPrivateField(plugin, "_context", new PluginInitContext(null!, flowApi));

        var repository = new SkillRepository(AppContext.BaseDirectory, root);
        repository.Add("trns", skillPath, global: false);
        SetPrivateField(plugin, "_skills", repository);

        var results = await plugin.QueryAsync(CreateAiQuery("/add"), CancellationToken.None);
        var picker = Assert.Single(results, result => result.Title == "trns");

        var hide = picker.Action!(new ActionContext());

        Assert.False(hide);
        Assert.Equal("ai /add trns ", recorder.LastQuery);
        Assert.False(recorder.LastRequery);
        var session = (AnswerSession)plugin.GetType()
            .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(plugin)!;
        Assert.Equal(AnswerSessionState.Draft, session.Snapshot().State);
    }

    private static Query CreateAiQuery(string search)
    {
        var query = new Query
        {
            ActionKeyword = "ai",
            SearchTerms = search.Split(Query.TermSeparator, StringSplitOptions.RemoveEmptyEntries)
        };
        SetQueryProperty(query, nameof(Query.Search), search);
        SetQueryProperty(query, nameof(Query.OriginalQuery), $"ai {search}");
        SetQueryProperty(query, nameof(Query.TrimmedQuery), $"ai {search}");
        return query;
    }

    private static SkillRepository RequireSkills(Main plugin) =>
        (SkillRepository)typeof(Main).GetField("_skills", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;

    private class ChangeQueryRecordingApi : DispatchProxy
    {
        public string? LastQuery { get; private set; }
        public bool LastRequery { get; private set; }

        public static (IPublicAPI Api, ChangeQueryRecordingApi Recorder) Create()
        {
            var proxy = Create<IPublicAPI, ChangeQueryRecordingApi>();
            return (proxy, (ChangeQueryRecordingApi)(object)proxy);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IPublicAPI.ChangeQuery))
            {
                LastQuery = (string?)args?[0];
                LastRequery = args is { Length: > 1 } && args[1] is bool requery && requery;
            }
            return targetMethod?.ReturnType == typeof(bool) ? false : null;
        }
    }

    private static Query CreateGlobalQuery(string text)
    {
        var query = new Query
        {
            ActionKeyword = string.Empty,
            SearchTerms = text.Split(Query.TermSeparator, StringSplitOptions.RemoveEmptyEntries)
        };
        SetQueryProperty(query, nameof(Query.Search), text);
        SetQueryProperty(query, nameof(Query.OriginalQuery), text);
        SetQueryProperty(query, nameof(Query.TrimmedQuery), text);
        return query;
    }

    private static void SetQueryProperty(Query query, string propertyName, string value)
    {
        var property = typeof(Query).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(query, value);
    }

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
        return new Main(new AnswerSession(), new ThrowingChatClientFactory(), settings);
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

    [Fact]
    public async Task QueryAsync_DynamicPicker_SelectActionRefillsQueryWithAlias()
    {
        var plugin = CreateConfiguredPlugin();
        var skillPath = Path.Combine(AppContext.BaseDirectory, "picker-skill.md");
        await File.WriteAllTextAsync(skillPath, "temporary skill");
        try
        {
            SetPrivateField(plugin, "_skills", new SkillRepository(
                AppContext.BaseDirectory,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
            var repository = GetPrivateField<SkillRepository>(plugin, "_skills");
            repository.Add("translate", skillPath, global: false);

            var results = await plugin.QueryAsync(CreateAiQuery("/add"), CancellationToken.None);
            var skillResult = Assert.Single(results, result => result.Title == "translate");

            var actionResult = skillResult.Action(new ActionContext());

            Assert.False(actionResult);
        }
        finally
        {
            File.Delete(skillPath);
        }
    }

    [Fact]
    public async Task AnswerResults_PutPreviewBeforeCopyResult()
    {
        var session = new AnswerSession();
        var plugin = new Main(session, new ThrowingChatClientFactory(), new TestSettingsAccessor());
        session.Begin("prompt");
        session.Append(session.Snapshot().Generation, "answer text");
        session.Complete(session.Snapshot().Generation);

        var results = await plugin.QueryAsync(CreateAiQuery("/last"), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Copy full answer", results[0].Title);
    }

    private static Query CreateAiQuery(string search)
    {
        var query = new Query
        {
            ActionKeyword = "ai",
            SearchTerms = search.Split(Query.TermSeparator, StringSplitOptions.RemoveEmptyEntries)
        };
        SetQueryProperty(query, nameof(Query.Search), search);
        SetQueryProperty(query, nameof(Query.OriginalQuery), $"ai {search}");
        SetQueryProperty(query, nameof(Query.TrimmedQuery), $"ai {search}");
        return query;
    }

    private static T GetPrivateField<T>(Main plugin, string name)
    {
        var field = typeof(Main).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(plugin));
    }

    private sealed class ThrowingChatClientFactory : Main.IChatClientFactory
    {
        public OpenAiCompatibleChatClient Create() =>
            throw new InvalidOperationException("Chat must not start during query routing tests.");
    }
}
