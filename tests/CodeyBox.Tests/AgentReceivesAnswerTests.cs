using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the rework prompt includes answered questions when
/// AllowAgentQuestions=true and the question store has answered questions.
/// Dismissed questions must NOT appear in the rework prompt.
/// </summary>
public sealed class AgentReceivesAnswerTests
{
    private static WorkItemQuestion Answered(string qId, string text, string answer) => new()
    {
        Id = Guid.NewGuid().ToString(),
        WorkItemId = "wi-1",
        QuestionId = qId,
        QuestionText = text,
        AnswerText = answer,
        State = "answered",
        AskedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        AnsweredAt = DateTimeOffset.UtcNow,
    };

    private static WorkItemQuestion Dismissed(string qId, string text) => new()
    {
        Id = Guid.NewGuid().ToString(),
        WorkItemId = "wi-1",
        QuestionId = qId,
        QuestionText = text,
        State = "dismissed",
        DismissReason = "out of scope",
        AskedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        DismissedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ReworkPrompt_IncludesAnsweredQuestions()
    {
        var answered = new List<WorkItemQuestion>
        {
            Answered("q-001", "Which approach?", "Use approach B; we standardise on those."),
        };

        var prompt = ReworkPromptBuilder.Build(
            "Original task.",
            findings: [],
            iteration: 1,
            maxIterations: 3,
            answeredQuestions: answered);

        Assert.Contains("Operator answers to your questions", prompt);
        Assert.Contains("q-001", prompt);
        Assert.Contains("Which approach?", prompt);
        Assert.Contains("Use approach B", prompt);
        Assert.Contains("Apply these answers to your work.", prompt);
    }

    [Fact]
    public void ReworkPrompt_ExcludesDismissedQuestions()
    {
        var questions = new List<WorkItemQuestion>
        {
            Answered("q-001", "Include me.", "Yes, include."),
            Dismissed("q-002", "Do NOT include me."),
        };

        var prompt = ReworkPromptBuilder.Build(
            "Original task.",
            findings: [],
            iteration: 1,
            maxIterations: 3,
            answeredQuestions: questions);

        Assert.Contains("q-001", prompt);
        Assert.DoesNotContain("q-002", prompt);
        Assert.DoesNotContain("Do NOT include me.", prompt);
    }

    [Fact]
    public void ReworkPrompt_NoAnsweredQuestions_OmitsSection()
    {
        var prompt = ReworkPromptBuilder.Build(
            "Original task.",
            findings: [],
            iteration: 1,
            maxIterations: 3,
            answeredQuestions: null);

        Assert.DoesNotContain("Operator answers to your questions", prompt);
    }

    [Fact]
    public void ReworkPrompt_AllDismissed_OmitsSection()
    {
        var questions = new List<WorkItemQuestion>
        {
            Dismissed("q-001", "Dismissed question."),
        };

        var prompt = ReworkPromptBuilder.Build(
            "Original task.",
            findings: [],
            iteration: 1,
            maxIterations: 3,
            answeredQuestions: questions);

        Assert.DoesNotContain("Operator answers to your questions", prompt);
    }

    [Fact]
    public void ReworkPrompt_MultipleAnswers_AllIncluded()
    {
        var questions = new List<WorkItemQuestion>
        {
            Answered("q-001", "Question one?", "Answer one."),
            Answered("q-002", "Question two?", "Answer two."),
        };

        var prompt = ReworkPromptBuilder.Build(
            "Original task.",
            findings: [],
            iteration: 1,
            maxIterations: 3,
            answeredQuestions: questions);

        Assert.Contains("q-001", prompt);
        Assert.Contains("q-002", prompt);
        Assert.Contains("Answer one.", prompt);
        Assert.Contains("Answer two.", prompt);
    }

    [Fact]
    public void ReworkPrompt_NullAnsweredQuestions_StillBuildsPrompt()
    {
        var prompt = ReworkPromptBuilder.Build(
            "Do the thing.",
            findings: [],
            iteration: 2,
            maxIterations: 5);

        Assert.Contains("Rework requested", prompt);
        Assert.Contains("Do the thing.", prompt);
        Assert.DoesNotContain("Operator answers", prompt);
    }
}
