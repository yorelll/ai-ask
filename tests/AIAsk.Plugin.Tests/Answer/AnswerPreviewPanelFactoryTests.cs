using System.Threading;
using AIAsk.Plugin.Answer;
using Xunit;

namespace AIAsk.Plugin.Tests.Answer;

public sealed class AnswerPreviewPanelFactoryTests
{
    [Fact]
    public void Create_UsesSnapshotAnswerAndState()
    {
        var snapshot = new AnswerSessionSnapshot(
            Generation: 7,
            State: AnswerSessionState.Completed,
            Prompt: "question",
            Answer: "complete answer",
            Summary: "complete answer",
            ErrorMessage: null);

        var panel = RunOnSta(() => new AnswerPreviewPanelFactory().Create(snapshot));

        Assert.Equal("complete answer", panel.AnswerText);
    }

    [Fact]
    public void Update_ReplacesAnswerText()
    {
        var answer = RunOnSta(() =>
        {
            var panel = new AnswerPreviewPanel();
            panel.Update("first", "Streaming");
            panel.Update("final", "Completed");
            return panel.AnswerText;
        });

        Assert.Equal("final", answer);
    }

    private static T RunOnSta<T>(Func<T> callback)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = callback();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            throw error;
        }

        return result!;
    }
}
