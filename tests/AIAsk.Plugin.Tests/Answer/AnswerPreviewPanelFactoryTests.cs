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

        var panel = new AnswerPreviewPanelFactory().Create(snapshot);

        Assert.Equal("complete answer", panel.AnswerText);
    }

    [Fact]
    public void Update_ReplacesAnswerText()
    {
        var panel = new AnswerPreviewPanel();

        panel.Update("first", "Streaming");
        panel.Update("final", "Completed");

        Assert.Equal("final", panel.AnswerText);
    }
}
