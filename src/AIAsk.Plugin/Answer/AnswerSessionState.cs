namespace AIAsk.Plugin.Answer;

/// <summary>
/// The explicit lifecycle of a user-visible answer generation.
/// </summary>
public enum AnswerSessionState
{
    Draft,
    Ready,
    Streaming,
    Completed,
    Cancelled,
    Failed
}
