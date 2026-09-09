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
        var relativePath = AgentCredentialMaterializationPolicy.ValidateRelativeFilePath(
            target.RelativePath,
            nameof(target));
        var destinationOverride = AgentCredentialMaterializationPolicy.ValidateDestinationOverride(
            target.DestinationOverride,
            nameof(target));
        AgentCredentialMaterializationPolicy.ValidatePayload(contents, nameof(contents));

        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv =
            [
                "bash",
                "-c",
                StdinWriterScript,
                "codeybox-credential-materialise",
                ResolveAllowedRoot(target.Root),
                relativePath,
                destinationOverride,
                ResolveOverwritePolicy(overwritePolicy),
                SandboxConventions.CredentialsTmpfsBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ],
            Stdin = contents,
        }, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new SandboxCredentialFileWriteException(
                relativePath,
                result.ExitCode,
                result.Stdout,
                result.Stderr,
                result.ExecutionUnavailable);
        }
    }

    /// <summary>
    /// Validates every credential file a run can materialise as one bounded
    /// plan before the first sandbox exec. <paramref name="credential"/>'s
    /// direct files and the environment-backed <paramref name="additional"/>
    /// requests share one count, destination-uniqueness, and byte budget.
    /// </summary>
    public static void ValidateMaterializationPlan(
        AgentCredential? credential,
        IReadOnlyList<SandboxCredentialFileMaterialization> additional)
    {
        ArgumentNullException.ThrowIfNull(additional);
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        var entriesSeen = 0;
        long aggregateBytes = 0;
        var hasAbsoluteHomeDestination = false;
        var hasRelativeHomeDestination = false;

        if (credential is not null)
        {
            foreach (var (relativePath, contents) in credential.Files)
            {
                Add(
                    new SandboxCredentialFileTarget(
                        SandboxCredentialFileRoot.CredentialsDirectory,
                        relativePath),
                    contents);
            }
        }

        foreach (var materialization in additional)
        {
            if (materialization is null)
                throw new ArgumentException("Credential materialization plans cannot contain null entries.", nameof(additional));
            Add(materialization.Target, materialization.Contents);
        }

        void Add(SandboxCredentialFileTarget target, string? contents)
        {
            if (++entriesSeen > AgentCredentialMaterializationPolicy.MaximumFiles)
            {
                throw new ArgumentException(
                    $"A run cannot materialise more than {AgentCredentialMaterializationPolicy.MaximumFiles} credential files.",
                    nameof(additional));
            }
            ArgumentNullException.ThrowIfNull(target);
            var relativePath = AgentCredentialMaterializationPolicy.ValidateRelativeFilePath(
                target.RelativePath,
                nameof(additional));
            var destinationOverride = AgentCredentialMaterializationPolicy.ValidateDestinationOverride(
                target.DestinationOverride,
                nameof(additional));
            if (target.Root == SandboxCredentialFileRoot.Home)
            {
                var isAbsolute = destinationOverride.StartsWith("/", StringComparison.Ordinal);
                if ((isAbsolute && hasRelativeHomeDestination)
                    || (!isAbsolute && hasAbsoluteHomeDestination))
                {
                    throw new ArgumentException(
                        "Absolute and HOME-relative credential destinations cannot coexist in one materialization plan.",
                        nameof(additional));
                }
                hasAbsoluteHomeDestination |= isAbsolute;
                hasRelativeHomeDestination |= !isAbsolute;
            }
            var identity = BuildMaterializationIdentity(target.Root, relativePath, destinationOverride);
            if (!destinations.Add(identity))
                throw new ArgumentException("Credential materialization plan contains duplicate destinations.", nameof(additional));

            AddBytes(System.Text.Encoding.UTF8.GetByteCount(relativePath));
            if (destinationOverride.Length > 0)
                AddBytes(System.Text.Encoding.UTF8.GetByteCount(destinationOverride));
            if (contents is not null)
                AddBytes(AgentCredentialMaterializationPolicy.GetPayloadAllocationBytes(contents, nameof(additional)));
        }

        void AddBytes(long bytes)
        {
            if (bytes > AgentCredentialMaterializationPolicy.MaterializationBudgetBytes - aggregateBytes)
                throw new ArgumentException("Credential materialization plan exceeds the sandbox credential budget.", nameof(additional));
            aggregateBytes += bytes;
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
        => AgentCredentialMaterializationPolicy.ValidateEnvironmentVariableName(value, fieldName);

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
        => _ = AgentCredentialMaterializationPolicy.ValidateRelativeFilePath(value, fieldName);

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string BuildMaterializationIdentity(
        SandboxCredentialFileRoot root,
        string relativePath,
        string destinationOverride)
    {
        if (root is not (SandboxCredentialFileRoot.Home or SandboxCredentialFileRoot.CredentialsDirectory))
            throw new ArgumentOutOfRangeException(nameof(root), root, "Unsupported credential file root.");
        var destination = destinationOverride;
        var kind = "relative";
        if (destination.Length == 0)
        {
            destination = relativePath;
        }
        else if (destination.StartsWith("$HOME/", StringComparison.Ordinal))
        {
            destination = destination[6..];
        }
        else if (destination.StartsWith("~/", StringComparison.Ordinal))
        {
            destination = destination[2..];
        }
        else if (destination.StartsWith("/", StringComparison.Ordinal))
        {
            kind = "absolute";
        }

        return $"{(int)root}:{kind}:{destination}";
    }

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
        import unicodedata

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
        MAX_PATH_BYTES = {{AgentCredentialMaterializationPolicy.MaximumPathUtf8Bytes}}
        MAX_PATH_SEGMENT_BYTES = {{AgentCredentialMaterializationPolicy.MaximumPathSegmentUtf8Bytes}}
        MAX_PATH_SEGMENTS = {{AgentCredentialMaterializationPolicy.MaximumPathSegments}}

        def fail(message):
            print(message, file=sys.stderr)
            raise SystemExit(2)

        def path_bytes(label, value):
            try:
                encoded = value.encode("utf-8", errors="strict")
            except UnicodeError:
                fail(f"credential {label} contains invalid Unicode")
            if len(encoded) > MAX_PATH_BYTES:
                fail(f"credential {label} exceeds size limit")
            return len(encoded)

        def unsafe_path_character(value):
            return any(
                unicodedata.category(character) in ("Cc", "Cs")
                or character in ("\u0085", "\u2028", "\u2029")
                for character in value)

        def validate_segments(label, parts):
            if not parts or len(parts) > MAX_PATH_SEGMENTS:
                fail(f"credential {label} has an invalid component count")
            for part in parts:
                if not part or part in (".", ".."):
                    fail(f"credential {label} contains empty or traversal components")
                try:
                    part_bytes = len(part.encode("utf-8", errors="strict"))
                except UnicodeError:
                    fail(f"credential {label} contains invalid Unicode")
                if part_bytes > MAX_PATH_SEGMENT_BYTES:
                    fail(f"credential {label} component exceeds size limit")

        def validate_path_text(label, value):
            if not value or value.isspace() or unsafe_path_character(value) or "\\" in value:
                fail(f"credential {label} is invalid")
            path_bytes(label, value)

        def validate_relative_path(label, value):
            validate_path_text(label, value)
            if value.startswith("/") or value.endswith("/") or "//" in value:
                fail(f"credential {label} is not a canonical relative path")
            parts = value.split("/")
            validate_segments(label, parts)
            return parts

        def validate_absolute_path(label, value):
            validate_path_text(label, value)
            if not value.startswith("/") or value == "/" or value.startswith("//") or value.endswith("/") or "//" in value:
                fail(f"credential {label} is not a canonical absolute file path")
            validate_segments(label, value[1:].split("/"))

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
            validate_absolute_path("allowed root", root_argument)
            root = os.path.realpath(root_argument)
            validate_absolute_path("resolved allowed root", root)
            if not os.path.isdir(root):
                fail("credential allowed root is not accessible")
            return root

        def resolve_destination(root, rel, override):
            validate_relative_path("path", rel)

            if override:
                if override.startswith("$HOME/"):
                    relative_override = override[len("$HOME/"):]
                    validate_relative_path("destination", relative_override)
                    candidate = os.path.join(root, relative_override)
                elif override.startswith("~/"):
                    relative_override = override[2:]
                    validate_relative_path("destination", relative_override)
                    candidate = os.path.join(root, relative_override)
                elif os.path.isabs(override):
                    validate_absolute_path("destination", override)
                    candidate = override
                else:
                    validate_relative_path("destination", override)
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

        def harden_existing_nonempty_regular(parent_fd, file_name):
            try:
                fd = os.open(file_name, os.O_RDONLY | O_NOFOLLOW | O_CLOEXEC, dir_fd=parent_fd)
            except FileNotFoundError:
                return None
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
                if st.st_size == 0:
                    return None
                os.fchmod(fd, FILE_MODE)
                hardened = os.fstat(fd)
                if stat.S_IMODE(hardened.st_mode) != FILE_MODE:
                    fail("credential destination file mode could not be hardened")
                return hardened.st_dev, hardened.st_ino
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

        def verify_destination(root_fd, parent_parts, parent_fd, file_name, expected_identity):
            verified_parent_fd = open_existing_parent_directory(root_fd, parent_parts)
            try:
                if not same_file(parent_fd, verified_parent_fd):
                    fail("credential destination parent path changed during write")
                verified_fd = os.open(file_name, os.O_RDONLY | O_NOFOLLOW | O_CLOEXEC, dir_fd=verified_parent_fd)
                try:
                    verified = os.fstat(verified_fd)
                    if not stat.S_ISREG(verified.st_mode):
                        fail("credential destination exists and is not a regular file")
                    if (verified.st_dev, verified.st_ino) != expected_identity:
                        fail("credential destination changed during write")
                    if stat.S_IMODE(verified.st_mode) != FILE_MODE:
                        fail("credential destination file mode is not private")
                finally:
                    os.close(verified_fd)
            finally:
                os.close(verified_parent_fd)

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
            data = read_bounded(max_bytes)

            root = resolve_root(root_argument)
            rel_destination = resolve_destination(root, rel, override)
            parts = validate_relative_path("resolved destination", rel_destination)
            file_name = parts[-1]
            parent_parts = parts[:-1]

            root_fd = os.open(root, os.O_RDONLY | O_DIRECTORY | O_CLOEXEC)
            parent_fd = None
            try:
                parent_fd = ensure_parent_directory(root_fd, parent_parts)
                if overwrite_policy == PRESERVE_NONEMPTY:
                    preserved_identity = harden_existing_nonempty_regular(parent_fd, file_name)
                    if preserved_identity is not None:
                        verify_destination(
                            root_fd,
                            parent_parts,
                            parent_fd,
                            file_name,
                            preserved_identity)
                        return
                reject_unsupported_destination_type(parent_fd, file_name)
                written_identity = write_file(parent_fd, file_name, data)
                verify_destination(
                    root_fd,
                    parent_parts,
                    parent_fd,
                    file_name,
                    written_identity)
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
/// must be a bounded canonical relative Unix file path beneath <paramref name="Root"/>.
/// <paramref name="DestinationOverride"/> may be absolute, relative,
/// <c>~/...</c>, or <c>$HOME/...</c> when <paramref name="Root"/> is
/// <see cref="SandboxCredentialFileRoot.Home"/>; the in-sandbox writer
/// normalizes it and rejects any destination outside the selected root.
/// Writes atomically replace the destination in overwrite mode, or preserve
/// the contents of an existing non-empty regular file while tightening its
/// mode to 0600 in preserve-nonempty mode.
/// </summary>
public sealed record SandboxCredentialFileTarget(
    SandboxCredentialFileRoot Root,
    string RelativePath,
    string? DestinationOverride = null);

/// <summary>
/// One prospective credential-file write used for whole-run preflight. A null
/// <paramref name="Contents"/> represents a payload already present in the
/// sandbox environment; the in-sandbox writer still enforces its byte limit.
/// </summary>
public sealed record SandboxCredentialFileMaterialization(
    SandboxCredentialFileTarget Target,
    string? Contents);

public sealed class SandboxCredentialFileWriteException : Exception
{
    public SandboxCredentialFileWriteException(
        string relativePath,
        int exitCode,
        string? stdout,
        string? stderr,
        bool executionUnavailable)
        : base($"Sandbox credential file write failed with exit code {exitCode}.")
    {
        RelativePath = relativePath;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        ExecutionUnavailable = executionUnavailable;
    }

    public string RelativePath { get; }
    public int ExitCode { get; }
    public string? Stdout { get; }
    public string? Stderr { get; }
    public bool ExecutionUnavailable { get; }
}
