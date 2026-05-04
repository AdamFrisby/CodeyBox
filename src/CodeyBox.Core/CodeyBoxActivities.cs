using System.Diagnostics;

namespace CodeyBox.Core;

/// <summary>
/// Shared ActivitySource instances. One source per logical observability area so
/// operators can configure OTel sampling rules per area. All sources are always
/// allocated; the OTel SDK no-ops StartActivity when no listener is registered.
/// </summary>
public static class CodeyBoxActivities
{
    public static readonly ActivitySource Pipeline = new("CodeyBox.Pipeline");
    public static readonly ActivitySource Sandbox  = new("CodeyBox.Sandbox");
    public static readonly ActivitySource Upstream = new("CodeyBox.Upstream");
    public static readonly ActivitySource Audit    = new("CodeyBox.Audit");
}
