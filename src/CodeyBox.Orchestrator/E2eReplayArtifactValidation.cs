using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class E2eReplayArtifactValidation
{
    public const int MaxArtifactJsonBytes = 256 * 1024;
    public const int MaxSteps = 500;
    public const int MaxAssertions = 500;
    public const int MaxStringLength = 4096;
    public const int MaxReadinessAttempts = 120;
    public const int MaxReadinessDelayMs = 30_000;
    public const int MaxStepDelayAfterMs = 60_000;

    private static readonly HashSet<string> StepActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "navigate",
        "click",
        "doubleClick",
        "fill",
        "press",
        "select",
        "check",
        "uncheck",
        "hover",
        "wait",
        "waitForSelector",
    };

    private static readonly HashSet<string> AssertionKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "selectorVisible",
        "selectorHidden",
        "selectorTextContains",
        "urlContains",
        "titleContains",
    };

    public static bool TryValidate(E2eReplayArtifact artifact, out string failureKind, out string detail)
    {
        if (artifact.Steps is null)
        {
            failureKind = "ArtifactSchemaError";
            detail = "steps must be an array";
            return false;
        }

        if (artifact.Assertions is null)
        {
            failureKind = "ArtifactSchemaError";
            detail = "assertions must be an array";
            return false;
        }

        if (!IsBoundedString(artifact.Name))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"name must be <= {MaxStringLength} characters";
            return false;
        }

        if (artifact.Steps.Count > MaxSteps)
        {
            failureKind = "ArtifactTooLarge";
            detail = $"artifact has {artifact.Steps.Count} steps; maximum is {MaxSteps}";
            return false;
        }

        if (artifact.Assertions.Count > MaxAssertions)
        {
            failureKind = "ArtifactTooLarge";
            detail = $"artifact has {artifact.Assertions.Count} assertions; maximum is {MaxAssertions}";
            return false;
        }

        if (artifact.Readiness is null && artifact.Steps.Count == 0 && artifact.Assertions.Count == 0)
        {
            failureKind = "EmptyArtifact";
            detail = "artifact must include a readiness URL, at least one step, or at least one assertion";
            return false;
        }

        if (artifact.Readiness is { } readiness
            && !TryValidateReadiness(readiness, out failureKind, out detail))
        {
            return false;
        }

        for (var i = 0; i < artifact.Steps.Count; i++)
        {
            if (!TryValidateStep(artifact.Steps[i], i, out failureKind, out detail))
                return false;
        }

        for (var i = 0; i < artifact.Assertions.Count; i++)
        {
            if (!TryValidateAssertion(artifact.Assertions[i], i, out failureKind, out detail))
                return false;
        }

        failureKind = string.Empty;
        detail = string.Empty;
        return true;
    }

    private static bool TryValidateReadiness(E2eReadinessProbe readiness, out string failureKind, out string detail)
    {
        if (readiness.Argv is null)
        {
            failureKind = "ArtifactSchemaError";
            detail = "readiness.argv must be an array when present";
            return false;
        }

        if (readiness.Argv.Count > 0)
        {
            failureKind = "UnsupportedLegacyArgv";
            detail = "readiness.argv is not accepted; use readiness.url";
            return false;
        }

        if (string.IsNullOrWhiteSpace(readiness.Url))
        {
            failureKind = "ArtifactSchemaError";
            detail = "readiness.url is required when readiness is present";
            return false;
        }

        if (!IsBoundedString(readiness.Url))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"readiness.url must be <= {MaxStringLength} characters";
            return false;
        }

        if (!Uri.TryCreate(readiness.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failureKind = "ArtifactSchemaError";
            detail = "readiness.url must be an absolute http(s) URL";
            return false;
        }

        if (readiness.MaxAttempts is < 1 or > MaxReadinessAttempts)
        {
            failureKind = "ArtifactSchemaError";
            detail = $"readiness.maxAttempts must be between 1 and {MaxReadinessAttempts}";
            return false;
        }

        if (readiness.DelayMs is < 0 or > MaxReadinessDelayMs)
        {
            failureKind = "ArtifactSchemaError";
            detail = $"readiness.delayMs must be between 0 and {MaxReadinessDelayMs}";
            return false;
        }

        failureKind = string.Empty;
        detail = string.Empty;
        return true;
    }

    private static bool TryValidateStep(E2eReplayStep step, int index, out string failureKind, out string detail)
    {
        if (step.Argv is null)
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}].argv must be an array when present";
            return false;
        }

        if (step.Argv.Count > 0)
        {
            failureKind = "UnsupportedLegacyArgv";
            detail = $"steps[{index}].argv is not accepted; use action/selector/target fields";
            return false;
        }

        if (!IsSafeSandboxWorkingDirectory(step.WorkingDirectory))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}].workingDirectory must stay under /work";
            return false;
        }

        if (!IsBoundedString(step.Action)
            || !IsBoundedString(step.Selector)
            || !IsBoundedString(step.Target)
            || !IsBoundedString(step.Value)
            || !IsBoundedString(step.Stdin)
            || !IsBoundedString(step.WorkingDirectory))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}] contains a string longer than {MaxStringLength} characters";
            return false;
        }

        if (string.IsNullOrWhiteSpace(step.Action))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}].action is required";
            return false;
        }

        if (!StepActions.Contains(step.Action))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}].action '{step.Action}' is not supported";
            return false;
        }

        if (RequiresSelector(step.Action) && string.IsNullOrWhiteSpace(step.Selector))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}].selector is required for action '{step.Action}'";
            return false;
        }

        if (string.Equals(step.Action, "navigate", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(step.Target))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}].target is required for navigate";
            return false;
        }

        if ((string.Equals(step.Action, "fill", StringComparison.OrdinalIgnoreCase)
                || string.Equals(step.Action, "press", StringComparison.OrdinalIgnoreCase)
                || string.Equals(step.Action, "select", StringComparison.OrdinalIgnoreCase))
            && step.Value is null)
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}].value is required for action '{step.Action}'";
            return false;
        }

        if (step.DelayAfterMs is < 0 or > MaxStepDelayAfterMs)
        {
            failureKind = "ArtifactSchemaError";
            detail = $"steps[{index}].delayAfterMs must be between 0 and {MaxStepDelayAfterMs}";
            return false;
        }

        failureKind = string.Empty;
        detail = string.Empty;
        return true;
    }

    private static bool TryValidateAssertion(E2eReplayAssertion assertion, int index, out string failureKind, out string detail)
    {
        if (assertion.Argv is null)
        {
            failureKind = "ArtifactSchemaError";
            detail = $"assertions[{index}].argv must be an array when present";
            return false;
        }

        if (assertion.Argv.Count > 0)
        {
            failureKind = "UnsupportedLegacyArgv";
            detail = $"assertions[{index}].argv is not accepted; use kind/selector/target/value fields";
            return false;
        }

        if (string.IsNullOrWhiteSpace(assertion.Kind))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"assertions[{index}].kind is required";
            return false;
        }

        if (!IsBoundedString(assertion.Kind)
            || !IsBoundedString(assertion.Selector)
            || !IsBoundedString(assertion.Target)
            || !IsBoundedString(assertion.Value)
            || !IsBoundedString(assertion.ExpectStdoutContains)
            || !IsBoundedString(assertion.ExpectStdoutNotContains)
            || !IsBoundedString(assertion.Description))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"assertions[{index}] contains a string longer than {MaxStringLength} characters";
            return false;
        }

        if (!AssertionKinds.Contains(assertion.Kind))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"assertions[{index}].kind '{assertion.Kind}' is not supported";
            return false;
        }

        if (AssertionRequiresSelector(assertion.Kind) && string.IsNullOrWhiteSpace(assertion.Selector))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"assertions[{index}].selector is required for kind '{assertion.Kind}'";
            return false;
        }

        if ((string.Equals(assertion.Kind, "selectorTextContains", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assertion.Kind, "urlContains", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assertion.Kind, "titleContains", StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrEmpty(assertion.Value))
        {
            failureKind = "ArtifactSchemaError";
            detail = $"assertions[{index}].value is required for kind '{assertion.Kind}'";
            return false;
        }

        failureKind = string.Empty;
        detail = string.Empty;
        return true;
    }

    private static bool RequiresSelector(string action) =>
        !string.Equals(action, "navigate", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(action, "wait", StringComparison.OrdinalIgnoreCase);

    private static bool AssertionRequiresSelector(string kind) =>
        string.Equals(kind, "selectorVisible", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "selectorHidden", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "selectorTextContains", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeSandboxWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return true;
        if (!workingDirectory.StartsWith("/", StringComparison.Ordinal))
            return false;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(workingDirectory);
        }
        catch
        {
            return false;
        }

        return string.Equals(fullPath, "/work", StringComparison.Ordinal)
            || fullPath.StartsWith("/work/", StringComparison.Ordinal);
    }

    private static bool IsBoundedString(string? value) =>
        value is null || value.Length <= MaxStringLength;
}
