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
        return
        [
            new Result
            {
                Title = "AI Ask (C# bootstrap)",
                SubTitle = "Native C# migration bootstrap is ready.",
                IcoPath = "Images\\plugin.png",
                Action = _ => false
            }
        ];
    }

    internal bool IsInitialized => _context is not null;
}
