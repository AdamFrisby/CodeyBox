using System.Text.Json;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Verifies the effective Incus device graph immediately before a VM starts.
/// This is an authorization check, not merely diagnostics: inherited profiles,
/// extra NICs, or downgraded mount flags would cross the sandbox boundary.
/// </summary>
internal static class IncusDeviceTopology
{
    internal static void Verify(
        string json,
        IncusSandboxOptions options,
        string? expectedBridge,
        IReadOnlyList<IncusPreparedMount> mounts,
        string? expectedRecoveryTokenHash = null,
        string? expectedRecoveryManifestHash = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mounts);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var instance = UnwrapMetadata(document.RootElement);
        if (!string.Equals(ReadOptionalString(instance, "type"), "virtual-machine", StringComparison.Ordinal))
            throw new InvalidOperationException("Incus instance is not a virtual machine.");
        RejectUnsafeInstanceConfiguration(instance, "config");
        RejectUnsafeInstanceConfiguration(instance, "expanded_config");
        if (expectedRecoveryTokenHash is not null || expectedRecoveryManifestHash is not null)
        {
            if (expectedRecoveryTokenHash is null || expectedRecoveryManifestHash is null)
                throw new ArgumentException("Incus recovery topology binding must supply both hashes.");
            var config = instance.GetProperty("config");
            var actualTokenHash = ReadOptionalString(
                config,
                IncusSandboxProvider.RecoveryTokenHashKey);
            var actualManifestHash = ReadOptionalString(
                config,
                IncusSandboxProvider.RecoveryManifestHashKey);
            if (actualTokenHash is null
                || actualManifestHash is null
                || !IncusRecoveryManifestCodec.FixedTimeEqualsHash(
                    actualTokenHash,
                    expectedRecoveryTokenHash)
                || !IncusRecoveryManifestCodec.FixedTimeEqualsHash(
                    actualManifestHash,
                    expectedRecoveryManifestHash))
            {
                throw new InvalidOperationException(
                    "Incus VM recovery capability binding changed before lifecycle authorization.");
            }
        }
        if (!instance.TryGetProperty("profiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Array
            || profiles.GetArrayLength() != 0)
        {
            throw new InvalidOperationException("Incus VM unexpectedly inherited one or more profiles.");
        }
        if (!instance.TryGetProperty("expanded_devices", out var devices)
            || devices.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Incus VM did not report its effective device topology.");
        }

        var expectedNames = new HashSet<string>(StringComparer.Ordinal) { "root" };
        VerifyDevice(devices, "root", "disk", ("path", "/"), ("pool", options.StoragePoolName));
        if (expectedBridge is not null)
        {
            expectedNames.Add("codeybox-net");
            VerifyDevice(
                devices,
                "codeybox-net",
                "nic",
                ("nictype", "bridged"),
                ("parent", expectedBridge),
                ("name", "eth0"));
        }

        for (var index = 0; index < mounts.Count; index++)
        {
            var mount = mounts[index];
            if (mount.TmpfsSizeBytes.HasValue || mount.RootDiskDirectory)
                continue;
            var deviceName = IncusSandboxProvider.BuildMountDeviceNameForVerification(index);
            expectedNames.Add(deviceName);
            VerifyDevice(
                devices,
                deviceName,
                "disk",
                ("source", mount.HostSource ?? throw new InvalidOperationException("Host-backed mount has no source.")),
                ("path", mount.GuestPath),
                ("io.bus", "virtiofs"));
            var device = devices.GetProperty(deviceName);
            var readOnly = ReadOptionalString(device, "readonly");
            if (mount.ReadOnly
                ? !string.Equals(readOnly, "true", StringComparison.Ordinal)
                : string.Equals(readOnly, "true", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Incus mount device '{deviceName}' did not preserve its requested access mode.");
            }
        }

        var actualNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices.EnumerateObject())
        {
            if (!actualNames.Add(device.Name))
                throw new InvalidOperationException("Incus returned duplicate effective device names.");
        }
        if (!actualNames.SetEquals(expectedNames))
            throw new InvalidOperationException("Incus VM effective devices differ from the authorized topology.");
    }

    private static JsonElement UnwrapMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Incus query response was not a JSON object.");
        if (root.TryGetProperty("metadata", out var metadata))
        {
            if (metadata.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Incus query metadata was not a JSON object.");
            return metadata;
        }
        return root;
    }

    private static void VerifyDevice(
        JsonElement devices,
        string name,
        string type,
        params (string Key, string Value)[] properties)
    {
        if (!devices.TryGetProperty(name, out var device) || device.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Incus VM is missing required device '{name}'.");
        if (!string.Equals(ReadOptionalString(device, "type"), type, StringComparison.Ordinal))
            throw new InvalidOperationException($"Incus device '{name}' has an unexpected type.");
        foreach (var (key, expected) in properties)
        {
            if (!string.Equals(ReadOptionalString(device, key), expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"Incus device '{name}' has an unexpected '{key}' value.");
        }
    }

    private static string? ReadOptionalString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static void RejectUnsafeInstanceConfiguration(JsonElement instance, string propertyName)
    {
        if (!instance.TryGetProperty(propertyName, out var config)
            || config.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Incus VM did not report its '{propertyName}' authorization surface.");
        }
        foreach (var property in config.EnumerateObject())
        {
            if (property.Name is "raw.qemu" or "raw.qemu.conf"
                || (property.Name == "security.nesting"
                    && string.Equals(property.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Incus VM contains forbidden low-level or nesting configuration.");
            }
        }
    }
}
