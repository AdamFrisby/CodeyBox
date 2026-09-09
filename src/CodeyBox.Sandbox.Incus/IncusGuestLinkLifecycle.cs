namespace CodeyBox.Sandbox.Incus;

internal static class IncusGuestLinkLifecycle
{
    internal static async Task RemoveForIsolatedValidationAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusGuestLink> links,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(links);
        foreach (var link in links)
        {
            Validate(link);
            await IncusGuestPathAuthorization.ValidateCanonicalParentAsync(
                cli,
                options,
                name,
                link.LinkPath,
                ct).ConfigureAwait(false);
            if (!await IsSymbolicLinkAsync(cli, options, name, link.LinkPath, ct).ConfigureAwait(false))
            {
                await EnsureAbsentAsync(cli, options, name, link.LinkPath, ct).ConfigureAwait(false);
                continue;
            }
            await VerifyExactAsync(cli, options, name, link, ct).ConfigureAwait(false);
            await cli.RunCheckedAsync(
                "remove isolated guest file-mount link",
                options,
                IncusCommandBuilder.BuildExec(options, name, ["rm", "-f", "--", link.LinkPath]),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false).ConfigureAwait(false);
            await EnsureAbsentAsync(cli, options, name, link.LinkPath, ct).ConfigureAwait(false);
        }
    }

    internal static async Task CreateAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        IReadOnlyList<IncusGuestLink> links,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(links);
        foreach (var link in links)
        {
            Validate(link);
            var parent = link.LinkPath[..link.LinkPath.LastIndexOf('/')];
            if (parent.Length == 0)
                parent = "/";
            await IncusGuestPathAuthorization.ValidateCanonicalParentAsync(
                cli,
                options,
                name,
                link.LinkPath,
                ct).ConfigureAwait(false);
            await cli.RunCheckedAsync(
                "create guest file-mount parent",
                options,
                IncusCommandBuilder.BuildExec(options, name, ["mkdir", "-p", "--", parent]),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false).ConfigureAwait(false);
            if (await IsSymbolicLinkAsync(cli, options, name, link.LinkPath, ct).ConfigureAwait(false))
            {
                await VerifyExactAsync(cli, options, name, link, ct).ConfigureAwait(false);
                continue;
            }
            await EnsureAbsentAsync(cli, options, name, link.LinkPath, ct).ConfigureAwait(false);
            await cli.RunCheckedAsync(
                "create guest file-mount link",
                options,
                IncusCommandBuilder.BuildExec(
                    options,
                    name,
                    ["ln", "-s", "--", link.Target, link.LinkPath]),
                stdin: null,
                options.OperationTimeout,
                ct,
                heavyOperation: false).ConfigureAwait(false);
            await VerifyExactAsync(cli, options, name, link, ct).ConfigureAwait(false);
        }
    }

    internal static async Task VerifyExactAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        IncusGuestLink link,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        Validate(link);
        await IncusGuestPathAuthorization.ValidateCanonicalParentAsync(
            cli,
            options,
            name,
            link.LinkPath,
            ct).ConfigureAwait(false);
        if (!await IsSymbolicLinkAsync(cli, options, name, link.LinkPath, ct).ConfigureAwait(false))
        {
            throw new InvalidDataException(
                $"Guest link '{link.LinkPath}' is absent or is no longer a symbolic link.");
        }

        var targetResult = await cli.RunCheckedAsync(
            "verify exact guest link target",
            options,
            IncusCommandBuilder.BuildExec(options, name, ["readlink", "--", link.LinkPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 8192,
            maxStderrBytes: 4096).ConfigureAwait(false);
        var actualTarget = targetResult.Stdout.TrimEnd('\r', '\n');
        if (actualTarget.Length == 0
            || actualTarget.Contains('\r')
            || actualTarget.Contains('\n')
            || !string.Equals(actualTarget, link.Target, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Guest link '{link.LinkPath}' no longer points to its authorized target.");
        }
    }

    private static async Task<bool> IsSymbolicLinkAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        string linkPath,
        CancellationToken ct)
    {
        var linkStatus = await cli.RunAllowFailureAsync(
            options,
            // test(1) has no portable operand delimiter. Provider-captured
            // guest paths are normalized absolute paths and cannot begin '-'.
            IncusCommandBuilder.BuildExec(options, name, ["test", "-L", linkPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 128,
            maxStderrBytes: 4096).ConfigureAwait(false);
        return linkStatus.Success;
    }

    private static async Task EnsureAbsentAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        string linkPath,
        CancellationToken ct)
    {
        var leafAbsent = await cli.RunAllowFailureAsync(
            options,
            // Both probes are required: -e alone treats a dangling symlink as
            // absent, while -L distinguishes that unsafe state.
            IncusCommandBuilder.BuildExec(options, name, ["test", "!", "-e", linkPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 128,
            maxStderrBytes: 4096).ConfigureAwait(false);
        var linkAbsent = await cli.RunAllowFailureAsync(
            options,
            IncusCommandBuilder.BuildExec(options, name, ["test", "!", "-L", linkPath]),
            stdin: null,
            options.OperationTimeout,
            ct,
            heavyOperation: false,
            maxStdoutBytes: 128,
            maxStderrBytes: 4096).ConfigureAwait(false);
        if (!leafAbsent.Success || !linkAbsent.Success)
        {
            throw new InvalidDataException(
                $"Guest link path '{linkPath}' is occupied by an unauthorized filesystem entry.");
        }
    }

    private static void Validate(IncusGuestLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        IncusInputValidation.ValidateAbsoluteGuestPath(link.Target, nameof(link));
        IncusInputValidation.ValidateAbsoluteGuestPath(link.LinkPath, nameof(link));
    }
}
