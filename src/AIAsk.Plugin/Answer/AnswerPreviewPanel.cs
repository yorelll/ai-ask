using System.Windows;
using System.Windows.Controls;
using Flow.Launcher.Plugin;

namespace AIAsk.Plugin.Answer;

/// <summary>
/// Native Flow preview panel for a complete AI answer. The text surface is a
/// read-only WPF TextBox, so users can select an arbitrary part and press
/// Ctrl+C without opening a browser or HTML page.
/// </summary>
public sealed class AnswerPreviewPanel : UserControl
{
    private readonly TextBox _answerTextBox;
    private readonly TextBlock _statusTextBlock;
    private readonly Button _copyAllButton;
    private readonly IAnswerClipboard _clipboard;

    public AnswerPreviewPanel(IAnswerClipboard? clipboard = null)
    {
        _clipboard = clipboard ?? new WpfAnswerClipboard();
        _statusTextBlock = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };

        _copyAllButton = new Button
        {
            Content = "Copy all",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 4, 12, 4),
            IsEnabled = false
        };
        _copyAllButton.Click += (_, _) => CopyAll();

        _answerTextBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            MinHeight = 140
        };

        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(_statusTextBlock, Dock.Top);
        DockPanel.SetDock(_copyAllButton, Dock.Top);
        panel.Children.Add(_statusTextBlock);
        panel.Children.Add(_copyAllButton);
        panel.Children.Add(_answerTextBox);
        Content = panel;
    }

    /// <summary>Complete answer currently rendered in the selectable editor.</summary>
    public string AnswerText => _answerTextBox.Text;

    /// <summary>Bind a snapshot emitted by <see cref="AnswerSession"/>.</summary>
    public void Update(AnswerSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Update(snapshot.Answer, snapshot.State.ToString(), snapshot.ErrorMessage);
    }

    /// <summary>Update display from a presenter without requiring Flow APIs.</summary>
    public void Update(string? answer, string? status, string? errorMessage = null)
    {
        _answerTextBox.Text = answer ?? string.Empty;
        _copyAllButton.IsEnabled = !string.IsNullOrEmpty(_answerTextBox.Text);
        _statusTextBlock.Text = string.IsNullOrEmpty(errorMessage)
            ? status ?? string.Empty
            : errorMessage;
    }

    /// <summary>Copies the full answer. Partial selection uses WPF Ctrl+C.</summary>
    public void CopyAll()
    {
        _ = _clipboard.TryCopy(_answerTextBox.Text);
    }
}

/// <summary>
/// Public factory used by the native Flow shell to attach a preview control to
/// a result's PreviewPanel. The panel stays native WPF; no localhost/HTML is
/// involved.
/// </summary>
public sealed class AnswerPreviewPanelFactory
{
    public AnswerPreviewPanel Create(AnswerSessionSnapshot snapshot, IAnswerClipboard? clipboard = null)
    {
        var panel = new AnswerPreviewPanel(clipboard);
        panel.Update(snapshot);
        return panel;
    }

    /// <summary>
    /// Creates the exact shape expected by <see cref="Result.PreviewPanel"/>.
    /// The shell can assign this directly:
    /// <code>result.PreviewPanel = factory.CreateLazy(session.Snapshot());</code>
    /// </summary>
    public Lazy<UserControl> CreateLazy(AnswerSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new Lazy<UserControl>(() => Create(snapshot));
    }

    /// <summary>
    /// Creates a live preview that follows future session snapshots. The event
    /// is marshalled to the WPF dispatcher before controls are updated.
    /// </summary>
    public Lazy<UserControl> CreateLive(AnswerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new Lazy<UserControl>(() =>
        {
            var panel = Create(session.Snapshot());
            EventHandler<AnswerSessionSnapshot>? handler = null;
            handler = (_, snapshot) =>
            {
                if (panel.Dispatcher.HasShutdownStarted || panel.Dispatcher.HasShutdownFinished)
                {
                    session.Changed -= handler;
                    return;
                }

                if (panel.Dispatcher.CheckAccess())
                {
                    panel.Update(snapshot);
                }
                else
                {
                    _ = panel.Dispatcher.BeginInvoke(() => panel.Update(snapshot));
                }
            };
            session.Changed += handler;
            panel.Unloaded += (_, _) => session.Changed -= handler;
            return panel;
        });
    }
}
