using Flow.Launcher.Plugin;

namespace AIAsk.Plugin;

/// <summary>
/// Gate-0 native plugin shell. Later agents will add the C# chat, skills,
/// settings, streaming, and preview implementations behind this stable entry.
/// </summary>
public sealed class Main : IPlugin
{
    private PluginInitContext? _context;

    public void Init(PluginInitContext context)
    {
        _context = context;
    }

    public List<Result> Query(Query query)
    {
        var prompt = query.Search?.Trim() ?? string.Empty;
        var subtitle = string.IsNullOrEmpty(prompt)
            ? "Native C# migration bootstrap is ready."
            : $"C# migration bootstrap received: {prompt}";

        return
        [
            new Result
            {
                Title = "AI Ask (C# bootstrap)",
                SubTitle = subtitle,
                IcoPath = "Images\\plugin.png",
                Action = _ => false
            }
        ];
    }

    internal bool IsInitialized => _context is not null;
}
