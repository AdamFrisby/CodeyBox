namespace CodeyBox.Cli.Models;

internal sealed class AuditRunCreateRequest
{
    public string? Project { get; set; }
    public string? Ref { get; set; }
    public string? BaseRef { get; set; }
    public List<string>? Auditors { get; set; }
    public string? Profile { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? Repository { get; set; }
}
