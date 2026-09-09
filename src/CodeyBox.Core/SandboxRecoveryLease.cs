using System.Text.Json.Serialization;

namespace CodeyBox.Core;

/// <summary>
/// Opaque, provider-bound capability for reopening one retained sandbox whose
/// work tree could not yet be checkpointed. The token is internal durable
/// metadata and is deliberately redacted from string formatting.
/// </summary>
public sealed record SandboxRecoveryLease
{
    public const int MaximumProviderIdLength = 64;
    public const int MaximumSandboxIdLength = 128;
    public const int MaximumTokenLength = 128;

    [JsonConstructor]
    public SandboxRecoveryLease(string providerId, string sandboxId, string token)
    {
        Validate(providerId, MaximumProviderIdLength, nameof(providerId));
        Validate(sandboxId, MaximumSandboxIdLength, nameof(sandboxId));
        Validate(token, MaximumTokenLength, nameof(token));
        ProviderId = providerId;
        SandboxId = sandboxId;
        Token = token;
    }

    public string ProviderId { get; }
    public string SandboxId { get; }
    public string Token { get; }

    public override string ToString() => $"{ProviderId}:[retained-sandbox]";

    private static void Validate(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Value must be non-empty.", parameterName);
        if (value.Length > maximumLength)
            throw new ArgumentException($"Value must be at most {maximumLength} characters.", parameterName);
        if (value[0] == '-')
            throw new ArgumentException("Value must not start with '-'.", parameterName);
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
                throw new ArgumentException("Value must not contain whitespace or control characters.", parameterName);
        }
    }
}
