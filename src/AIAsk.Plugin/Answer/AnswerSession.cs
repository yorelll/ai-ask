using System.Text;

namespace AIAsk.Plugin.Answer;

/// <summary>
/// Thread-safe answer-generation state machine.
///
/// Callers must start an explicit generation before appending chunks. Every
/// mutating method receives the generation returned by <see cref="Begin"/>;
/// stale generations are ignored so an old cancelled request cannot overwrite
/// a replacement request's visible answer.
/// </summary>
public sealed class AnswerSession
{
    private const int DefaultSummaryLimit = 180;

    private readonly object _gate = new();
    private readonly StringBuilder _answer = new();
    private long _generation;
    private AnswerSessionState _state = AnswerSessionState.Draft;
    private string _prompt = string.Empty;
    private string? _errorMessage;

    /// <summary>
    /// Raised synchronously after an accepted transition while the state is
    /// internally consistent. Subscribers should marshal to the WPF dispatcher
    /// before changing controls.
    /// </summary>
    public event EventHandler<AnswerSessionSnapshot>? Changed;

    public AnswerSessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Moves Draft/terminal state to Ready without starting network work.
    /// </summary>
    public bool MarkReady(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        AnswerSessionSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_state is AnswerSessionState.Streaming)
            {
                return false;
            }

            _prompt = prompt;
            _errorMessage = null;
            _answer.Clear();
            _state = AnswerSessionState.Ready;
            snapshot = CreateSnapshot();
        }

        Publish(snapshot);
        return true;
    }

    /// <summary>
    /// Explicitly starts a generation and invalidates every prior generation.
    /// </summary>
    public long Begin(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        AnswerSessionSnapshot snapshot;
        lock (_gate)
        {
            _generation++;
            _prompt = prompt;
            _errorMessage = null;
            _answer.Clear();
            _state = AnswerSessionState.Streaming;
            snapshot = CreateSnapshot();
        }

        Publish(snapshot);
        return snapshot.Generation;
    }

    /// <summary>
    /// Appends text only when it belongs to the active streaming generation.
    /// </summary>
    public bool Append(long generation, string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (chunk.Length == 0)
        {
            return IsCurrentStreamingGeneration(generation);
        }

        AnswerSessionSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!IsCurrentStreamingGenerationUnsafe(generation))
            {
                return false;
            }

            _answer.Append(chunk);
            snapshot = CreateSnapshot();
        }

        Publish(snapshot);
        return true;
    }

    public bool Complete(long generation)
    {
        return Finish(generation, AnswerSessionState.Completed, null);
    }

    public bool Cancel(long generation)
    {
        return Finish(generation, AnswerSessionState.Cancelled, null);
    }

    public bool Fail(long generation, string safeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        return Finish(generation, AnswerSessionState.Failed, safeMessage);
    }

    /// <summary>
    /// Cancels the active request, preserving any accumulated text for a
    /// presenter that wants to offer partial copy.
    /// </summary>
    public bool CancelActive()
    {
        AnswerSessionSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_state is not AnswerSessionState.Streaming)
            {
                return false;
            }

            _state = AnswerSessionState.Cancelled;
            snapshot = CreateSnapshot();
        }

        Publish(snapshot);
        return true;
    }

    /// <summary>
    /// Returns the current full answer text, suitable for a copy-all action.
    /// </summary>
    public string CurrentAnswer()
    {
        lock (_gate)
        {
            return _answer.ToString();
        }
    }

    public static string CreateSummary(string text, int limit = DefaultSummaryLimit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var normalized = string.Join(
            " ",
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (normalized.Length <= limit)
        {
            return normalized;
        }

        return string.Concat(normalized.AsSpan(0, limit - 1), "…");
    }

    private bool Finish(long generation, AnswerSessionState terminalState, string? errorMessage)
    {
        AnswerSessionSnapshot? snapshot = null;
        lock (_gate)
        {
            if (!IsCurrentStreamingGenerationUnsafe(generation))
            {
                return false;
            }

            _state = terminalState;
            _errorMessage = errorMessage;
            snapshot = CreateSnapshot();
        }

        Publish(snapshot);
        return true;
    }

    private bool IsCurrentStreamingGeneration(long generation)
    {
        lock (_gate)
        {
            return IsCurrentStreamingGenerationUnsafe(generation);
        }
    }

    private bool IsCurrentStreamingGenerationUnsafe(long generation)
    {
        return generation == _generation && _state is AnswerSessionState.Streaming;
    }

    private AnswerSessionSnapshot CreateSnapshot()
    {
        var answer = _answer.ToString();
        return new AnswerSessionSnapshot(
            _generation,
            _state,
            _prompt,
            answer,
            CreateSummary(answer),
            _errorMessage);
    }

    private void Publish(AnswerSessionSnapshot snapshot)
    {
        Changed?.Invoke(this, snapshot);
    }
}
