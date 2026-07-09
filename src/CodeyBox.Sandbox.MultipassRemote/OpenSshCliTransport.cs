using System.Formats.Tar;
using System.Globalization;
using System.Text;
using CodeyBox.HostProcess;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Sandbox.MultipassRemote;

/// <summary>
/// <see cref="IRemoteHostTransport"/> backed by the local OpenSSH client.
/// Chosen as the default transport because every supported orchestrator host
/// already ships OpenSSH, no new managed dependency is required, and the
/// existing <see cref="IProcessRunner"/> infrastructure already streams the
/// child process's stdout/stderr line-by-line — which is exactly what
/// AgentStreamCapture needs for live tailing.
///
/// <para>The transport considers SSH-level failures (the OpenSSH client itself
/// failing to authenticate / connect) distinct from a successful remote exec
/// that returns non-zero. Standard OpenSSH exit codes used for classification:
/// <list type="bullet">
///   <item><c>255</c> — SSH transport failure (auth, connection, key) — maps
///   to <see cref="RemoteSshTransportException"/>.</item>
///   <item>Any other exit code — that's what the remote command actually
///   produced; returned in the <see cref="ProcessRunResult"/>.</item>
/// </list>
/// This is the documented OpenSSH convention (see <c>ssh(1)</c>: "ssh exits with
/// the exit status of the remote command or with 255 if an error occurred").
/// </para>
/// </summary>
public sealed class OpenSshCliTransport : IRemoteHostTransport
{
    /// <summary>
    /// OpenSSH's reserved exit code for "the ssh client itself failed before
    /// the remote command even ran." Used to distinguish a transport drop
    /// (recoverable sandbox failure) from a real remote-command non-zero exit.
    /// </summary>
    public const int SshTransportFailureExitCode = 255;

    private readonly Func<MultipassRemoteSandboxOptions> _opts;
    private readonly IProcessRunner _runner;
    private readonly ILogger _log;

    public OpenSshCliTransport(
        Func<MultipassRemoteSandboxOptions> opts,
        IProcessRunner runner,
        ILogger<OpenSshCliTransport> log)
    {
        _opts = opts;
        _runner = runner;
        _log = log;
    }

    public string DiagnosticId => "openssh-cli";

    public async Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? stdin,
        CancellationToken ct,
        Action<string>? stdoutChunkCallback = null,
        Action<string>? stderrChunkCallback = null,
        int? maxStdoutBytes = null,
        int? maxStderrBytes = null,
        bool killOnOutputLimit = true)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0) throw new ArgumentException("argv must be non-empty.", nameof(argv));

        var opts = _opts();
        ValidateOptionsOrThrow(opts);

        // OpenSSH treats the trailing argv as a single shell command on the
        // remote side. We build it from the requested argv via shell-quoting
        // so callers can pass argv exactly like a local exec call.
        var remoteCommand = QuoteShellArgv(argv);
        var sshArgv = BuildSshArgv(opts, remoteCommand);

        var result = await _runner.RunAsync(
            sshArgv,
            stdin,
            ct,
            stdoutChunkCallback: stdoutChunkCallback,
            stderrChunkCallback: stderrChunkCallback,
            maxStdoutBytes: maxStdoutBytes,
            maxStderrBytes: maxStderrBytes,
            killOnOutputLimit: killOnOutputLimit).ConfigureAwait(false);

        if (result.StartFailed)
            throw new RemoteSshTransportException(
                $"OpenSSH client failed to start at '{opts.SshBinary}'. Verify SshBinary points to an existing executable.");

        if (result.ExitCode == SshTransportFailureExitCode)
        {
            // Distinguish transport failures from remote 255 exits where
            // possible. ssh(1) reserves 255 for itself; remote commands that
            // legitimately exit 255 are extremely rare and treating both the
            // same is the documented OpenSSH convention.
            var stderrTail = TailFor(result.Stderr);
            _log.LogWarning(
                "SSH transport failure to {Target} via {Binary}: {StderrTail}",
                opts.SshTarget, opts.SshBinary, stderrTail);
            throw new RemoteSshTransportException(
                $"SSH transport failure to '{opts.SshTarget}' (exit 255): {stderrTail}");
        }

        return result;
    }

    public async Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ValidateRemotePath(remotePath);

        var opts = _opts();
        ValidateOptionsOrThrow(opts);

        // Build a tar | ssh tar pipeline.
        //   local:  tar -C <parent-of-host> -cf - <basename> | ssh ... 'mkdir -p <remoteParent> && tar -C <remoteParent> -xf -'
        // tar preserves mode bits and recurses directories; ssh streams the
        // tarball over the existing transport so we don't need a separate
        // SFTP / SCP code path.
        if (!File.Exists(hostPath) && !Directory.Exists(hostPath))
            throw new FileNotFoundException(
                $"StageInAsync source path does not exist on host: {hostPath}", hostPath);

        var hostParent = Path.GetDirectoryName(Path.GetFullPath(hostPath))
            ?? throw new ArgumentException($"Host path has no parent directory: {hostPath}", nameof(hostPath));
        var basename = Path.GetFileName(hostPath.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new ArgumentException($"Host path has no basename: {hostPath}", nameof(hostPath));

        var remoteParent = RemoteParent(remotePath);
        var remoteBasename = RemoteBasename(remotePath);

        var remoteCmd =
            $"set -e; mkdir -p {QuoteShellWord(remoteParent)}; " +
            $"tar -C {QuoteShellWord(remoteParent)} -xf - " +
            $"&& if [ {QuoteShellWord(remoteBasename)} != {QuoteShellWord(basename)} ]; then " +
            $"  rm -rf {QuoteShellWord(remoteParent + "/" + remoteBasename)} && " +
            $"  mv {QuoteShellWord(remoteParent + "/" + basename)} {QuoteShellWord(remoteParent + "/" + remoteBasename)}; " +
            $"fi";

        var sshArgv = BuildSshArgv(opts, remoteCmd);

        // We need to pipe the local tar's stdout into ssh's stdin. Run them
        // as separate processes connected via an in-memory stream relay so
        // we keep using IProcessRunner (which already streams stderr) for
        // each leg.
        using var tarProc = StartLocalTar(opts, hostParent, basename);
        var tarStderrTask = tarProc.StandardError.ReadToEndAsync(ct);
        try
        {
            // IProcessRunner doesn't accept a binary stdin stream — tar
            // produces opaque bytes — so the staging path runs the OpenSSH
            // child directly and copies tar.stdout → ssh.stdin.
            var sshResult = await RunSshWithBinaryStdinAsync(opts, sshArgv, tarProc.StandardOutput.BaseStream, ct).ConfigureAwait(false);

            await tarProc.WaitForExitAsync(ct).ConfigureAwait(false);
            var tarStderr = await tarStderrTask.ConfigureAwait(false);
            if (tarProc.ExitCode != 0)
                throw new RemoteSshTransportException(
                    $"Local tar failed (exit {tarProc.ExitCode}) staging '{hostPath}' into '{remotePath}': {TailFor(tarStderr)}",
                    RemoteSshTransportFailureKind.RemoteCommand);

            if (sshResult.StartFailed)
                throw new RemoteSshTransportException(
                    $"OpenSSH client failed to start during StageInAsync at '{opts.SshBinary}'.");

            if (sshResult.ExitCode == SshTransportFailureExitCode)
                throw new RemoteSshTransportException(
                    $"SSH transport failure during StageInAsync to '{opts.SshTarget}': {TailFor(sshResult.Stderr)}");

            if (sshResult.ExitCode != 0)
                throw new RemoteSshTransportException(
                    $"Remote tar-extract failed (exit {sshResult.ExitCode}) for '{remotePath}': {TailFor(sshResult.Stderr)}",
                    RemoteSshTransportFailureKind.RemoteCommand);
        }
        finally
        {
            try { if (!tarProc.HasExited) tarProc.Kill(entireProcessTree: true); } catch { }
            try { await tarStderrTask.ConfigureAwait(false); } catch { }
        }
    }

    public async Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ValidateRemotePath(remotePath);

        var opts = _opts();
        ValidateOptionsOrThrow(opts);

        var hostParent = Path.GetDirectoryName(Path.GetFullPath(hostPath))
            ?? throw new ArgumentException($"Host path has no parent directory: {hostPath}", nameof(hostPath));
        var basename = Path.GetFileName(hostPath.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new ArgumentException($"Host path has no basename: {hostPath}", nameof(hostPath));
        Directory.CreateDirectory(hostParent);

        var remoteParent = RemoteParent(remotePath);
        var remoteBasename = RemoteBasename(remotePath);

        // Remote tars the source dir to stdout; the host writes that stream to
        // a private temp archive, validates metadata, extracts into a private
        // temp directory, then swaps the validated tree into place. Never
        // extract sandbox-controlled tar metadata directly over the host repo.
        var remoteCmd =
            $"set -e; cd {QuoteShellWord(remoteParent)}; " +
            $"if [ ! -e {QuoteShellWord(remoteBasename)} ]; then echo 'remote source missing: {EscapeForSingleQuotes(remoteParent + "/" + remoteBasename)}' >&2; exit 2; fi; " +
            $"tar -cf - {QuoteShellWord(remoteBasename)}";

        var sshArgv = BuildSshArgv(opts, remoteCmd);

        using var sshProc = StartSshChild(opts, sshArgv);
        var tempRoot = Path.Combine(hostParent, ".codeybox-stageout-" + Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(tempRoot, "extract");
        var archivePath = Path.Combine(tempRoot, "archive.tar");
        Directory.CreateDirectory(extractRoot);
        try
        {
            var sshErrTask = sshProc.StandardError.ReadToEndAsync(ct);
            await using (var archive = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true))
            {
                await CopyToArchiveWithLimitAsync(
                    sshProc.StandardOutput.BaseStream,
                    archive,
                    opts.StageOutMaxArchiveBytes,
                    remotePath,
                    ct).ConfigureAwait(false);
            }

            await sshProc.WaitForExitAsync(ct).ConfigureAwait(false);
            var sshErr = await sshErrTask.ConfigureAwait(false);

            if (sshProc.ExitCode == SshTransportFailureExitCode)
                throw new RemoteSshTransportException(
                    $"SSH transport failure during StageOutAsync from '{opts.SshTarget}': {TailFor(sshErr)}");

            if (sshProc.ExitCode != 0)
                throw new RemoteSshTransportException(
                    $"Remote tar-create failed (exit {sshProc.ExitCode}) for '{remotePath}': {TailFor(sshErr)}",
                    RemoteSshTransportFailureKind.RemoteCommand);

            ValidateTarArchive(
                archivePath,
                remoteBasename,
                opts.StageOutMaxEntries,
                opts.StageOutMaxExpansionRatio);
            await ExtractTarArchiveAsync(opts, archivePath, extractRoot, ct).ConfigureAwait(false);
            ValidateExtractedTree(extractRoot);

            var extracted = Path.Combine(extractRoot, remoteBasename);
            if (!Directory.Exists(extracted) && !File.Exists(extracted))
                throw ContentValidationException(
                    $"Validated tar archive for '{remotePath}' did not contain expected root '{remoteBasename}'.");

            ReplacePath(extracted, Path.Combine(hostParent, basename));
        }
        finally
        {
            try { if (!sshProc.HasExited) sshProc.Kill(entireProcessTree: true); } catch { }
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static System.Diagnostics.Process StartLocalTar(MultipassRemoteSandboxOptions opts, string hostParent, string basename)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = opts.LocalTarBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = hostParent,
        };
        psi.ArgumentList.Add("-cf");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add(basename);
        var p = new System.Diagnostics.Process { StartInfo = psi };
        if (!p.Start())
            throw new RemoteSshTransportException(
                $"Failed to start local tar at '{opts.LocalTarBinary}'.");
        return p;
    }

    private static System.Diagnostics.Process StartLocalTarExtract(MultipassRemoteSandboxOptions opts, string archivePath, string extractRoot)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = opts.LocalTarBinary,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = extractRoot,
        };
        psi.ArgumentList.Add("-xf");
        psi.ArgumentList.Add(archivePath);
        var p = new System.Diagnostics.Process { StartInfo = psi };
        if (!p.Start())
            throw new RemoteSshTransportException(
                $"Failed to start local tar at '{opts.LocalTarBinary}'.");
        return p;
    }

    private static async Task CopyToArchiveWithLimitAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        string remotePath,
        CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return;

            copied += read;
            if (copied > maxBytes)
            {
                throw new RemoteSshTransportException(
                    $"Remote tar archive for '{remotePath}' exceeded configured StageOutMaxArchiveBytes={maxBytes.ToString(CultureInfo.InvariantCulture)}.",
                    RemoteSshTransportFailureKind.ResourceLimit);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarArchiveAsync(
        MultipassRemoteSandboxOptions opts,
        string archivePath,
        string extractRoot,
        CancellationToken ct)
    {
        using var tarProc = StartLocalTarExtract(opts, archivePath, extractRoot);
        try
        {
            var stderrTask = tarProc.StandardError.ReadToEndAsync(ct);
            await tarProc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (tarProc.ExitCode != 0)
                throw ContentValidationException(
                    $"Local tar-extract failed (exit {tarProc.ExitCode}) for staged archive '{archivePath}': {TailFor(stderr)}");
        }
        finally
        {
            try { if (!tarProc.HasExited) tarProc.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static void ValidateTarArchive(
        string archivePath,
        string expectedRootName,
        int maxEntries,
        double maxExpansionRatio)
    {
        var archiveBytes = new FileInfo(archivePath).Length;
        var maxDeclaredBytes = MaxDeclaredPayloadBytes(archiveBytes, maxExpansionRatio);
        long declaredRegularFileBytes = 0;
        var entryCount = 0;
        var sawRootedEntry = false;
        try
        {
            using var archive = File.OpenRead(archivePath);
            using var reader = new TarReader(archive, leaveOpen: false);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                if (IsTarMetadataEntry(entry.EntryType))
                    continue;

                entryCount++;
                if (entryCount > maxEntries)
                    throw new RemoteSshTransportException(
                        $"Remote tar archive exceeded configured StageOutMaxEntries={maxEntries.ToString(CultureInfo.InvariantCulture)}.",
                        RemoteSshTransportFailureKind.ResourceLimit);

                if (!IsSafeTarEntryType(entry.EntryType))
                    throw ContentValidationException(
                        $"Unsafe tar entry '{entry.Name}' has unsupported type '{entry.EntryType}'.");

                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    if (entry.Length > maxDeclaredBytes - declaredRegularFileBytes)
                    {
                        var attemptedDeclaredBytes = declaredRegularFileBytes > long.MaxValue - entry.Length
                            ? long.MaxValue
                            : declaredRegularFileBytes + entry.Length;
                        throw new RemoteSshTransportException(
                            $"Remote tar archive declared {attemptedDeclaredBytes.ToString(CultureInfo.InvariantCulture)} file bytes, exceeding StageOutMaxExpansionRatio={maxExpansionRatio.ToString(CultureInfo.InvariantCulture)} for archive size {archiveBytes.ToString(CultureInfo.InvariantCulture)}.",
                            RemoteSshTransportFailureKind.ResourceLimit);
                    }
                    declaredRegularFileBytes += entry.Length;
                }

                var name = NormalizeTarEntryName(entry.Name);
                EnsureTarEntryUnderExpectedRoot(name, expectedRootName);
                sawRootedEntry = true;
            }
        }
        catch (RemoteSshTransportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new RemoteSshTransportException(
                $"Remote tar archive failed validation: {ex.Message}",
                RemoteSshTransportFailureKind.ContentValidation,
                ex);
        }

        if (!sawRootedEntry)
            throw ContentValidationException("Remote tar archive contained no extractable entries.");
    }

    private static long MaxDeclaredPayloadBytes(long archiveBytes, double maxExpansionRatio)
    {
        var capped = archiveBytes * maxExpansionRatio;
        if (double.IsInfinity(capped) || capped >= long.MaxValue)
            return long.MaxValue;
        return Math.Max(archiveBytes, (long)Math.Ceiling(capped));
    }

    private static bool IsSafeTarEntryType(TarEntryType type) =>
        type is TarEntryType.Directory
            or TarEntryType.RegularFile
            or TarEntryType.V7RegularFile;

    private static bool IsTarMetadataEntry(TarEntryType type) =>
        type is TarEntryType.ExtendedAttributes
            or TarEntryType.GlobalExtendedAttributes;

    internal static string NormalizeTarEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw ContentValidationException("Tar archive contains an entry with an empty name.");
        if (name.IndexOf('\0') >= 0)
            throw ContentValidationException("Tar archive contains an entry with a NUL byte in its name.");

        var normalized = name.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0 || normalized[0] == '/')
            throw ContentValidationException($"Unsafe tar entry path '{name}'.");

        var parts = normalized.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == "..")
                throw ContentValidationException($"Unsafe tar entry path '{name}'.");
        }

        return normalized;
    }

    private static void EnsureTarEntryUnderExpectedRoot(string entryName, string expectedRootName)
    {
        if (string.Equals(entryName, expectedRootName, StringComparison.Ordinal))
            return;
        if (entryName.StartsWith(expectedRootName + "/", StringComparison.Ordinal))
            return;
        throw ContentValidationException(
            $"Unsafe tar entry '{entryName}' is outside expected root '{expectedRootName}'.");
    }

    internal static void ValidateExtractedTree(string extractRoot)
    {
        var root = Path.GetFullPath(extractRoot);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(path);
            EnsureContainedPath(root, full);
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw ContentValidationException(
                    $"Unsafe extracted filesystem entry '{full}' is a reparse point.");
        }
    }

    private static void EnsureContainedPath(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(normalizedRoot, StringComparison.Ordinal))
            throw ContentValidationException(
                $"Unsafe extracted filesystem entry '{candidate}' escapes staging root '{root}'.");
    }

    internal static RemoteSshTransportException ContentValidationException(string message) =>
        new(message, RemoteSshTransportFailureKind.ContentValidation);

    private static void ReplacePath(string source, string target)
    {
        var backup = target + ".codeybox-backup-" + Guid.NewGuid().ToString("N");
        var hadTarget = PathExists(target);
        if (hadTarget)
            MovePath(target, backup);

        try
        {
            MovePath(source, target);
        }
        catch
        {
            if (hadTarget && !PathExists(target) && PathExists(backup))
                MovePath(backup, target);
            throw;
        }

        if (PathExists(backup))
            DeletePath(backup);
    }

    private static void MovePath(string source, string target)
    {
        if (Directory.Exists(source) && !IsReparsePoint(source))
        {
            Directory.Move(source, target);
            return;
        }

        File.Move(source, target, overwrite: false);
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path) && !IsReparsePoint(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path) || IsReparsePoint(path))
            File.Delete(path);
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path) || IsReparsePoint(path);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static System.Diagnostics.Process StartSshChild(MultipassRemoteSandboxOptions opts, IReadOnlyList<string> sshArgv)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = sshArgv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < sshArgv.Count; i++) psi.ArgumentList.Add(sshArgv[i]);
        var p = new System.Diagnostics.Process { StartInfo = psi };
        if (!StartWithTextBusyRetry(p))
            throw new RemoteSshTransportException(
                $"Failed to start OpenSSH client at '{opts.SshBinary}'.");
        return p;
    }

    // Linux errno for ETXTBSY ("Text file busy"). Process.Start forks and execs;
    // when the orchestrator spawns many processes concurrently (hundreds of VMs
    // fanned across hosts), a sibling fork can transiently inherit an open
    // write fd to the target executable, making exec fail with ETXTBSY even
    // though the binary is complete. It clears in milliseconds once the writer
    // closes, so a brief bounded retry is the correct handling rather than
    // surfacing a spurious start failure.
    private const int ETXTBSY = 26;

    // Retry budget for the ETXTBSY window. The condition clears within a few
    // milliseconds of the concurrent writer closing its fd, so the poll is
    // deliberately tight: up to 20 attempts spaced 10ms apart caps the added
    // start latency at ~200ms before we stop retrying and surface the failure.
    // Kept small on purpose — a genuine (non-transient) start failure must not
    // be masked by a long spin.
    private const int TextBusyMaxAttempts = 20;
    private const int TextBusyRetryDelayMs = 10;

    private static bool StartWithTextBusyRetry(System.Diagnostics.Process p)
        => StartWithTextBusyRetry(
            p.Start,
            static _ => System.Threading.Thread.Sleep(TextBusyRetryDelayMs));

    // Test seam: the retry policy is driven through this overload with an
    // injected start delegate and a no-op / recording sleep so all three
    // branches (retry-then-succeed, exhaustion after TextBusyMaxAttempts, and
    // immediate propagation of a non-ETXTBSY Win32Exception) can be exercised
    // without depending on real fork/exec timing. Production callers use the
    // Process overload above, which forwards Process.Start here.
    internal static bool StartWithTextBusyRetry(Func<bool> start, Action<int> onBusyRetry)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return start();
            }
            catch (System.ComponentModel.Win32Exception ex)
                when (ex.NativeErrorCode == ETXTBSY && attempt < TextBusyMaxAttempts)
            {
                onBusyRetry(attempt);
            }
        }
    }

    private static async Task<ProcessRunResult> RunSshWithBinaryStdinAsync(
        MultipassRemoteSandboxOptions opts,
        IReadOnlyList<string> sshArgv,
        Stream binaryStdin,
        CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = sshArgv[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < sshArgv.Count; i++) psi.ArgumentList.Add(sshArgv[i]);
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        if (!StartWithTextBusyRetry(p))
            return new ProcessRunResult(1, "", "", StartFailed: true);

        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await binaryStdin.CopyToAsync(p.StandardInput.BaseStream, ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (!ct.IsCancellationRequested)
        {
            try { p.StandardInput.Close(); } catch { }
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); } catch { }
            var partialStdout = await ReadCompletedOrEmptyAsync(stdoutTask).ConfigureAwait(false);
            var partialStderr = await ReadCompletedOrEmptyAsync(stderrTask).ConfigureAwait(false);
            if (p.HasExited && p.ExitCode != SshTransportFailureExitCode)
                return new ProcessRunResult(p.ExitCode, partialStdout, partialStderr);

            throw new RemoteSshTransportException(
                $"SSH transport failure streaming binary stdin to '{opts.SshTarget}': {TailFor(partialStderr)}",
                ex);
        }
        finally
        {
            try { p.StandardInput.Close(); } catch { }
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        _ = opts;
        return new ProcessRunResult(p.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadCompletedOrEmptyAsync(Task<string> task)
    {
        try
        {
            return task.IsCompleted ? await task.ConfigureAwait(false) : "";
        }
        catch
        {
            return "";
        }
    }

    private static IReadOnlyList<string> BuildSshArgv(MultipassRemoteSandboxOptions opts, string remoteCommand)
    {
        var argv = new List<string>(16) { opts.SshBinary };
        // BatchMode=yes prevents OpenSSH from prompting for a password
        // interactively when the key auth fails — that prompt would deadlock
        // the orchestrator. The transport always uses key-based auth.
        argv.Add("-o"); argv.Add("BatchMode=yes");
        argv.Add("-o"); argv.Add("StrictHostKeyChecking=" + (opts.AcceptUnknownHostKeys ? "accept-new" : "yes"));
        // Keep-alive so a long-running multipass exec doesn't get dropped by a
        // dead-peer interval — the agent CLI may go ~minutes between writes.
        argv.Add("-o"); argv.Add($"ServerAliveInterval={opts.ServerAliveIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        argv.Add("-o"); argv.Add($"ServerAliveCountMax={opts.ServerAliveCountMax.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        argv.Add("-o"); argv.Add($"ConnectTimeout={opts.ConnectTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        // Multiplexed control sockets would reduce per-exec connection cost,
        // but they're host-disk artifacts that need cleanup and are tricky
        // for our test fakes. Keep the transport stateless for now.
        argv.Add("-o"); argv.Add("ControlMaster=no");
        argv.Add("-o"); argv.Add("ControlPath=none");
        if (!string.IsNullOrWhiteSpace(opts.SshKeyPath))
        {
            argv.Add("-i"); argv.Add(opts.SshKeyPath);
            // IdentitiesOnly forces ssh to use only the provided key, not
            // anything the agent or ~/.ssh/config defaults pulled in. Reduces
            // surprise auth attempts that could lock out a service account.
            argv.Add("-o"); argv.Add("IdentitiesOnly=yes");
        }
        if (opts.SshPort is { } port && port != 22)
        {
            argv.Add("-p"); argv.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (var extra in opts.ExtraSshOptions)
        {
            // Operator-supplied SSH options pass through as -o flags. We
            // validate they look like 'Key=Value' to avoid accidental argv
            // injection (e.g. an option value with a space).
            if (string.IsNullOrWhiteSpace(extra)) continue;
            if (!IsSafeSshOption(extra))
                throw new ArgumentException(
                    $"ExtraSshOptions entry '{extra}' looks unsafe — expected '<Key>=<Value>' with no whitespace.");
            argv.Add("-o"); argv.Add(extra);
        }
        argv.Add(opts.SshTarget);
        argv.Add(remoteCommand);
        return argv;
    }

    private static bool IsSafeSshOption(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var eq = s.IndexOf('=');
        if (eq <= 0 || eq == s.Length - 1) return false;
        foreach (var ch in s)
        {
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') return false;
        }
        return true;
    }

    private static void ValidateOptionsOrThrow(MultipassRemoteSandboxOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.SshTarget))
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.SshTarget is required.");
        if (string.IsNullOrWhiteSpace(opts.SshBinary))
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.SshBinary is required.");
        if (string.IsNullOrWhiteSpace(opts.RemoteMultipassPath))
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.RemoteMultipassPath is required.");
        if (string.IsNullOrWhiteSpace(opts.RemoteStagingRoot))
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.RemoteStagingRoot is required.");
        if (opts.ServerAliveIntervalSeconds <= 0)
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.ServerAliveIntervalSeconds must be > 0.");
        if (opts.ServerAliveCountMax <= 0)
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.ServerAliveCountMax must be > 0.");
        if (opts.ConnectTimeoutSeconds <= 0)
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.ConnectTimeoutSeconds must be > 0.");
        if (opts.StageOutMaxArchiveBytes <= 0)
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.StageOutMaxArchiveBytes must be > 0.");
        if (opts.StageOutMaxEntries <= 0)
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.StageOutMaxEntries must be > 0.");
        if (double.IsNaN(opts.StageOutMaxExpansionRatio)
            || double.IsInfinity(opts.StageOutMaxExpansionRatio)
            || opts.StageOutMaxExpansionRatio < 1.0d)
        {
            throw new InvalidOperationException("MultipassRemoteSandboxOptions.StageOutMaxExpansionRatio must be >= 1.");
        }
    }

    private static void ValidateRemotePath(string remotePath)
    {
        if (!remotePath.StartsWith('/'))
            throw new ArgumentException(
                $"Remote path must be absolute (starts with '/'): '{remotePath}'", nameof(remotePath));
        if (remotePath.Contains('\n') || remotePath.Contains('\r') || remotePath.Contains('\0'))
            throw new ArgumentException(
                $"Remote path contains illegal characters: '{remotePath}'", nameof(remotePath));
    }

    private static string RemoteParent(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    private static string RemoteBasename(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i < 0 ? trimmed : trimmed[(i + 1)..];
    }

    private static string TailFor(string s, int max = 240)
    {
        if (string.IsNullOrEmpty(s)) return "(no stderr)";
        var trimmed = s.Trim();
        if (trimmed.Length <= max) return trimmed;
        return "…" + trimmed[^max..];
    }

    /// <summary>
    /// Shell-quote a single argv list into one composite command string for
    /// the remote bash. We use single quotes; embedded single quotes are
    /// escaped via the standard '\'' bash idiom. This avoids any
    /// interpretation of $, *, backticks, etc. — the remote shell sees
    /// exactly the bytes we intended.
    /// </summary>
    internal static string QuoteShellArgv(IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder(argv.Count * 16);
        for (var i = 0; i < argv.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(QuoteShellWord(argv[i]));
        }
        return sb.ToString();
    }

    internal static string QuoteShellWord(string s)
    {
        if (s.Length == 0) return "''";
        return "'" + EscapeForSingleQuotes(s) + "'";
    }

    private static string EscapeForSingleQuotes(string s) =>
        s.Replace("'", "'\\''", StringComparison.Ordinal);
}
