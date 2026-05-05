namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local representation of a work-item question returned by GET /workitems/{id}/questions.
/// Intentionally separate from CodeyBox.Core — coupling is REST + JSON only.
/// </summary>
public sealed class QuestionDto
{
    public string Id { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string QuestionId { get; set; } = "";
    public string QuestionText { get; set; } = "";
    public DateTimeOffset AskedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public string? AnswerText { get; set; }
    public string? AnsweredBy { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
    public string? DismissReason { get; set; }
    public string State { get; set; } = "open";

    public bool IsOpen => State == "open";
    public bool IsAnswered => State == "answered";
    public bool IsDismissed => State == "dismissed";
}
