namespace AIAsk.Plugin.Answer;

/// <summary>
/// Immutable state emitted by <see cref="AnswerSession"/> after each accepted
/// lifecycle transition. It is UI-independent so later WPF/Flow presenters can
/// safely marshal it onto their own dispatcher.
/// </summary>
public sealed record AnswerSessionSnapshot(
    long Generation,
    AnswerSessionState State,
    string Prompt,
    string Answer,
    string Summary,
    string? ErrorMessage)
{
    public bool CanCopy => !string.IsNullOrEmpty(Answer);

    public bool IsTerminal => State is AnswerSessionState.Completed
        or AnswerSessionState.Cancelled
        or AnswerSessionState.Failed;
}
