using System.Windows;

namespace AIAsk.Plugin.Answer;

/// <summary>
/// Owns the single modeless native answer window used by explicit answer-result
/// activation. It reuses the existing WPF panel/window for subsequent clicks so
/// a long-lived plugin never leaks windows or AnswerSession subscriptions.
/// </summary>
public sealed class AnswerWindowManager
{
    private readonly AnswerPreviewPanelFactory _previewFactory;
    private Window? _window;
    private AnswerPreviewPanel? _panel;
    private AnswerSession? _session;

    public AnswerWindowManager(AnswerPreviewPanelFactory? previewFactory = null)
    {
        _previewFactory = previewFactory ?? new AnswerPreviewPanelFactory();
    }

    public Window? CurrentWindow => _window;

    public void Show(AnswerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_window is { IsLoaded: true } && _session == session && _panel is not null)
        {
            _panel.Update(session.Snapshot());
            _window.Show();
            _window.Activate();
            _ = _panel.Focus();
            return;
        }

        CloseCurrent();
        _session = session;
        _panel = _previewFactory.CreateLive(session).Value as AnswerPreviewPanel
            ?? throw new InvalidOperationException("Expected native answer preview panel.");
        _window = new Window
        {
            Title = "AI Ask Answer",
            Content = _panel,
            Width = 760,
            Height = 560,
            MinWidth = 420,
            MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        _window.Closed += OnWindowClosed;
        _window.Show();
        _window.Activate();
        _ = _panel.Focus();
    }

    public void CloseCurrent()
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.Close();
        }
        DetachCurrent();
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        DetachCurrent();
    }

    private void DetachCurrent()
    {
        _window = null;
        _panel = null;
        _session = null;
    }
}
