using System.Runtime.InteropServices;
using System.Windows;

namespace AIAsk.Plugin.Answer;

/// <summary>Abstraction for copy-all so WPF clipboard failures stay non-fatal.</summary>
public interface IAnswerClipboard
{
    bool TryCopy(string text);
}

/// <summary>
/// WPF implementation. Clipboard locks are common on Windows; callers receive
/// false rather than an exception that could take down the Flow plugin UI.
/// </summary>
public sealed class WpfAnswerClipboard : IAnswerClipboard
{
    public bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
