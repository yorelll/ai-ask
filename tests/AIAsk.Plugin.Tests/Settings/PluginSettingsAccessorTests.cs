using AIAsk.Plugin.Settings;
using Xunit;

namespace AIAsk.Plugin.Tests.Settings;

public sealed class PluginSettingsAccessorTests
{
    [Fact]
    public void Normalize_UsesDefaultsForMissingOrInvalidValues()
    {
        var settings = new AIAskPluginSettings
        {
            BaseUrl = "  https://example.test/v1/  ",
            ApiKey = "  key  ",
            Model = " ",
            MaxTokens = 0,
            TimeoutSeconds = -1
        };

        FlowPluginSettingsAccessor.Normalize(settings);

        Assert.Equal("https://example.test/v1/", settings.BaseUrl);
        Assert.Equal("key", settings.ApiKey);
        Assert.Equal("fast", settings.Model);
        Assert.Equal(100_000, settings.MaxTokens);
        Assert.Equal(60, settings.TimeoutSeconds);
    }

    [Fact]
    public void Normalize_PreservesExplicitValidSettings()
    {
        var settings = new AIAskPluginSettings
        {
            BaseUrl = "https://api.example/v1",
            ApiKey = "secret",
            Model = "fast",
            MaxTokens = 42_000,
            TimeoutSeconds = 75
        };

        FlowPluginSettingsAccessor.Normalize(settings);

        Assert.Equal(42_000, settings.MaxTokens);
        Assert.Equal(75, settings.TimeoutSeconds);
        Assert.Equal("fast", settings.Model);
    }
}
