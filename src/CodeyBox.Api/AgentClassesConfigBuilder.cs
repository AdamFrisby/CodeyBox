using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

/// <summary>
/// Builds (and validates) the in-memory <see cref="AgentClassRouter"/> inputs
/// from their JSON-bound config shapes. Lives outside <c>Program.cs</c> so
/// both the startup wiring and the hot-reload coordinator can rebuild from
/// the latest <see cref="CodeyBoxOptions"/> without duplicating the
/// validation rules.
/// </summary>
public static class AgentClassesConfigBuilder
{
    /// <summary>
    /// Validates <paramref name="options"/> and produces the
    /// <see cref="AgentClass"/> catalog the router consumes. Throws
    /// <see cref="InvalidOperationException"/> on any validation failure —
    /// callers in the hot-reload path catch and keep the prior snapshot so a
    /// bad edit can't break a running orchestrator.
    /// </summary>
    public static IReadOnlyList<AgentClass> Build(
        List<AgentClassOptions> options, ILogger log)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AgentClass>();

        foreach (var classOpts in options)
        {
            if (string.IsNullOrWhiteSpace(classOpts.Id))
                throw new InvalidOperationException("Each AgentClass must have a non-empty Id");
            if (!seenIds.Add(classOpts.Id))
                throw new InvalidOperationException($"AgentClass Id '{classOpts.Id}' is not unique");
            if (classOpts.Members.Count == 0)
                throw new InvalidOperationException($"AgentClass '{classOpts.Id}' must have at least one member");

            var members = new List<AgentMembership>();
            foreach (var m in classOpts.Members)
            {
                if (string.IsNullOrWhiteSpace(m.Agent))
                    throw new InvalidOperationException($"AgentClass '{classOpts.Id}': member Agent must be non-empty");
                if (!Enum.TryParse<AgentBilling>(m.Billing, ignoreCase: true, out var billing))
                    throw new InvalidOperationException(
                        $"AgentClass '{classOpts.Id}': unknown Billing '{m.Billing}'. Expected Subscription or PayPerApi");
                if (m.QualityScore is null)
                    throw new InvalidOperationException(
                        $"AgentClass '{classOpts.Id}': member '{m.Agent}' is missing QualityScore. " +
                        $"Add QualityScore=N (0–200); see docs/agent-classes.md for recommended values.");
                var score = m.QualityScore.Value;
                if (score < 0 || score > 200)
                    throw new InvalidOperationException(
                        $"AgentClass '{classOpts.Id}': member '{m.Agent}' has QualityScore={score} which is outside the valid range 0–200.");
                // Gemini at frontier-adjacent tier REQUIRES ReasoningMode="high" — running
                // standard-reasoning Gemini in a >=90 slot misrepresents its capability.
                var agentKind = new AgentKind(m.Agent);
                if (agentKind == AgentKind.Gemini && score >= 90 &&
                    !string.Equals(m.ReasoningMode, "high", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"AgentClass '{classOpts.Id}': Gemini member with QualityScore={score} (≥90) requires " +
                        $"ReasoningMode=\"high\". Either set ReasoningMode=\"high\" (requires @google/gemini-cli ≥0.1.9 " +
                        $"with --thinking support; install via MultipassExtraRuncmd) or lower QualityScore below 90.");
                GeminiKnownModels.ValidateModelIdAgainstProviderList(classOpts.Id, agentKind, m.ModelId, log);
                // Capabilities are operator-declared tags. Normalise (trim + drop empties)
                // and de-duplicate case-insensitively so '"sensitive"' and '"Sensitive"'
                // don't both end up in the list. Tag values themselves are otherwise
                // free-form — the router compares with OrdinalIgnoreCase.
                var capabilities = new List<string>();
                var seenCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in m.Capabilities)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var tag = raw.Trim();
                    if (seenCapabilities.Add(tag))
                        capabilities.Add(tag);
                }
                members.Add(new AgentMembership
                {
                    Agent = agentKind,
                    Billing = billing,
                    ModelId = m.ModelId,
                    QualityScore = score,
                    ReasoningMode = m.ReasoningMode,
                    Capabilities = capabilities,
                });
            }

            var hasOnlySubscription = members.All(m => m.Billing == AgentBilling.Subscription);
            if (hasOnlySubscription)
                log.LogWarning(
                    "AgentClass '{ClassId}' has no PayPerApi fallback — items may wait indefinitely if all subscriptions are exhausted",
                    classOpts.Id);

            result.Add(new AgentClass
            {
                Id = classOpts.Id,
                DisplayName = string.IsNullOrWhiteSpace(classOpts.DisplayName)
                    ? classOpts.Id
                    : classOpts.DisplayName,
                Members = members,
            });
        }

        return result;
    }

    /// <summary>
    /// Parses and validates <see cref="AgentScoreModifiersOptions"/> into the
    /// pre-resolved <see cref="ParsedTodModifier"/> list the router evaluates
    /// per-pickup. Same throw-on-error contract as <see cref="Build"/>.
    /// </summary>
    public static IReadOnlyList<ParsedTodModifier> BuildTodModifiers(
        AgentScoreModifiersOptions opts, ILogger log)
    {
        // Allowed day-code → DayOfWeek mapping (three-letter abbreviations).
        var dayMap = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mon"] = DayOfWeek.Monday,
            ["Tue"] = DayOfWeek.Tuesday,
            ["Wed"] = DayOfWeek.Wednesday,
            ["Thu"] = DayOfWeek.Thursday,
            ["Fri"] = DayOfWeek.Friday,
            ["Sat"] = DayOfWeek.Saturday,
            ["Sun"] = DayOfWeek.Sunday,
        };

        var result = new List<ParsedTodModifier>();
        foreach (var entry in opts.ByTimeOfDay)
        {
            if (string.IsNullOrWhiteSpace(entry.Agent))
                throw new InvalidOperationException("AgentScoreModifiers.ByTimeOfDay: entry is missing Agent value");

            if (Math.Abs(entry.Modifier) > 5)
                throw new InvalidOperationException(
                    $"AgentScoreModifiers.ByTimeOfDay: modifier for '{entry.Agent}' is {entry.Modifier}; " +
                    $"absolute value must be ≤ 5. Modifiers are tiebreakers, not eligibility gates. " +
                    $"Use RequiredCapabilities on the work item to gate by clearance.");

            var parsedWindows = new List<ParsedTimeWindow>();
            foreach (var w in entry.Windows)
            {
                var days = new HashSet<DayOfWeek>();
                foreach (var d in w.Days)
                {
                    if (!dayMap.TryGetValue(d, out var dow))
                        throw new InvalidOperationException(
                            $"AgentScoreModifiers.ByTimeOfDay: unknown day code '{d}' for agent '{entry.Agent}'. " +
                            $"Valid codes: Mon, Tue, Wed, Thu, Fri, Sat, Sun.");
                    days.Add(dow);
                }
                if (days.Count == 0)
                    throw new InvalidOperationException(
                        $"AgentScoreModifiers.ByTimeOfDay: window for agent '{entry.Agent}' has no days.");

                if (!TimeSpan.TryParseExact(w.StartUtc, @"hh\:mm", null, out var start))
                    throw new InvalidOperationException(
                        $"AgentScoreModifiers.ByTimeOfDay: StartUtc '{w.StartUtc}' for agent '{entry.Agent}' is not a valid HH:mm time.");
                if (!TimeSpan.TryParseExact(w.EndUtc, @"hh\:mm", null, out var end))
                    throw new InvalidOperationException(
                        $"AgentScoreModifiers.ByTimeOfDay: EndUtc '{w.EndUtc}' for agent '{entry.Agent}' is not a valid HH:mm time.");

                parsedWindows.Add(new ParsedTimeWindow(days, start, end));
            }

            result.Add(new ParsedTodModifier(new AgentKind(entry.Agent), entry.Modifier, parsedWindows));
        }

        // Log active windows so operators can audit the schedule at startup.
        if (result.Count > 0)
        {
            foreach (var mod in result)
            {
                var windowDescs = mod.Windows.Select(w =>
                    $"[{string.Join(",", w.Days)} {w.Start:hh\\:mm}–{w.End:hh\\:mm} UTC]");
                log.LogInformation(
                    "AgentScoreModifiers: agent={Agent} modifier={Modifier:+0;-0} windows={Windows}",
                    mod.Agent.Value, mod.Modifier, string.Join(", ", windowDescs));
            }
        }

        return result;
    }
}
