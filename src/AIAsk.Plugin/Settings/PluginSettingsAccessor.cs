using Flow.Launcher.Plugin;

namespace AIAsk.Plugin.Settings;

/// <summary>
/// Persisted native plugin configuration. Flow owns serialization through its
/// IPublicAPI setting JSON storage, so API settings survive plugin reloads.
/// Skill definitions are persisted separately by SkillRepository in the same
/// Flow plugin settings directory.
/// </summary>
public sealed class AIAskPluginSettings
{
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "fast";

    public int MaxTokens { get; set; } = 100_000;

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Public settings seam for the native plugin shell and WPF settings panel.
/// Main.InitAsync can create this through <see cref="FlowPluginSettingsAccessor"/>
/// and pass it to chat/query services without an in-memory-only fallback.
/// </summary>
public interface IPluginSettingsAccessor
{
    AIAskPluginSettings Current { get; }

    void Save();
}

/// <summary>
/// Flow-backed implementation. LoadSettingJsonStorage returns one tracked
/// instance; SaveSettingJsonStorage persists that exact object in Flow's
/// plugin settings storage.
/// </summary>
public sealed class FlowPluginSettingsAccessor : IPluginSettingsAccessor
{
    private readonly IPublicAPI _api;

    public FlowPluginSettingsAccessor(IPublicAPI api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        Current = _api.LoadSettingJsonStorage<AIAskPluginSettings>();
    }

    public AIAskPluginSettings Current { get; }

    public void Save()
    {
        Normalize(Current);
        _api.SaveSettingJsonStorage<AIAskPluginSettings>();
    }

    public static void Normalize(AIAskPluginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.BaseUrl = (settings.BaseUrl ?? string.Empty).Trim();
        settings.ApiKey = (settings.ApiKey ?? string.Empty).Trim();
        settings.Model = string.IsNullOrWhiteSpace(settings.Model) ? "fast" : settings.Model.Trim();
        settings.MaxTokens = settings.MaxTokens <= 0 ? 100_000 : settings.MaxTokens;
        settings.TimeoutSeconds = settings.TimeoutSeconds <= 0 ? 60 : settings.TimeoutSeconds;
    }
}
