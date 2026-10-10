using AIAsk.Plugin.Answer;
using Xunit;

namespace AIAsk.Plugin.Tests.Answer;

public sealed class AnswerSessionTests
{
    [Fact]
    public void NewSession_IsDraftWithNoAnswer()
    {
        var session = new AnswerSession();

        var snapshot = session.Snapshot();

        Assert.Equal(AnswerSessionState.Draft, snapshot.State);
        Assert.Equal(0, snapshot.Generation);
        Assert.Empty(snapshot.Answer);
        Assert.False(snapshot.CanCopy);
    }

    [Fact]
    public void MarkReady_DoesNotStartStreaming()
    {
        var session = new AnswerSession();

        var accepted = session.MarkReady("write a test");

        var snapshot = session.Snapshot();
        Assert.True(accepted);
        Assert.Equal(AnswerSessionState.Ready, snapshot.State);
        Assert.Equal("write a test", snapshot.Prompt);
        Assert.False(snapshot.CanCopy);
    }

    [Fact]
    public void Begin_AppendsAndCompletesCurrentGeneration()
    {
        var session = new AnswerSession();
        var generation = session.Begin("hello");

        Assert.True(session.Append(generation, "hello "));
        Assert.True(session.Append(generation, "world"));
        Assert.True(session.Complete(generation));

        var snapshot = session.Snapshot();
        Assert.Equal(AnswerSessionState.Completed, snapshot.State);
        Assert.Equal("hello world", snapshot.Answer);
        Assert.Equal("hello world", snapshot.Summary);
        Assert.True(snapshot.CanCopy);
    }

    [Fact]
    public void Summary_NormalizesWhitespaceAndTruncates()
    {
        var source = "line one\n\n line two\tline three";

        Assert.Equal("line one line two line three", AnswerSession.CreateSummary(source));
        Assert.Equal("ab…", AnswerSession.CreateSummary("abcdef", 3));
    }

    [Fact]
    public void BeginReplacement_IgnoresStaleChunksAndCompletion()
    {
        var session = new AnswerSession();
        var first = session.Begin("first");
        Assert.True(session.Append(first, "old"));

        var second = session.Begin("second");

        Assert.False(session.Append(first, " stale"));
        Assert.False(session.Complete(first));
        Assert.True(session.Append(second, "new"));
        Assert.True(session.Complete(second));

        var snapshot = session.Snapshot();
        Assert.Equal(second, snapshot.Generation);
        Assert.Equal("new", snapshot.Answer);
        Assert.Equal(AnswerSessionState.Completed, snapshot.State);
    }

    [Fact]
    public void Cancel_PreservesPartialAnswerAndRejectsLaterChunks()
    {
        var session = new AnswerSession();
        var generation = session.Begin("prompt");
        Assert.True(session.Append(generation, "partial"));

        Assert.True(session.Cancel(generation));
        Assert.False(session.Append(generation, " should not appear"));

        var snapshot = session.Snapshot();
        Assert.Equal(AnswerSessionState.Cancelled, snapshot.State);
        Assert.Equal("partial", snapshot.Answer);
        Assert.True(snapshot.CanCopy);
    }

    [Fact]
    public void Fail_StoresSafeDisplayMessageAndPreservesPartialAnswer()
    {
        var session = new AnswerSession();
        var generation = session.Begin("prompt");
        Assert.True(session.Append(generation, "partial"));

        Assert.True(session.Fail(generation, "Connection failed."));

        var snapshot = session.Snapshot();
        Assert.Equal(AnswerSessionState.Failed, snapshot.State);
        Assert.Equal("Connection failed.", snapshot.ErrorMessage);
        Assert.Equal("partial", snapshot.Answer);
    }

    [Fact]
    public void CancelActive_OnlyCancelsStreamingState()
    {
        var session = new AnswerSession();
        Assert.False(session.CancelActive());

        var generation = session.Begin("prompt");
        Assert.True(session.CancelActive());
        Assert.False(session.Complete(generation));
        Assert.Equal(AnswerSessionState.Cancelled, session.Snapshot().State);
    }

    [Fact]
    public void Changed_PublishesAcceptedTransitionsOnly()
    {
        var session = new AnswerSession();
        var snapshots = new List<AnswerSessionSnapshot>();
        session.Changed += (_, snapshot) => snapshots.Add(snapshot);

        var generation = session.Begin("prompt");
        session.Append(generation, "a");
        session.Complete(generation);
        session.Append(generation, "ignored");

        Assert.Equal(
            [AnswerSessionState.Streaming, AnswerSessionState.Streaming, AnswerSessionState.Completed],
            snapshots.Select(snapshot => snapshot.State));
    }

    [Fact]
    public async Task ConcurrentAppend_ProducesAllChunksForCurrentGeneration()
    {
        var session = new AnswerSession();
        var generation = session.Begin("prompt");
        const int count = 100;

        await Task.WhenAll(
            Enumerable.Range(0, count).Select(index =>
                Task.Run(() => session.Append(generation, index.ToString()))));

        Assert.True(session.Complete(generation));
        var answer = session.CurrentAnswer();
        Assert.Equal(count, Enumerable.Range(0, count).Count(index => answer.Contains(index.ToString(), StringComparison.Ordinal)));
    }
}
