using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using CodeyBox.Cli.Models;
using CodeyBox.Cli.Services;

namespace CodeyBox.Cli.Commands;

/// <summary>
/// Standalone audit-run surface: queue a tool-only audit at a pinned SHA
/// and inspect its reports, logs, and artifacts. JSON output suits automation.
/// </summary>
internal static class AuditRunCommand
{
    internal static Command Build(
        Option<string?> apiUrlOpt,
        Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory)
    {
        var run = new Command("run", "Queue a standalone tool-only audit run at a pinned ref");
        var projectOpt = new Option<string?>("--project", "Owning project (required)");
        var refOpt = new Option<string?>("--ref", "Explicit 40-hex SHA to audit (required)");
        var baseRefOpt = new Option<string?>("--base-ref", "Explicit base SHA for diff-based auditors");
        var auditorOpt = new Option<string[]>("--auditor", "Selected auditor ID (repeatable)");
        var profileOpt = new Option<string?>("--profile", "Named audit profile (alternative to --auditor)");
        var waitOpt = new Option<bool>("--wait", "Poll until the run reaches a terminal state");
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");
        var keyOpt = new Option<string?>("--idempotency-key", "Fixed idempotency key for safe retry");
        run.AddOption(projectOpt);
        run.AddOption(refOpt);
        run.AddOption(baseRefOpt);
        run.AddOption(auditorOpt);
        run.AddOption(profileOpt);
        run.AddOption(waitOpt);
        run.AddOption(jsonOpt);
        run.AddOption(keyOpt);
        run.SetHandler((InvocationContext ctx) =>
        {
            var req = new AuditRunCreateRequest
            {
                Project = ctx.ParseResult.GetValueForOption(projectOpt),
                Ref = ctx.ParseResult.GetValueForOption(refOpt),
                BaseRef = ctx.ParseResult.GetValueForOption(baseRefOpt),
                Auditors = ctx.ParseResult.GetValueForOption(auditorOpt)?.ToList(),
                Profile = ctx.ParseResult.GetValueForOption(profileOpt),
            };
            var wait = ctx.ParseResult.GetValueForOption(waitOpt);
            var key = ctx.ParseResult.GetValueForOption(keyOpt);
            return RunCreateAsync(ctx, apiUrlOpt, apiKeyOpt, jsonOpt, clientFactory, req, key, wait);
        });

        var list = new Command("list", "List standalone audit runs");
        var listProject = new Option<string?>("--project", "Filter by owning project");
        var listLimit = new Option<int?>("--limit", "Max rows");
        var listJson = new Option<bool>("--json", "Print raw JSON response");
        list.AddOption(listProject);
        list.AddOption(listLimit);
        list.AddOption(listJson);
        list.SetHandler((InvocationContext ctx) => JsonEndpointCommand.RunAsync(
            ctx, apiUrlOpt, apiKeyOpt, listJson, clientFactory,
            (client, ct) => client.GetAuditRunsAsync(
                ctx.ParseResult.GetValueForOption(listProject),
                ctx.ParseResult.GetValueForOption(listLimit), ct),
            RenderRunList));

        var show = IdCommand("show", "Show a run with provenance and per-auditor outcomes", apiUrlOpt, apiKeyOpt, clientFactory,
            (client, id, project, ct) => client.GetAuditRunAsync(id, project, ct), RenderRun);

        var cancel = new Command("cancel", "Cancel a queued or executing run");
        var cancelJson = new Option<bool>("--json", "Print raw JSON response");
        var cancelId = new Argument<string>("id", "Run ID");
        cancel.AddOption(cancelJson);
        cancel.AddArgument(cancelId);
        cancel.SetHandler((InvocationContext ctx) =>
        {
            var id = ctx.ParseResult.GetValueForArgument(cancelId);
            return JsonEndpointCommand.RunAsync(
                ctx, apiUrlOpt, apiKeyOpt, cancelJson, clientFactory,
                (client, ct) => client.CancelAuditRunAsync(id, ct), RenderRun);
        });

        var report = IdCommand("report", "Show per-auditor reports for a run", apiUrlOpt, apiKeyOpt, clientFactory,
            (client, id, project, ct) => client.GetAuditRunReportsAsync(id, project, ct), RenderReports);

        var logs = IdCommand("logs", "Show bounded redacted logs for a run", apiUrlOpt, apiKeyOpt, clientFactory,
            (client, id, project, ct) => client.GetAuditRunLogsAsync(id, project, null, ct), RenderLogs);

        var artifacts = IdCommand("artifacts", "List artifacts for a run", apiUrlOpt, apiKeyOpt, clientFactory,
            (client, id, project, ct) => client.GetAuditRunArtifactsAsync(id, project, ct), RenderArtifacts);

        var artifact = new Command("artifact", "Fetch one artifact's content for a run");
        var artifactJson = new Option<bool>("--json", "Print raw JSON response");
        artifact.AddOption(artifactJson);
        artifact.AddOption(new Option<string?>("--project", "Owning project scope"));
        artifact.AddArgument(new Argument<string>("id", "Run ID"));
        artifact.AddArgument(new Argument<string>("name", "Artifact name"));
        artifact.SetHandler((InvocationContext ctx) =>
        {
            return JsonEndpointCommand.RunAsync(
                ctx, apiUrlOpt, apiKeyOpt, artifactJson, clientFactory,
                (client, ct) =>
                {
                    var id = ctx.ParseResult.GetValueForArgument((Argument<string>)artifact.Arguments[0]);
                    var name = ctx.ParseResult.GetValueForArgument((Argument<string>)artifact.Arguments[1]);
                    var project = ctx.ParseResult.GetValueForOption((Option<string?>)artifact.Options.First(o => o.Name == "project"));
                    return client.GetAuditRunArtifactAsync(id, name, project, ct);
                },
                root => Console.WriteLine(root.ValueKind == JsonValueKind.String ? root.GetString() : root.ToString()));
        });

        var cmd = new Command("audit-run", "Standalone tool-only audit runs at pinned SHAs");
        cmd.AddCommand(run);
        cmd.AddCommand(list);
        cmd.AddCommand(show);
        cmd.AddCommand(cancel);
        cmd.AddCommand(report);
        cmd.AddCommand(logs);
        cmd.AddCommand(artifacts);
        cmd.AddCommand(artifact);
        return cmd;
    }

    private static Command IdCommand(
        string name, string description,
        Option<string?> apiUrlOpt, Option<string?> apiKeyOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory,
        Func<CodeyBoxClient, string, string?, CancellationToken, Task<string>> fetch,
        Action<JsonElement> render)
    {
        var cmd = new Command(name, description);
        var jsonOpt = new Option<bool>("--json", "Print raw JSON response");
        var projectOpt = new Option<string?>("--project", "Owning project scope (denied on mismatch)");
        cmd.AddOption(jsonOpt);
        cmd.AddOption(projectOpt);
        cmd.AddArgument(new Argument<string>("id", "Run ID"));
        cmd.SetHandler((InvocationContext ctx) => JsonEndpointCommand.RunAsync(
            ctx, apiUrlOpt, apiKeyOpt, jsonOpt, clientFactory,
            (client, ct) => fetch(
                client,
                ctx.ParseResult.GetValueForArgument((Argument<string>)cmd.Arguments[0]),
                ctx.ParseResult.GetValueForOption(projectOpt), ct),
            render));
        return cmd;
    }

    private static async Task RunCreateAsync(
        InvocationContext ctx,
        Option<string?> apiUrlOpt, Option<string?> apiKeyOpt, Option<bool> jsonOpt,
        Func<ResolvedConfig, CodeyBoxClient> clientFactory,
        AuditRunCreateRequest req, string? key, bool wait)
    {
        if (wait)
        {
            await JsonEndpointCommand.RunAsync(
                ctx, apiUrlOpt, apiKeyOpt, jsonOpt, clientFactory,
                async (client, ct) =>
                {
                    var raw = await client.CreateAuditRunAsync(req, key, ct);
                    if (ctx.ParseResult.GetValueForOption(jsonOpt))
                        return raw;
                    using var doc = JsonDocument.Parse(raw);
                    var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    var deadline = DateTime.UtcNow.AddMinutes(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        var cur = await client.GetAuditRunAsync(id, req.Project, ct);
                        using var curDoc = JsonDocument.Parse(cur);
                        var state = curDoc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "";
                        if (state is "Passed" or "Findings" or "Cancelled" or "Failed")
                            return cur;
                    }
                    return await client.GetAuditRunAsync(id, req.Project, ct);
                },
                RenderRun);
            return;
        }
        await JsonEndpointCommand.RunAsync(
            ctx, apiUrlOpt, apiKeyOpt, jsonOpt, clientFactory,
            (client, ct) => client.CreateAuditRunAsync(req, key, ct),
            RenderRun);
    }

    private static void RenderRunList(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected top-level JSON array.");
        DisplayHelpers.PrintTable(
            [new("ID", 34), new("PROJECT", 20), new("STATE", 12), new("AGGREGATE", 12), new("SHA", 12)],
            root.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(e =>
            [
                DisplayHelpers.Field(e, "id"),
                DisplayHelpers.Field(e, "project"),
                DisplayHelpers.Field(e, "state"),
                DisplayHelpers.Field(e, "aggregate"),
                Trunc(DisplayHelpers.Field(e, "sha"), 12),
            ]));
    }

    private static void RenderRun(JsonElement root)
    {
        DisplayHelpers.PrintTable(
            [new("FIELD", 16), new("VALUE", 60)],
            [
                ["ID", DisplayHelpers.Field(root, "id")],
                ["Project", DisplayHelpers.Field(root, "project")],
                ["State", DisplayHelpers.Field(root, "state")],
                ["Aggregate", DisplayHelpers.Field(root, "aggregate")],
                ["SHA", DisplayHelpers.Field(root, "sha")],
                ["Base SHA", DisplayHelpers.Field(root, "baseSha")],
                ["Config", DisplayHelpers.Field(root, "configDigest")],
            ]);
    }

    private static void RenderReports(JsonElement root)
    {
        if (!root.TryGetProperty("auditors", out var auditors) || auditors.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected auditors array.");
        DisplayHelpers.PrintTable(
            [new("AUDITOR", 28), new("OUTCOME", 12), new("FINDINGS", 9), new("MS", 10)],
            auditors.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(a =>
            [
                DisplayHelpers.Field(a, "name"),
                DisplayHelpers.Field(a, "outcome"),
                a.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array
                    ? f.GetArrayLength().ToString() : "?",
                DisplayHelpers.Field(a, "durationMs"),
            ]));
    }

    private static void RenderLogs(JsonElement root)
    {
        if (!root.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected logs array.");
        foreach (var entry in logs.EnumerateArray())
        {
            Console.WriteLine($"--- {DisplayHelpers.Field(entry, "auditor")} [{DisplayHelpers.Field(entry, "outcome")}] ---");
            Console.WriteLine(entry.TryGetProperty("excerpt", out var x) ? x.GetString() : "(no log)");
            Console.WriteLine();
        }
    }

    private static void RenderArtifacts(JsonElement root)
    {
        if (!root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected artifacts array.");
        DisplayHelpers.PrintTable(
            [new("NAME", 40), new("BYTES", 10), new("DIGEST", 20)],
            artifacts.EnumerateArray().Select<JsonElement, IReadOnlyList<string?>>(a =>
            [
                DisplayHelpers.Field(a, "name"),
                DisplayHelpers.Field(a, "sizeBytes"),
                Trunc(DisplayHelpers.Field(a, "contentDigest"), 20),
            ]));
    }

    private static string? Trunc(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
