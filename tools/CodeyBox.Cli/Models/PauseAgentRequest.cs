namespace CodeyBox.Cli.Models;

internal sealed class PauseAgentRequest
{
    public string Reason { get; set; } = "";
    public double? DurationSeconds { get; set; }
}
