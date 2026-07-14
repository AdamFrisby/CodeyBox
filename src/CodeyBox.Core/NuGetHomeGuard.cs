namespace CodeyBox.Core;

/// <summary>
/// Single source of truth for the POSIX-shell preamble that makes a
/// <c>dotnet</c> invocation survive an audit sandbox whose NuGet user-settings
/// directory (<c>$HOME/.nuget/NuGet</c>) is unusable.
///
/// <para>Audit sandboxes have shipped <c>$HOME/.nuget</c> owned by another uid
/// (e.g. root, mode 755) or at mode 000. On first restore NuGet must create and
/// read <c>$HOME/.nuget/NuGet/NuGet.Config</c>; when the directory is not
/// writable that aborts every project restore with "Failed to read NuGet.Config
/// due to unauthorized access" before a single project builds -- failing the
/// gate for reasons unrelated to the diff under review. NuGet resolves the
/// user-settings path from <c>$HOME</c> unconditionally, so neither a
/// repo-committed <c>nuget.config</c>, a <c>--configfile</c>, nor an MSBuild
/// property can redirect that read; the only reliable lever is <c>$HOME</c>.</para>
///
/// <para>The preamble probes the actual operation NuGet performs (create the
/// settings directory and write a file inside it) rather than inferring
/// usability from permission bits, then relocates <c>$HOME</c> to a writable
/// scratch directory when the probe fails, reusing any pre-populated package
/// cache so the relocated restore stays offline. It is idempotent and a no-op
/// when the real NuGet home is already usable, so it is safe to prepend to any
/// <c>dotnet</c> command. It assumes <c>set -u</c> semantics are tolerable (all
/// variable reads are guarded) and is safe under <c>set -e</c> (every command
/// that may fail runs inside a condition or is neutralised with <c>|| true</c>).</para>
/// </summary>
public static class NuGetHomeGuard
{
    /// <summary>
    /// The shell preamble. Prepend it to a <c>dotnet</c> command in the same
    /// shell (e.g. <c>sh -c "&lt;preamble&gt;\nexec \"$@\""</c>) so the relocation
    /// applies to the command that follows. Pure shell -- contains no host-side
    /// interpolation -- so it can be embedded verbatim into a larger script.
    /// </summary>
    public const string RelocationPreamble = """
        # NuGet reads (and, on first restore, creates) the user-settings file
        # $HOME/.nuget/NuGet/NuGet.Config. Audit sandboxes have shipped that
        # directory owned by another uid or mode 000, which aborts restore with an
        # "unauthorized access" error before any project builds. Detect an unusable
        # settings location and relocate HOME to a writable scratch directory,
        # preserving a readable package cache so the restore stays offline.
        #
        # Decide usability by PROBING the actual first-restore operation -- creating
        # the settings directory and writing a file inside it -- rather than
        # inferring it from permission bits, which ACLs, read-only mounts, overlay
        # filesystems, and a non-directory occupying the settings path can all defeat.
        codeybox_nuget_home="${HOME:-/nonexistent}/.nuget"
        codeybox_settings_dir="$codeybox_nuget_home/NuGet"
        codeybox_settings_file="$codeybox_settings_dir/NuGet.Config"
        codeybox_write_probe="$codeybox_settings_dir/.codeybox-write-probe.$$"
        codeybox_nuget_usable=1
        if mkdir -p "$codeybox_settings_dir" 2>/dev/null \
           && touch "$codeybox_write_probe" 2>/dev/null; then
          rm -f "$codeybox_write_probe" 2>/dev/null || true
          # The directory is writable; NuGet must also READ an existing config.
          if [ -e "$codeybox_settings_file" ] && [ ! -r "$codeybox_settings_file" ]; then
            codeybox_nuget_usable=0
          fi
        else
          codeybox_nuget_usable=0
        fi
        if [ "$codeybox_nuget_usable" -eq 0 ]; then
          echo "CodeyBox: $codeybox_settings_dir is unusable for NuGet; relocating HOME to a writable scratch directory." >&2
          # Preserve the pre-populated package cache under the (soon-abandoned) real
          # HOME so the relocated restore stays offline. A WRITABLE cache can back
          # the global packages folder, which NuGet both reads and writes. A cache
          # that is readable but NOT writable -- e.g. a root-owned shared mount --
          # cannot: NuGet writes .nupkg.metadata markers into the global folder. Its
          # supported read-only mechanism is a fallback package folder, which NuGet
          # only ever reads. Classify against the ORIGINAL HOME before relocating.
          codeybox_cache="$codeybox_nuget_home/packages"
          codeybox_cache_mode=none
          if [ -z "${NUGET_PACKAGES:-}" ] && [ -d "$codeybox_cache" ] && [ -r "$codeybox_cache" ]; then
            if [ -w "$codeybox_cache" ]; then
              codeybox_cache_mode=writable
            else
              codeybox_cache_mode=readonly
            fi
          fi
          HOME="$(mktemp -d 2>/dev/null || printf '%s' "${TMPDIR:-/tmp}/codeybox-nuget-home-$$")"
          export HOME
          mkdir -p "$HOME"
          if [ "$codeybox_cache_mode" = writable ]; then
            NUGET_PACKAGES="$codeybox_cache"
            export NUGET_PACKAGES
          elif [ "$codeybox_cache_mode" = readonly ]; then
            # Register the read-only cache as a fallback folder in the writable
            # relocated HOME so an offline restore resolves every pre-populated
            # package without writing to the cache. $codeybox_cache is a
            # sandbox-provided HOME path; a pathological value only yields malformed
            # XML that NuGet ignores (fails safe -- no sink beyond this config file).
            codeybox_fallback_settings="$HOME/.nuget/NuGet"
            if mkdir -p "$codeybox_fallback_settings" 2>/dev/null; then
              {
                printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>'
                printf '%s\n' '<configuration>'
                printf '%s\n' '  <fallbackPackageFolders>'
                printf '    <add key="codeybox-prewarmed" value="%s" />\n' "$codeybox_cache"
                printf '%s\n' '  </fallbackPackageFolders>'
                printf '%s\n' '</configuration>'
              } > "$codeybox_fallback_settings/NuGet.Config" 2>/dev/null || true
            fi
          fi
        fi
        """;

    /// <summary>
    /// Returns <see cref="RelocationPreamble"/> when <paramref name="argv"/> is a
    /// <c>dotnet</c> invocation (which performs a NuGet restore and therefore
    /// touches the user-settings directory), otherwise <c>null</c>. Centralises
    /// the "which commands need the NuGet-home guard" rule so callers do not each
    /// re-encode it.
    /// </summary>
    public static string? PreambleForCommand(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return argv.Count > 0 && string.Equals(argv[0], "dotnet", StringComparison.Ordinal)
            ? RelocationPreamble
            : null;
    }
}
