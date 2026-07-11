using System.Text;
using CodeyBox.Core;
using CodeyBox.Sandbox.Graphical;

namespace CodeyBox.ExploratoryTesting;

/// <summary>
/// Capability and origin policy gate for model-proposed computer-use actions
/// during cheap-model authoring. Every action is validated here before it
/// reaches the real sandbox bridge.
/// </summary>
public static class ComputerUseAuthoringActionPolicy
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenshot",
        "click",
        "type",
        "key",
    };

    private static readonly string[] BlockedKeyFragments =
    [
        "super",
        "meta",
        "win",
        "ctrl+alt+",
        "control+alt+",
        "alt+f2",
        "ctrl+shift+i",
        "control+shift+i",
    ];

    public static void EnsurePlanAllowed(E2eExplorationPlan plan, ComputerUseAuthoringLimits limits)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(limits);

        if (!string.Equals(plan.Modality, "web-graphical", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Computer-use authoring supports only web-graphical modality; got '{plan.Modality}'.");
        }

        if (!string.IsNullOrWhiteSpace(plan.EntryUrl)
            && !TryIsAllowedOrigin(plan.EntryUrl, limits.AllowedOrigins, out var detail))
        {
            throw new InvalidOperationException(detail);
        }
    }

    public static void EnsureActionAllowed(
        ComputerUseRequest action,
        ComputerUseAuthoringLimits limits)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(limits);

        var canonical = ComputerUseBridge.ResolveCanonicalAction(action);
        if (!AllowedActions.Contains(canonical))
        {
            throw new InvalidOperationException(
                $"Model-proposed computer-use action '{canonical}' is not allowed during authoring.");
        }

        if (canonical is "screenshot")
            return;

        if (action.X is int x && (x < 0 || x > limits.DisplayWidthPx))
        {
            throw new InvalidOperationException(
                $"Model-proposed X coordinate {x} is outside the {limits.DisplayWidthPx}px display width.");
        }

        if (action.Y is int y && (y < 0 || y > limits.DisplayHeightPx))
        {
            throw new InvalidOperationException(
                $"Model-proposed Y coordinate {y} is outside the {limits.DisplayHeightPx}px display height.");
        }

        if (canonical is "type")
        {
            var text = action.Text ?? string.Empty;
            if (text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Model-proposed type text must not contain newlines.");
            }

            if (Encoding.UTF8.GetByteCount(text) > SandboxInputEventValidation.DefaultMaxTextUtf8Bytes)
            {
                throw new InvalidOperationException(
                    $"Model-proposed type text exceeds {SandboxInputEventValidation.DefaultMaxTextUtf8Bytes} UTF-8 bytes.");
            }
        }

        if (canonical is "key")
        {
            var key = action.Key ?? action.Text ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(key) > SandboxInputEventValidation.DefaultMaxKeyUtf8Bytes)
            {
                throw new InvalidOperationException(
                    $"Model-proposed key exceeds {SandboxInputEventValidation.DefaultMaxKeyUtf8Bytes} UTF-8 bytes.");
            }

            var normalized = key.Trim().ToLowerInvariant();
            foreach (var fragment in BlockedKeyFragments)
            {
                if (normalized.Contains(fragment, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Model-proposed key '{key}' is blocked by the authoring action policy.");
                }
            }
        }
    }

    private static bool TryIsAllowedOrigin(
        string url,
        IReadOnlyList<string> allowedOrigins,
        out string detail)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            detail = "EntryUrl must be an absolute http(s) URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            detail = "EntryUrl must not contain userinfo.";
            return false;
        }

        var normalized = NormalizeOrigin(parsed);
        foreach (var allowedOrigin in allowedOrigins)
        {
            if (!Uri.TryCreate(allowedOrigin, UriKind.Absolute, out var allowedUri))
                continue;
            if (string.Equals(normalized, NormalizeOrigin(allowedUri), StringComparison.OrdinalIgnoreCase))
            {
                detail = string.Empty;
                return true;
            }
        }

        detail = $"EntryUrl origin '{normalized}' is not in the authoring allowed-origin list.";
        return false;
    }

    private static string NormalizeOrigin(Uri uri)
    {
        var port = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;
        var defaultPort = uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        return port == defaultPort
            ? $"{uri.Scheme}://{uri.IdnHost}"
            : $"{uri.Scheme}://{uri.IdnHost}:{port}";
    }
}
