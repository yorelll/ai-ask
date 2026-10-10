using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using AIAsk.Plugin.Answer;
using AIAsk.Plugin.Chat;
using AIAsk.Plugin.Skills;
using AIAsk.Plugin.Settings;
using Flow.Launcher.Plugin;

namespace AIAsk.Plugin;

/// <summary>
/// Native Flow Launcher plugin shell. WPF settings and native preview controls
/// are injected through dedicated interfaces so they can evolve independently
/// from query, streaming, and skill behavior.
/// </summary>
public sealed class Main : IAsyncPlugin, IContextMenu, IResultUpdated, ISettingProvider
{
    private const string DefaultModel = "fast";
    private const int DefaultMaxTokens = 100_000;
    private const int DefaultTimeoutSeconds = 60;
    private const string DefaultIcon = "Images\\plugin.png";
    private const string GeneratingIcon = "Images\\generating.png";
    private const string AnswerIcon = "Images\\answer.png";
    private const string StopIcon = "Images\\stop.png";
    private const string ClearIcon = "Images\\clear.png";
    private const string ErrorIcon = "Images\\error.png";

    private readonly AnswerSession _session;
    private readonly IChatClientFactory _chatClientFactory;
    private AIAsk.Plugin.Settings.IPluginSettingsAccessor _settings;
    private readonly object _generationGate = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _cancellations = new();

    private PluginInitContext? _context;
    private SkillRepository? _skills;
    private SkillSettingsController? _skillSettingsController;
    private AnswerPreviewPanelFactory? _previewFactory;
    private long _activeGeneration;
    private Query? _activeQuery;

    public Main()
        : this(new AnswerSession(), new DefaultChatClientFactory(), new UninitializedSettingsAccessor())
    {
    }

    internal Main(
        AnswerSession session,
        IChatClientFactory chatClientFactory,
        AIAsk.Plugin.Settings.IPluginSettingsAccessor settings)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _session.Changed += OnSessionChanged;
    }

    public event ResultUpdatedEventHandler? ResultsUpdated;

    public async Task InitAsync(PluginInitContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var metadata = context.CurrentPluginMetadata;
        _skills = new SkillRepository(metadata.PluginDirectory, metadata.PluginSettingsDirectoryPath);
        if (_settings is UninitializedSettingsAccessor)
        {
            _settings = new FlowPluginSettingsAccessor(context.API);
        }
        FlowPluginSettingsAccessor.Normalize(_settings.Current);
        _skillSettingsController = new SkillSettingsController(_skills);
        _previewFactory = new AnswerPreviewPanelFactory();
        await Task.CompletedTask;
    }

    public System.Windows.Controls.Control CreateSettingPanel() =>
        NativeSkillSettingsPanel.Create(RequireSkillSettingsController());

    public Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(query);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(QueryCore(query));
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult?.PluginID != _context?.CurrentPluginMetadata.ID)
        {
            return [];
        }

        return
        [
            CreateActionResult(
                "Copy full answer",
                "Copy the entire current answer to the clipboard",
                AnswerIcon,
                _ => CopyCurrentAnswer(hideAfterAction: false)),
            CreateActionResult("Stop generation", "Cancel the active AI request", StopIcon, _ => StopActiveGeneration()),
            CreateActionResult("Clear answer", "Clear the current answer session", ClearIcon, _ =>
            {
                ClearAnswer();
                return false;
            })
        ];
    }

    private List<Result> QueryCore(Query query)
    {
        var raw = (query.Search ?? string.Empty).Trim().TrimStart(':', '：');

        if (string.IsNullOrEmpty(raw))
        {
            return [CreateResult("AI Ask", "Enter a prompt, then press Enter on Generate response.", DefaultIcon)];
        }

        if (TryHandleCommand(query, raw, out var commandResults))
        {
            return commandResults;
        }

        if (!TryCreateRequest(raw, out var request, out var error))
        {
            return [CreateResult("AI Ask configuration error", error, ErrorIcon)];
        }

        _session.MarkReady(request.Prompt);
        return [CreateSendResult(query, request)];
    }

    private bool TryHandleCommand(Query query, string raw, out List<Result> results)
    {
        var command = raw.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        switch (command.ToLowerInvariant())
        {
            case "/stop":
                results = [CreateResult("Stop requested", StopActiveGeneration() ? "Generation cancelled." : "No generation is active.", StopIcon)];
                return true;
            case "/clear":
                results = [CreateResult("Answer cleared", ClearAnswer(), ClearIcon)];
                return true;
            case "/last":
                var answer = _session.CurrentAnswer();
                results = string.IsNullOrEmpty(answer)
                    ? [CreateResult("No answer yet", "Send a prompt first.", DefaultIcon)]
                    : [CreateCopyResult("Copy full answer", CreateSummary(answer), answer, AnswerIcon)];
                return true;
            case "/add":
                results = BuildDynamicSkillResults(query, raw);
                return true;
            default:
                results = [];
                return false;
        }
    }

    private List<Result> BuildDynamicSkillResults(Query query, string raw)
    {
        var repository = RequireSkills();
        var remainder = raw.Length <= 4 ? string.Empty : raw[4..].TrimStart(' ', ':', '：');
        if (string.IsNullOrEmpty(remainder))
        {
            return repository.Load()
                .Select(skill => CreateResult(skill.Alias, $"{(skill.Global ? "Global" : "Dynamic")} · {skill.Path}", DefaultIcon))
                .Prepend(CreateResult("Choose a temporary skill", "Use /add <alias> <question>", DefaultIcon))
                .ToList();
        }

        return TryCreateRequest(raw, out var request, out var error)
            ? [CreateSendResult(query, request)]
            : [CreateResult("Dynamic skill error", error, ErrorIcon)];
    }

    private bool TryCreateRequest(string raw, out PendingRequest request, out string error)
    {
        request = default!;
        error = string.Empty;
        var baseUrl = _settings.Current.BaseUrl;
        var apiKey = _settings.Current.ApiKey;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            error = "Configure Base URL and API Key in plugin settings.";
            return false;
        }

        var repository = RequireSkills();
        IReadOnlyList<SkillDefinition> dynamicSkills = [];
        var prompt = raw;
        if (raw.StartsWith("/add", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = raw.Length <= 4 ? string.Empty : raw[4..].TrimStart(' ', ':', '：');
            var parsed = repository.ParseDynamicAdd(remainder);
            if (!parsed.IsSuccess)
            {
                error = parsed.Error ?? "Unable to load dynamic skills.";
                return false;
            }
            dynamicSkills = parsed.Skills!;
            prompt = parsed.Prompt!;
        }

        string globalPrompt;
        try
        {
            globalPrompt = repository.BuildGlobalSystemPrompt();
        }
        catch (SkillStorageException exception)
        {
            error = exception.Message;
            return false;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return false;
        }

        var dynamicPrompt = BuildDynamicSystemPrompt(dynamicSkills, repository, out error);
        if (error.Length > 0)
        {
            return false;
        }

        var systemPrompt = JoinPrompts(globalPrompt, dynamicPrompt);
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new ChatMessage("system", systemPrompt));
        }
        messages.Add(new ChatMessage("user", prompt));

        request = new PendingRequest(
            prompt,
            new ChatRequest(
                baseUrl,
                apiKey,
                _settings.Current.Model,
                messages,
                _settings.Current.MaxTokens,
                TimeSpan.FromSeconds(_settings.Current.TimeoutSeconds),
                new Dictionary<string, string> { ["X-Skill-Count"] = dynamicSkills.Count.ToString() }));
        return true;
    }

    private static string BuildDynamicSystemPrompt(IReadOnlyList<SkillDefinition> skills, SkillRepository repository, out string error)
    {
        error = string.Empty;
        if (skills.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var skill in skills)
        {
            var validation = repository.Validate(skill);
            if (!validation.IsValid)
            {
                error = $"Skill {skill.Alias} is invalid: {validation.Error}";
                return string.Empty;
            }
            try
            {
                parts.Add(File.ReadAllText(skill.Path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                error = $"Skill {skill.Alias} cannot be read.";
                return string.Empty;
            }
        }
        return string.Join("\n\n---\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string JoinPrompts(string globalPrompt, string dynamicPrompt) =>
        string.Join("\n\n---\n\n", new[] { globalPrompt, dynamicPrompt }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static string CreateSummary(string answer) => AnswerSession.CreateSummary(answer);

    private Result CreateSendResult(Query query, PendingRequest request) => CreateActionResult(
        "Generate response", "Press Enter or click to send.", GeneratingIcon, _ => StartGeneration(query, request));

    private bool StartGeneration(Query query, PendingRequest pending)
    {
        var generation = _session.Begin(pending.Prompt);
        var cancellation = new CancellationTokenSource();
        lock (_generationGate)
        {
            CancelAndDisposeActiveUnsafe();
            _activeGeneration = generation;
            _activeQuery = query;
            _cancellations[generation] = cancellation;
        }
        _ = StreamGenerationAsync(generation, pending.Request, cancellation.Token);
        return false;
    }

    private async Task StreamGenerationAsync(long generation, ChatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in _chatClientFactory.Create().StreamAsync(request, cancellationToken))
            {
                if (!_session.Append(generation, chunk))
                {
                    return;
                }
            }
            _session.Complete(generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _session.Cancel(generation);
        }
        catch (ChatClientException exception)
        {
            _session.Fail(generation, exception.Message);
        }
        catch (Exception)
        {
            _session.Fail(generation, "The AI request failed. Check endpoint, model, and plugin settings.");
        }
        finally
        {
            lock (_generationGate)
            {
                if (_cancellations.Remove(generation, out var cancellation))
                {
                    cancellation.Dispose();
                }
            }
        }
    }

    private bool StopActiveGeneration()
    {
        lock (_generationGate)
        {
            if (_activeGeneration == 0 || !_cancellations.TryGetValue(_activeGeneration, out var cancellation))
            {
                return false;
            }
            cancellation.Cancel();
            return _session.CancelActive();
        }
    }

    private string ClearAnswer()
    {
        _ = StopActiveGeneration();
        _session.MarkReady(string.Empty);
        return "Answer session cleared.";
    }

    private void CancelAndDisposeActiveUnsafe()
    {
        if (_activeGeneration != 0 && _cancellations.Remove(_activeGeneration, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
            _session.CancelActive();
        }
    }

    private SkillRepository RequireSkills() =>
        _skills ?? throw new InvalidOperationException("Plugin has not been initialized.");

    private SkillSettingsController RequireSkillSettingsController() =>
        _skillSettingsController ?? throw new InvalidOperationException("Plugin has not been initialized.");

    private void OnSessionChanged(object? sender, AnswerSessionSnapshot snapshot)
    {
        var query = _activeQuery;
        if (query is null)
        {
            return;
        }
        ResultsUpdated?.Invoke(this, new ResultUpdatedEventArgs
        {
            Query = query,
            Results = BuildAnswerResults(snapshot),
            Token = CancellationToken.None
        });
    }

    private List<Result> BuildAnswerResults(AnswerSessionSnapshot snapshot)
    {
        var results = new List<Result>();
        if (snapshot.CanCopy)
        {
            results.Add(CreateCopyResult("Copy full answer", "Enter copies the complete answer.", snapshot.Answer, AnswerIcon));
        }

        var title = snapshot.State switch
        {
            AnswerSessionState.Streaming => "Generating…",
            AnswerSessionState.Completed => "Answer ready",
            AnswerSessionState.Cancelled => "Generation cancelled",
            AnswerSessionState.Failed => "AI Ask error",
            _ => "AI Ask"
        };
        var subtitle = snapshot.State is AnswerSessionState.Failed
            ? snapshot.ErrorMessage ?? "The AI request failed."
            : snapshot.Summary;
        var answerResult = new Result
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = snapshot.State is AnswerSessionState.Failed ? ErrorIcon : AnswerIcon,
            Action = _ => false
        };
        if (_previewFactory is not null)
        {
            answerResult.PreviewPanel = _previewFactory.CreateLive(_session);
        }
        results.Add(answerResult);
        return results;
    }

    private bool CopyCurrentAnswer(bool hideAfterAction)
    {
        var text = _session.CurrentAnswer();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        _context?.API.CopyToClipboard(text);
        return hideAfterAction;
    }

    private Result CreateCopyResult(string title, string subtitle, string text, string icon) => CreateActionResult(
        title, subtitle, icon, _ => CopyCurrentAnswer(hideAfterAction: true), text);

    private static Result CreateActionResult(string title, string subtitle, string icon, Func<ActionContext, bool> action, string? copyText = null) =>
        new()
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = icon,
            CopyText = copyText ?? string.Empty,
            Action = action
        };

    private static Result CreateResult(string title, string subtitle, string icon) =>
        new()
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = icon,
            Action = _ => false
        };

    internal bool IsInitialized => _context is not null;

    internal interface IChatClientFactory
    {
        OpenAiCompatibleChatClient Create();
    }


    private sealed class DefaultChatClientFactory : IChatClientFactory
    {
        private static readonly HttpClient Client = new();
        public OpenAiCompatibleChatClient Create() => new(Client);
    }

    private sealed class UninitializedSettingsAccessor : AIAsk.Plugin.Settings.IPluginSettingsAccessor
    {
        public AIAskPluginSettings Current { get; } = new();
        public void Save() { }
    }

    private sealed record PendingRequest(string Prompt, ChatRequest Request);
}
