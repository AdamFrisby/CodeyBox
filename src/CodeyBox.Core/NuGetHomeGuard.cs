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
/// property can redirect that read; the only reliable lever is the filesystem
/// state of <c>$HOME/.nuget</c> (which the build uid owns via <c>$HOME</c>) or
/// <c>$HOME</c> itself.</para>
///
/// <para>Two complementary strategies exist:</para>
/// <list type="bullet">
///   <item><description><see cref="RelocationPreamble"/> relocates <c>$HOME</c>
///   to a writable scratch directory for the CURRENT shell. Non-destructive,
///   but its effect is confined to the shell that runs it.</description></item>
///   <item><description><see cref="InPlaceRepairPreamble"/> repairs the shared
///   <c>$HOME/.nuget</c> itself (renames the foreign-owned directory aside and
///   recreates it uid-owned). Its effect is visible to EVERY sibling
///   <c>dotnet</c> invocation in the same sandbox -- including a later gate that
///   runs raw, without any preamble -- which relocation cannot achieve. Prepend
///   it before <see cref="RelocationPreamble"/> so relocation remains the
///   fallback when the home cannot be repaired without root.</description></item>
/// </list>
///
/// <para>Both preambles probe the actual operation NuGet performs (create the
/// settings directory and write a file inside it) rather than inferring
/// usability from permission bits, reuse any pre-populated package cache so the
/// restore stays offline, are idempotent and a no-op when the real NuGet home
/// is already usable, and are safe under <c>set -eu</c> (every read is guarded;
/// every command that may fail runs inside a condition or is neutralised with
/// <c>|| true</c>).</para>
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
    /// Shell preamble that repairs a foreign-owned <c>$HOME/.nuget</c> IN PLACE
    /// rather than relocating <c>$HOME</c>. When the build uid owns <c>$HOME</c>
    /// (the usual case even if <c>.nuget</c> itself is foreign-owned) it can
    /// rename the unusable directory aside and recreate it uid-owned, preserving
    /// the pre-populated package cache via symlink so restores stay offline.
    ///
    /// <para>Unlike <see cref="RelocationPreamble"/>, whose effect is confined to
    /// the shell that runs it, an in-place repair heals the shared home for every
    /// sibling <c>dotnet</c> invocation in the same sandbox -- e.g. a required-build
    /// gate followed by separate build-warnings-as-errors / test gates that each
    /// run their own <c>dotnet</c> without a preamble. Prepend it before
    /// <see cref="RelocationPreamble"/>: when the home cannot be repaired without
    /// root (e.g. <c>$HOME</c> is not writable) this is a no-op and relocation
    /// takes over. Empty/missing user settings after the repair are fine -- NuGet
    /// falls back to its built-in nuget.org default source.</para>
    ///
    /// <para>Idempotent and a strict no-op when <c>$HOME/.nuget</c> is already
    /// usable. Safe under <c>set -eu</c>. Never emits secret material.</para>
    /// </summary>
    public const string InPlaceRepairPreamble = """
        # Repair a foreign-owned $HOME/.nuget IN PLACE so every sibling dotnet
        # invocation in this sandbox -- not just the current shell -- sees a
        # usable NuGet home. Probe the real first-restore operation (create the
        # settings dir + write a file in it, and read any existing config) rather
        # than inferring usability from permission bits.
        codeybox_repair_home="${HOME:-/nonexistent}/.nuget"
        codeybox_repair_settings="$codeybox_repair_home/NuGet"
        codeybox_repair_config="$codeybox_repair_settings/NuGet.Config"
        codeybox_repair_probe="$codeybox_repair_settings/.codeybox-repair-probe.$$"
        codeybox_repair_usable=1
        if mkdir -p "$codeybox_repair_settings" 2>/dev/null \
           && touch "$codeybox_repair_probe" 2>/dev/null; then
          rm -f "$codeybox_repair_probe" 2>/dev/null || true
          if [ -e "$codeybox_repair_config" ] && [ ! -r "$codeybox_repair_config" ]; then
            codeybox_repair_usable=0
          fi
        else
          codeybox_repair_usable=0
        fi
        if [ "$codeybox_repair_usable" -eq 0 ] && [ -n "${HOME:-}" ]; then
          # Rename the foreign-owned home aside (needs only a writable $HOME, which
          # the build uid owns) and recreate it uid-owned. Best-effort under set -e:
          # any failure leaves the tree as-is for the relocation fallback to handle.
          if [ -e "$codeybox_repair_home" ] || [ -L "$codeybox_repair_home" ]; then
            codeybox_repair_aside="$codeybox_repair_home.codeybox-foreign-owned.$$"
            if mv "$codeybox_repair_home" "$codeybox_repair_aside" 2>/dev/null; then
              if mkdir -p "$codeybox_repair_settings" 2>/dev/null; then
                echo "CodeyBox: repaired unusable $codeybox_repair_home in place (foreign-owned tree renamed aside)." >&2
                # Preserve the pre-populated package cache so restores stay offline.
                if [ -d "$codeybox_repair_aside/packages" ] \
                   && [ ! -e "$codeybox_repair_home/packages" ]; then
                  ln -s "$codeybox_repair_aside/packages" "$codeybox_repair_home/packages" 2>/dev/null || true
                fi
              else
                # Could not recreate: restore the original so nothing is lost.
                mv "$codeybox_repair_aside" "$codeybox_repair_home" 2>/dev/null || true
              fi
            fi
          else
            mkdir -p "$codeybox_repair_settings" 2>/dev/null || true
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
