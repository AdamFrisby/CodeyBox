using System.Text;
using System.Text.Json;

namespace CodeyBox.Audit.Llm.PlanAudit;

/// <summary>The trusted system prompt and untrusted user payload for one plan-audit run.</summary>
public sealed record PlanAuditPrompts(string SystemPrompt, string UserPrompt);

/// <summary>
/// Pure builder for a plan-audit chain prompt. The trusted test instructions and
/// shared framework go in the provider system channel; the untrusted task prompt
/// and PLAN artifact go in the user channel as a JSON data object, never
/// concatenated into the instructions. This keeps the injection boundary that
/// <see cref="TextOnlyPlanReview"/> requires.
/// </summary>
public static class PlanAuditPromptBuilder
{
    public static PlanAuditPrompts Build(PlanAuditTest test, string originalPrompt, string planArtifact)
    {
        ArgumentNullException.ThrowIfNull(test);

        var system = new StringBuilder();
        system.Append(TextOnlyPlanReview.TrustedSystemPreamble).Append("\n\n");
        system.Append("You are reviewing a proposed implementation PLAN before any code is written. ")
              .Append("Treat the plan as an architecture-review artifact, not a task list. Catching a ")
              .Append("wrong or ungrounded approach here is far cheaper than catching it after ")
              .Append("implementation.\n\n");

        system.Append("TEST ").Append(test.Id).Append(" — ").Append(test.Title).Append('\n');
        system.Append("Objective: ").Append(test.Objective).Append("\n\n");
        system.Append("Review the plan against these questions:\n").Append(test.ReviewGuidance).Append("\n\n");
        system.Append("Pass: ").Append(test.PassCriteria).Append('\n');
        system.Append("Fail: ").Append(test.FailCriteria).Append("\n\n");
        system.Append(test.AutomaticBlocker).Append("\n\n");
        system.Append("Required fixes for a failing plan:\n").Append(test.RequiredFixes).Append("\n\n");

        system.Append("Criterion keys for this test (use these exact keys in findings and notApplicable):\n");
        foreach (var criterion in test.Criteria)
            system.Append("- ").Append(criterion).Append('\n');
        system.Append('\n');

        system.Append(PlanAuditChainFramework.Grounding).Append("\n\n");
        system.Append(PlanAuditChainFramework.SeverityAndGate).Append("\n\n");
        system.Append(PlanAuditChainFramework.Calibration).Append("\n\n");
        system.Append(PlanAuditChainFramework.FactoryModel).Append("\n\n");
        system.Append(PlanAuditChainFramework.NotApplicable).Append("\n\n");
        system.Append(PlanAuditChainFramework.OutputSchema);

        var userData = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["originalPrompt"] = originalPrompt,
            ["planArtifact"] = planArtifact,
        });

        return new PlanAuditPrompts(system.ToString(), userData);
    }
}
