namespace CodeyBox.Admin.Web;

/// <summary>
/// Settings for the server-side SignalR hub connection used by the live
/// stdout panel in WorkItemDetail. Registered as a singleton in Program.cs;
/// derived from CodeyBoxAdmin:ApiBaseUrl and CODEYBOX_API_KEY.
/// </summary>
public sealed record OrchestratorHubSettings(string HubUrl, string? ApiKey);
