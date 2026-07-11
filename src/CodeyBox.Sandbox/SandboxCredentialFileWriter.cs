using CodeyBox.Core;

namespace CodeyBox.Sandbox;

/// <summary>
/// Writes credential payloads to a caller-selected allowlisted root inside a
/// sandbox. The implementation requires Python 3 in the sandbox image so it
/// can use descriptor-relative, no-follow filesystem operations. Payloads are
/// accepted only through stdin and are capped at
/// <see cref="SandboxConventions.CredentialsTmpfsBytes"/> before buffering.
/// </summary>
public static class SandboxCredentialFileWriter
{
    private const int MaximumEnvironmentVariableNameLength = 128;
    private const string OverwritePolicyValue = "overwrite";
    private const string PreserveNonEmptyPolicyValue = "preserve-nonempty";

    /// <summary>
    /// Writes <paramref name="contents"/> atomically with mode 0600 below the
    /// selected root. Parent directories are created or tightened to 0700.
    /// Existing leaf symlinks are atomically replaced in overwrite mode and
    /// rejected in preserve mode; neither mode follows them.
    /// </summary>
    public static async Task WriteAsync(
        ISandbox sandbox,
        SandboxCredentialFileTarget target,
        string contents,
        SandboxCredentialOverwritePolicy overwritePolicy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(contents);
        ValidateRelativePath(target.RelativePath, nameof(target));
        if (System.Text.Encoding.UTF8.GetByteCount(contents) > SandboxConventions.CredentialsTmpfsBytes)
            throw new ArgumentException("Credential payload exceeds the size limit.", nameof(contents));

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash",
                "-c",
                StdinWriterScript,
                "codeybox-credential-materialise",
                ResolveAllowedRoot(target.Root),
                target.RelativePath,
                target.DestinationOverride ?? string.Empty,
                ResolveOverwritePolicy(overwritePolicy),
                SandboxConventions.CredentialsTmpfsBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ],
            Stdin = contents,
        }, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new SandboxCredentialFileWriteException(
                target.RelativePath,
                result.ExitCode,
                result.Stdout,
                result.Stderr);
        }
    }

    /// <summary>
    /// Builds the Python-3-backed Bash materialiser used by in-sandbox smoke
    /// probes. The environment names are validated POSIX identifiers before
    /// interpolation, and credential bytes remain in the sandbox environment
    /// rather than argv.
    /// </summary>
    public static string BuildEnvironmentMaterialisationScript(
        string payloadEnvironmentVariable,
        string homeRelativePath,
        string? destinationEnvironmentVariable = null,
        SandboxCredentialOverwritePolicy overwritePolicy = SandboxCredentialOverwritePolicy.PreserveNonEmpty)
    {
        ValidateEnvironmentVariableName(payloadEnvironmentVariable, nameof(payloadEnvironmentVariable));
        if (destinationEnvironmentVariable is not null)
            ValidateEnvironmentVariableName(destinationEnvironmentVariable, nameof(destinationEnvironmentVariable));
        ValidateRelativePath(homeRelativePath, nameof(homeRelativePath));

        var script = WriterFunctionScript +
            "\nvalue=\"${" + payloadEnvironmentVariable + ":-}\"\n" +
            "if [ -z \"$value\" ]; then exit 0; fi\n";
        script += destinationEnvironmentVariable is null
            ? "dest_override=\"\"\n"
            : "dest_override=\"${" + destinationEnvironmentVariable + ":-}\"\n";
        return script +
            "printf '%s' \"$value\" | codeybox_write_credential_file \"$HOME\" " +
            ShellQuote(homeRelativePath) +
            " \"$dest_override\" " + ShellQuote(ResolveOverwritePolicy(overwritePolicy)) +
            " " + SandboxConventions.CredentialsTmpfsBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
    }

    /// <summary>Validates a POSIX environment-variable identifier.</summary>
    public static void ValidateEnvironmentVariableName(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Environment variable name must be non-empty.", fieldName);
        if (value.Length > MaximumEnvironmentVariableNameLength)
            throw new ArgumentException("Environment variable name exceeds the size limit.", fieldName);
        if (!IsAsciiLetter(value[0]) && value[0] != '_')
            throw new ArgumentException("Environment variable name is not a POSIX identifier.", fieldName);
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (IsAsciiLetter(character) || character is >= '0' and <= '9' || character == '_')
                continue;
            throw new ArgumentException("Environment variable name is not a POSIX identifier.", fieldName);
        }
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string ResolveAllowedRoot(SandboxCredentialFileRoot root) => root switch
    {
        SandboxCredentialFileRoot.Home => "$HOME",
        SandboxCredentialFileRoot.CredentialsDirectory => SandboxConventions.CredentialsDir,
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "Unsupported credential file root."),
    };

    private static string ResolveOverwritePolicy(SandboxCredentialOverwritePolicy overwritePolicy) => overwritePolicy switch
    {
        SandboxCredentialOverwritePolicy.Overwrite => OverwritePolicyValue,
        SandboxCredentialOverwritePolicy.PreserveNonEmpty => PreserveNonEmptyPolicyValue,
        _ => throw new ArgumentOutOfRangeException(nameof(overwritePolicy), overwritePolicy, "Unsupported credential overwrite policy."),
    };

    public static void ValidateRelativePath(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Credential relative path must be non-empty.", fieldName);
        if (value.StartsWith('/'))
            throw new ArgumentException($"Credential path must be relative to its allowed root: {value}", fieldName);

        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                throw new ArgumentException($"Credential path must not contain traversal segments: {value}", fieldName);
        }
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static readonly string WriterFunctionScript =
        $$"""
        set -eu
        codeybox_fail() { printf '%s\n' "$1" >&2; exit 2; }
        codeybox_credential_writer_py=$(cat <<'PY'
        import errno
        import os
        import secrets
        import stat
        import sys

        OVERWRITE = "{{OverwritePolicyValue}}"
        PRESERVE_NONEMPTY = "{{PreserveNonEmptyPolicyValue}}"
        TEMP_FILE_ALLOCATION_ATTEMPTS = 16
        # 64 random bits keeps accidental temp-name collisions negligible while
        # the bounded retry count prevents an attacker-controlled directory from
        # driving an unbounded allocation loop.
        TEMP_SUFFIX_BYTES = 8
        O_DIRECTORY = getattr(os, "O_DIRECTORY", 0)
        O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)
        O_CLOEXEC = getattr(os, "O_CLOEXEC", 0)
        DIRECTORY_MODE = 0o700
        FILE_MODE = 0o600

        def fail(message):
            print(message, file=sys.stderr)
            raise SystemExit(2)

        def reject_dot_segments(label, value):
            for segment in value.split("/"):
                if segment in (".", ".."):
                    fail(f"credential {label} contains traversal")

        def split_relative(value):
            parts = [part for part in value.split(os.sep) if part]
            for part in parts:
                if part in (".", ".."):
                    fail("credential destination contains traversal")
            return parts

        def open_directory(parent_fd, name):
            try:
                return os.open(
                    name,
                    os.O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC,
                    dir_fd=parent_fd)
            except OSError as ex:
                if ex.errno == errno.ELOOP:
                    fail("credential destination parent is a symlink")
                if ex.errno == errno.ENOTDIR:
                    fail("credential destination parent is not a directory")
                raise

        def ensure_parent_directory(root_fd, parent_parts):
            current_fd = os.dup(root_fd)
            for part in parent_parts:
                try:
                    next_fd = open_directory(current_fd, part)
                except FileNotFoundError:
                    os.mkdir(part, DIRECTORY_MODE, dir_fd=current_fd)
                    next_fd = open_directory(current_fd, part)

                try:
                    mode = os.fstat(next_fd).st_mode
                    if not stat.S_ISDIR(mode):
                        fail("credential destination parent is not a directory")
                    os.fchmod(next_fd, DIRECTORY_MODE)
                finally:
                    os.close(current_fd)
                current_fd = next_fd
            return current_fd

        def open_existing_parent_directory(root_fd, parent_parts):
            current_fd = os.dup(root_fd)
            for part in parent_parts:
                next_fd = open_directory(current_fd, part)
                os.close(current_fd)
                current_fd = next_fd
            return current_fd

        def same_file(left_fd, right_fd):
            left = os.fstat(left_fd)
            right = os.fstat(right_fd)
            return left.st_dev == right.st_dev and left.st_ino == right.st_ino

        def resolve_root(root_argument):
            if root_argument == "$HOME":
                root_argument = os.environ.get("HOME")
            if not root_argument or not os.path.isabs(root_argument):
                fail("credential allowed root is invalid")
            root = os.path.realpath(root_argument)
            if not os.path.isdir(root):
                fail("credential allowed root is not accessible")
            return root

        def resolve_destination(root, rel, override):
            if not rel or rel.startswith("/"):
                fail("credential path must be root-relative")
            reject_dot_segments("path", rel)

            if override:
                reject_dot_segments("destination", override)
                if override.startswith("$HOME/"):
                    candidate = os.path.join(root, override[len("$HOME/"):])
                elif override.startswith("~/"):
                    candidate = os.path.join(root, override[2:])
                elif os.path.isabs(override):
                    candidate = override
                else:
                    candidate = os.path.join(root, override)
            else:
                candidate = os.path.join(root, rel)

            destination = os.path.normpath(candidate)
            try:
                common = os.path.commonpath([root, destination])
            except ValueError:
                fail("credential destination escapes allowed root")
            if common != root:
                fail("credential destination escapes allowed root")
            return os.path.relpath(destination, root)

        def existing_destination_is_nonempty_regular(parent_fd, file_name):
            try:
                fd = os.open(file_name, os.O_RDONLY | O_NOFOLLOW | O_CLOEXEC, dir_fd=parent_fd)
            except FileNotFoundError:
                return False
            except OSError as ex:
                if ex.errno == errno.ELOOP:
                    fail("credential destination file is a symlink")
                if ex.errno == errno.ENOTDIR:
                    fail("credential destination exists and is not a regular file")
                raise

            try:
                st = os.fstat(fd)
                if not stat.S_ISREG(st.st_mode):
                    fail("credential destination exists and is not a regular file")
                return st.st_size > 0
            finally:
                os.close(fd)

        def reject_unsupported_destination_type(parent_fd, file_name):
            try:
                st = os.stat(file_name, dir_fd=parent_fd, follow_symlinks=False)
            except FileNotFoundError:
                return
            # Overwrite mode replaces the final directory entry atomically, so
            # a leaf symlink is safe: credential bytes are never written through it.
            if stat.S_ISLNK(st.st_mode):
                return
            if not stat.S_ISREG(st.st_mode):
                fail("credential destination exists and is not a regular file")

        def write_file(parent_fd, file_name, data):
            tmp_name = None
            tmp_fd = None
            for _ in range(TEMP_FILE_ALLOCATION_ATTEMPTS):
                candidate = f".{file_name}.tmp.{secrets.token_hex(TEMP_SUFFIX_BYTES)}"
                try:
                    tmp_fd = os.open(
                        candidate,
                        os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_NOFOLLOW | O_CLOEXEC,
                        FILE_MODE,
                        dir_fd=parent_fd)
                    tmp_name = candidate
                    break
                except FileExistsError:
                    continue
            if tmp_fd is None or tmp_name is None:
                fail("credential temporary file name could not be allocated")

            try:
                with os.fdopen(tmp_fd, "wb", closefd=True) as handle:
                    handle.write(data)
                    handle.flush()
                    os.fchmod(handle.fileno(), FILE_MODE)
                os.replace(tmp_name, file_name, src_dir_fd=parent_fd, dst_dir_fd=parent_fd)
                written = os.stat(file_name, dir_fd=parent_fd, follow_symlinks=False)
                return written.st_dev, written.st_ino
            finally:
                try:
                    os.unlink(tmp_name, dir_fd=parent_fd)
                except FileNotFoundError:
                    pass

        def read_bounded(max_bytes):
            data = sys.stdin.buffer.read(max_bytes + 1)
            if len(data) > max_bytes:
                fail("credential payload exceeds size limit")
            return data

        def main():
            if len(sys.argv) != 6:
                fail("credential writer invoked with invalid arguments")
            root_argument, rel, override, overwrite_policy, max_bytes_raw = sys.argv[1:]
            if overwrite_policy not in (OVERWRITE, PRESERVE_NONEMPTY):
                fail("credential overwrite policy is invalid")
            try:
                max_bytes = int(max_bytes_raw)
            except ValueError:
                fail("credential size limit is invalid")
            if max_bytes <= 0:
                fail("credential size limit is invalid")

            root = resolve_root(root_argument)
            rel_destination = resolve_destination(root, rel, override)
            parts = split_relative(rel_destination)
            if not parts:
                fail("credential destination file name is empty")
            file_name = parts[-1]
            parent_parts = parts[:-1]

            root_fd = os.open(root, os.O_RDONLY | O_DIRECTORY | O_CLOEXEC)
            parent_fd = None
            try:
                parent_fd = ensure_parent_directory(root_fd, parent_parts)
                if overwrite_policy == PRESERVE_NONEMPTY and existing_destination_is_nonempty_regular(parent_fd, file_name):
                    return
                reject_unsupported_destination_type(parent_fd, file_name)
                written_identity = write_file(parent_fd, file_name, read_bounded(max_bytes))

                verified_parent_fd = open_existing_parent_directory(root_fd, parent_parts)
                try:
                    if not same_file(parent_fd, verified_parent_fd):
                        fail("credential destination parent path changed during write")
                    verified_fd = os.open(file_name, os.O_RDONLY | O_NOFOLLOW | O_CLOEXEC, dir_fd=verified_parent_fd)
                    try:
                        verified = os.fstat(verified_fd)
                        if (verified.st_dev, verified.st_ino) != written_identity:
                            fail("credential destination changed during write")
                    finally:
                        os.close(verified_fd)
                finally:
                    os.close(verified_parent_fd)
            finally:
                if parent_fd is not None:
                    os.close(parent_fd)
                os.close(root_fd)

        try:
            main()
        except SystemExit:
            raise
        except Exception as ex:
            fail(f"credential materialisation failed: {ex.__class__.__name__}: {ex}")
        PY
        )
        codeybox_write_credential_file() {
          command -v python3 >/dev/null 2>&1 || codeybox_fail 'python3 is required to materialise credential files'
          python3 -c "$codeybox_credential_writer_py" "$1" "$2" "${3:-}" "$4" "$5"
        }
        """;

    private static readonly string StdinWriterScript =
        WriterFunctionScript +
        "\ncodeybox_write_credential_file \"$1\" \"$2\" \"${3:-}\" \"$4\" \"$5\"\n";
}

public enum SandboxCredentialFileRoot
{
    Home,
    CredentialsDirectory,
}

public enum SandboxCredentialOverwritePolicy
{
    Overwrite,
    PreserveNonEmpty,
}

/// <summary>
/// Describes one credential file write inside a sandbox. <paramref name="RelativePath"/>
/// must be non-empty and relative to <paramref name="Root"/> with no traversal
/// segments. <paramref name="DestinationOverride"/> may be absolute, relative,
/// <c>~/...</c>, or <c>$HOME/...</c> when <paramref name="Root"/> is
/// <see cref="SandboxCredentialFileRoot.Home"/>; the in-sandbox writer
/// normalizes it and rejects any destination outside the selected root.
/// Writes atomically replace the destination in overwrite mode, or leave an
/// existing non-empty regular file unchanged in preserve-nonempty mode.
/// </summary>
public sealed record SandboxCredentialFileTarget(
    SandboxCredentialFileRoot Root,
    string RelativePath,
    string? DestinationOverride = null);

public sealed class SandboxCredentialFileWriteException : Exception
{
    public SandboxCredentialFileWriteException(
        string relativePath,
        int exitCode,
        string? stdout,
        string? stderr)
        : base($"Sandbox credential file write failed with exit code {exitCode}.")
    {
        RelativePath = relativePath;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
    }

    public string RelativePath { get; }
    public int ExitCode { get; }
    public string? Stdout { get; }
    public string? Stderr { get; }
}
