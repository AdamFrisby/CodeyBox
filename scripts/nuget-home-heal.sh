# shellcheck shell=sh
# Single source of truth for the per-user NuGet-home recovery.
#
# NuGet reads its per-user configuration from <cli-home>/.nuget/NuGet and, when
# that file is absent, tries to create it there. <cli-home> defaults to
# DOTNET_CLI_HOME, or $HOME when that is unset. In locked-down / COW-inherited
# sandboxes the inherited directory can be owned by another account or mounted
# read-only, so `dotnet` aborts restore before the build with an
# unauthorized-access error ("Failed to read NuGet.Config due to unauthorized
# access"). NuGet performs that user-config read during restore, so neither an
# in-tree NuGet.Config / RestoreConfigFile (which only redirect package sources,
# not the fatal user-config read) nor a late MSBuild BeforeTargets="Restore" hook
# can heal it — the home must be repaired on disk before that read. An
# InitialTargets hook runs early enough (see below); every call site repairs the
# home, none merely reconfigures sources.
#
# DOT-SOURCE this file (`. scripts/nuget-home-heal.sh`) BEFORE invoking dotnet.
# It never fails the caller (safe under `set -e`), and — because it is
# dot-sourced — can export a fallback DOTNET_CLI_HOME into the caller's
# environment. "Usable" covers the consumed side too: a home whose settings
# dir verifies but whose .nuget/packages tree fails the whole-tree cache
# admission below is still healed (quarantined aside or redirected). On a
# fully-usable home the only writes are the settings dir's create-if-missing,
# the transient create-then-delete writability probe inside it, and the
# idempotent migration-marker seeding (which may materialize its candidate
# homes) described below. build.sh and the audit build/test gates all source
# this rather than re-implementing the recovery.
#
# It is ALSO run from MSBuild via an InitialTargets hook (see
# Directory.NuGetHomeHeal.targets, wired into Directory.Build.props,
# Directory.Solution.props and Directory.Build.targets). InitialTargets fires
# at the very start of MSBuild evaluation, before NuGet's user-config read —
# once per solution via Directory.Solution.props, and per project via
# Directory.Build.props/targets only OUTSIDE solution builds (the per-project
# hooks are suppressed by _CodeyBoxInSolution during solution builds). The
# per-project invocations set CODEYBOX_NUGET_HEAL_NO_QUARANTINE=1 — the ONLY
# value that suppresses quarantine; the solution-level Exec clears any
# inherited value first — so they can
# NEVER run the destructive quarantine — repair that renames a directory is
# reserved for the once-per-solution hook; a non-solution build that still
# finds an unusable home is covered by the in-process NuGetHomeGuard.targets
# admission check and repoint instead. That MSBuild path relies only on the on-disk repair (an
# exported DOTNET_CLI_HOME would not survive back to the parent `dotnet`),
# which the in-place quarantine below performs whenever the cli-home is
# writable — at solution level, or in any single-shot dot-source caller.
#
# HOME, DOTNET_CLI_HOME, XDG_RUNTIME_DIR and friends are launch-env input:
# a merely-writable directory is not proof of ownership. Every path this file
# writes under or exports is therefore admitted only after it canonicalizes to
# storage owned by this uid and not writable by any co-tenant (the "private"
# rule below), or — for data we only consume, like an image-baked package
# cache — owned by this uid or root across the WHOLE tree (the "trusted" rule
# on the root plus the descendant scan in _codeybox_secure_package_cache).
# Anything else falls through to an owned fallback instead of being trusted
# because it was writable.
#
# Group write bits are tolerated ONLY on entries owned by this uid whose group
# is provably private to this account — a gid this account actually holds
# (`id -G`) whose EVERY group-db entry across ALL NSS modules names no members
# other than possibly this user, which no OTHER account holds as a primary
# group, and which has at least one group-db entry unless it is the account's
# own primary gid — see _codeybox_group_is_private. "Across ALL NSS modules"
# is itself proven, not assumed: a keyless `getent` only iterates modules
# that support setgrent/getgrent enumeration, and sssd/AD/LDAP commonly run
# with enumeration disabled while keyed lookups still resolve — so the proof
# first verifies that EVERY source configured for the group AND passwd
# databases in nsswitch.conf is enumerable (or covered by the direct /etc/*
# union) and fails closed when coverage cannot be shown
# (_codeybox_nss_enumeration_complete). That
# is the standard Linux
# user-private-group shape (default umask 002 creates drwxrwxr-x dirs); it
# does not widen co-tenant access. A uid-owned directory in a SHARED group
# (users, staff, an LDAP/AD group), any foreign-owned group-writable entry,
# and every other-writable (o+w) entry remain rejected.

# === codeybox-shared-helpers: begin ===
# Everything between the "codeybox-shared-helpers" sentinels is the head of
# NuGetHomeSelfHeal.Preamble: CodeyBox.Core embeds THIS file as a manifest
# resource and lifts the marked region verbatim into the C# preamble, so a
# guard change here lands in both the dot-sourced recovery and the wrapped
# `dotnet` preamble automatically — there is no second copy to hand-sync.
#
# The leaf assignments are likewise the single source for the C# names:
# NuGetHomeSelfHeal.WritableHomeLeaf (the per-user fallback home under
# ${TMPDIR:-/tmp}) and NuGetHomeSelfHeal.RepoHomeLeaf (the stamped
# repo-local CLI home, mirrored by DotnetCliHomeConventions.DirectoryName)
# are parsed out of the embedded copy of this file, and
# _codeybox_nuget_migrations_marker is parsed into
# NuGetHomeSelfHeal.NuGetMigrationsMarkerDir for the Incus provisioning seed
# — BuildScriptNuGetHealTests pins the assignments, so a rename cannot
# desync the fallback selection or the migration-marker seeding.
_codeybox_home_leaf="codeybox-nuget-home"
_codeybox_repo_home_leaf=".dotnet-cli-home"
# Relative path of NuGet's applied-migration marker DIRECTORY under a user
# home: NuGet's first-use "NuGet-Migrations" named mutex runs only while the
# marker file (<dir>/1) inside it is absent. One literal for every seed —
# the dot-sourced recovery below and the Incus bake-time seed composed from
# this file's parsed value — so a NuGet layout change moves both together.
_codeybox_nuget_migrations_marker=".local/share/NuGet/Migrations"
_codeybox_euid="$(id -u)"
_codeybox_login_name="$(id -un 2>/dev/null)"

# Populate _codeybox_stat_mode (the "drwxr-xr-x" field), _codeybox_stat_uid
# (numeric owner uid) and _codeybox_stat_gid (numeric group gid) for directory
# $1 from a single `ls -ldn` lstat — the attributes the ownership checks below
# need, without relying on find(1) option dialects (-perm / -uid spellings
# differ across GNU, BSD, and busybox). Fails when $1 is not a directory —
# including a symlink, which reports mode "l...": callers canonicalize first,
# so a link seen here is a mid-check swap and must not be treated as the
# verified directory.
_codeybox_lstat_dir() {
    set -- $(ls -ldn "$1" 2>/dev/null)
    case ${1:-} in
        d*) ;;
        *) return 1 ;;
    esac
    _codeybox_stat_mode=$1
    _codeybox_stat_uid=${3:-}
    _codeybox_stat_gid=${4:-}
    [ -n "$_codeybox_stat_uid" ] && [ -n "$_codeybox_stat_gid" ]
}

# Single-entry memo for _codeybox_group_is_private: directory walks revisit
# the same handful of gids, and each verdict costs a group-db and passwd-db
# lookup.
_codeybox_private_group_gid=
_codeybox_private_group_verdict=
# The process's own group set (`id -G`), primary gid (`id -g`), and the
# passwd/group dbs are each resolved once per process — all go through NSS
# (LDAP/AD included), with /etc/passwd and /etc/group unioned in for a
# nsswitch that skips the files db.
_codeybox_group_set=
_codeybox_group_set_dumped=0
_codeybox_primary_gid=
_codeybox_primary_gid_dumped=0
_codeybox_group_dump=
_codeybox_group_dumped=0
_codeybox_passwd_dump=
_codeybox_passwd_dumped=0
# The account/group database files and NSS configuration the proofs read are
# variables rather than literals so tests can point them at fixture data;
# production always uses the /etc defaults shown here.
_codeybox_group_db="/etc/group"
_codeybox_passwd_db="/etc/passwd"
_codeybox_nsswitch_conf="/etc/nsswitch.conf"
_codeybox_sssd_conf="/etc/sssd/sssd.conf"
_codeybox_sssd_vendor_conf="/usr/etc/sssd/sssd.conf"
_codeybox_sssd_conf_d="/etc/sssd/conf.d"
# Memo for _codeybox_nss_enumeration_complete: "" unchecked, then 0/1.
_codeybox_nss_enum_checked=0
_codeybox_nss_enum_ok=0

# Print the NSS service names configured for database $1 in nsswitch.conf —
# the whitespace-separated tokens after "db:" with every [status=action]
# bracket removed. A missing conf, or a conf with no line for the db, means
# glibc's built-in default of `files` alone, which the direct /etc/* union in
# the proofs already covers. Returns nonzero when the conf exists but cannot
# be read or parsed: an uninspectable configuration cannot prove coverage,
# so callers must fail closed.
_codeybox_nss_db_sources() {
    if [ ! -e "$_codeybox_nsswitch_conf" ]; then
        printf 'files\n'
        return 0
    fi
    [ -r "$_codeybox_nsswitch_conf" ] || return 1
    awk -v db="$1" '
        {
            line = $0
            sub(/#.*/, "", line)
            if (line ~ "^[ \t]*" db "[ \t]*:") {
                sub("^[ \t]*" db "[ \t]*:[ \t]*", "", line)
                gsub(/\[[^]]*\]/, " ", line)
                n = split(line, fields, /[ \t]+/)
                for (i = 1; i <= n; i++)
                    if (fields[i] != "")
                        print fields[i]
                found = 1
                exit
            }
        }
        END { if (!found) print "files" }
    ' "$_codeybox_nsswitch_conf" 2>/dev/null
}

# sssd answers setgrent/getgrent enumeration only for domains configured
# `enumerate = true` — the per-domain default is false — while keyed lookups
# (getgrgid, initgroups, getpwnam) still resolve. A configured `sss` source
# can therefore hide group/passwd entries from a keyless getent dump, so
# enumeration must be PROVEN: sssd serves the domains named in the [sssd]
# `domains =` list plus every [domain/*] section, and every one of them must
# carry `enumerate = true` in its own section.
#
# SSSD's config hierarchy is /etc/sssd/sssd.conf when present; otherwise the
# vendor file /usr/etc/sssd/sssd.conf applies. In either case the readable
# /etc/sssd/conf.d/*.conf snippets are merged afterwards in lexical order
# with later assignments winning — hidden dotfiles are not snippets. The
# `enumerate` option is per-domain only: an [sssd]-section value is not
# honoured by sssd and is deliberately not accepted. No main or snippet
# config means sssd serves nothing at all; an unreadable or unlistable
# config fails closed because a partially-read config could hide a later
# `enumerate = false`.
_codeybox_sss_enumeration_proven() {
    _codeybox_sss_main=
    if [ -e "$_codeybox_sssd_conf" ] || [ -L "$_codeybox_sssd_conf" ]; then
        [ -f "$_codeybox_sssd_conf" ] && [ -r "$_codeybox_sssd_conf" ] \
            || return 1
        _codeybox_sss_main="$_codeybox_sssd_conf"
    elif [ -e "$_codeybox_sssd_vendor_conf" ] \
        || [ -L "$_codeybox_sssd_vendor_conf" ]; then
        [ -f "$_codeybox_sssd_vendor_conf" ] \
            && [ -r "$_codeybox_sssd_vendor_conf" ] || return 1
        _codeybox_sss_main="$_codeybox_sssd_vendor_conf"
    fi

    _codeybox_sss_text=
    if [ -n "$_codeybox_sss_main" ]; then
        _codeybox_sss_text="$(cat "$_codeybox_sss_main")" || return 1
    fi
    if [ -e "$_codeybox_sssd_conf_d" ] || [ -L "$_codeybox_sssd_conf_d" ]; then
        [ -d "$_codeybox_sssd_conf_d" ] && [ -r "$_codeybox_sssd_conf_d" ] \
            && [ -x "$_codeybox_sssd_conf_d" ] || return 1
        for _codeybox_sss_dropin in "$_codeybox_sssd_conf_d"/*.conf; do
            if [ -e "$_codeybox_sss_dropin" ] || [ -L "$_codeybox_sss_dropin" ]; then
                [ -f "$_codeybox_sss_dropin" ] || continue
                [ -r "$_codeybox_sss_dropin" ] || return 1
                _codeybox_sss_drop="$(cat "$_codeybox_sss_dropin")" || return 1
                _codeybox_sss_text="${_codeybox_sss_text}${_codeybox_sss_text:+
}$_codeybox_sss_drop"
            fi
        done
    fi
    [ -n "$_codeybox_sss_text" ] || return 0
    printf '%s\n' "$_codeybox_sss_text" | awk '
        {
            line = $0
            sub(/[#;].*/, "", line)
        }
        /^[ \t]*\[/ {
            section = line
            sub(/^[ \t]*\[/, "", section)
            sub(/\].*/, "", section)
            gsub(/[ \t]/, "", section)
            section = tolower(section)
            in_sssd = (section == "sssd")
            curdom = ""
            if (section ~ /^domain\//) {
                curdom = substr(section, 8)
                doms[curdom] = 1
            }
            next
        }
        {
            if (line ~ /^[ \t]*$/) next
            eq = index(line, "=")
            if (eq == 0) next
            key = substr(line, 1, eq - 1)
            val = substr(line, eq + 1)
            gsub(/^[ \t]+|[ \t]+$/, "", key)
            gsub(/^[ \t]+|[ \t]+$/, "", val)
            lk = tolower(key)
            lv = tolower(val)
            if (in_sssd && lk == "domains") {
                m = split(val, parts, /[ \t,]+/)
                for (i = 1; i <= m; i++)
                    if (parts[i] != "")
                        doms[tolower(parts[i])] = 1
            } else if (curdom != "") {
                if (lk == "enumerate")
                    enum[curdom] = \
                        (lv == "true" || lv == "yes" || lv == "on" || lv == "1") \
                            ? "yes" : "no"
            }
        }
        END {
            bad = 0
            for (d in doms)
                if (enum[d] != "yes")
                    bad = 1
            exit (bad ? 1 : 0)
        }
    ' 2>/dev/null
}

# Return 0 when a single NSS source is provably covered by the keyless
# getent enumeration the proofs consume. `files`/`compat` are covered by the
# direct /etc/* union; `db`, `nis`, `nisplus`, `yp`, and `systemd` implement
# setXent/getXent enumeration (nss-systemd has since systemd v239, and the
# dynamic users/groups it synthesizes are privileged-actor creations a
# co-tenant cannot mint anyway). `sss` is covered only when
# _codeybox_sss_enumeration_proven shows enumerate=true for every served
# domain. Anything else — ldap, winbind, dns, hesiod, mymachines, unknown —
# cannot be proven enumerable and fails closed.
_codeybox_nss_source_enumerable() {
    case ${1:-} in
        files|compat|db|nis|nisplus|yp|systemd) return 0 ;;
        sss) _codeybox_sss_enumeration_proven ;;
        *) return 1 ;;
    esac
}

# Return 0 only when keyless `getent group` AND `getent passwd` enumeration
# is proven to cover EVERY NSS source configured for BOTH databases — the
# precondition for trusting either dump in _codeybox_group_is_private. A
# non-empty getent dump is NOT proof of complete coverage: modules without
# enumeration support (sss with enumerate=false, nss_ldap, winbind without
# enum, ...) are skipped silently while keyed lookups still resolve, so a
# duplicate-gid group entry or a foreign primary-gid account could hide in
# exactly the blind spot the member-list and primary-gid proofs exist to
# close. Result is memoized per process.
_codeybox_nss_enumeration_complete() {
    if [ "${_codeybox_nss_enum_checked:-0}" = "1" ]; then
        [ "${_codeybox_nss_enum_ok:-0}" = "1" ]
        return
    fi
    _codeybox_nss_enum_checked=1
    _codeybox_nss_enum_ok=0
    _codeybox_nss_db=group
    while :; do
        _codeybox_nss_srcs="$(_codeybox_nss_db_sources "$_codeybox_nss_db")" \
            || return 1
        for _codeybox_nss_src in $_codeybox_nss_srcs; do
            _codeybox_nss_source_enumerable "$_codeybox_nss_src" || return 1
        done
        [ "$_codeybox_nss_db" = "group" ] || break
        _codeybox_nss_db=passwd
    done
    _codeybox_nss_enum_ok=1
    return 0
}

# Return 0 when gid $1 is provably PRIVATE to this account — group-write on
# storage bearing it grants no co-tenant access. Every proof must hold, and
# anything unverifiable fails closed:
#
#   0. Held gid: $1 must appear in THIS process's own group set (`id -G`,
#      resolved through NSS). A gid we do not ourselves belong to can never
#      be "our" private group — its privacy would rest entirely on proving
#      a negative about accounts (and user-side supplementary memberships)
#      we cannot enumerate.
#   1. NSS enumeration: `getent` must exist. Without it (macOS, musl) only
#      /etc/group and /etc/passwd are visible while directory-service
#      (OpenDirectory/AD/LDAP) groups and accounts are invisible — both
#      proofs below would pass for a genuinely shared group, so the verdict
#      is "not private".
#   1b. Enumeration COMPLETENESS: getent existing is not enough — a keyless
#      `getent group`/`getent passwd` only iterates NSS modules that support
#      setgrent/getgrent enumeration, and sssd/AD/LDAP commonly run with
#      enumeration disabled while keyed lookups (getgrgid, initgroups,
#      getpwnam) still resolve. A non-empty dump is therefore NOT proof that
#      every module's entries were seen: a duplicate-gid group entry listing
#      a co-tenant, or an LDAP account holding the gid as primary group,
#      could sit in exactly the blind spot proofs 2 and 3 exist to close.
#      _codeybox_nss_enumeration_complete requires every source configured
#      for BOTH databases to be provably enumerable (or covered by the
#      direct /etc/* union) before either dump is trusted; anything
#      unverifiable fails closed.
#   2. Member list: EVERY group-db entry carrying $1 across ALL NSS modules
#      must name nobody in its member field but possibly this login. A keyed
#      `getent group <gid>` is NOT sufficient: glibc answers a numeric key
#      with a single getgrgid() lookup, which returns only the FIRST NSS
#      module's match — a duplicate-gid entry in a later module (sssd/LDAP)
#      would never be seen while initgroups/getgrent still honours it. The
#      proof therefore enumerates the whole group db with a KEYLESS
#      `getent group` (setgrent/getgrent iterates every module) and filters
#      on the gid field, unioned with every matching /etc/group line so a
#      files-db entry nsswitch skipped cannot hide members either. Multiple
#      entries may legally share one gid; inspecting only the first would
#      let a second entry's foreign members slip through. A gid with NO
#      group-db entry at all cannot pass vacuously: the member-list proof
#      is evidence-free, so it is admitted only when $1 is the account's
#      PRIMARY gid (`id -g`) — the user-private-group shape where membership
#      is inherent to the account — and still has to survive proof 3.
#   3. Primary gid: no OTHER passwd entry holds $1 as its primary gid — the
#      classic failure mode where a group member list is empty yet every
#      local account shares the gid (e.g. macOS `staff`).
#
# Residual, documented rather than claimed away: a co-tenant holding $1 as a
# supplementary group through a user-side directory-service membership that
# no group entry reports is not enumerable portably. Proof 0 bounds that
# residue to gids this account actually belongs to — the umask-002 /
# user-private-group shape this carve-out exists for.
_codeybox_group_is_private() {
    [ -n "${1:-}" ] || return 1
    case $1 in
        *[!0-9]*) return 1 ;;
    esac
    if [ "$1" = "${_codeybox_private_group_gid:-}" ]; then
        [ "${_codeybox_private_group_verdict:-}" = "yes" ]
        return
    fi
    _codeybox_private_group_gid=$1
    _codeybox_private_group_verdict=no

    # Proof 0 — the gid must be one this account actually holds. `id -G`
    # prints the full group set (primary + supplementary) through NSS; an
    # empty or failed dump cannot prove membership, so it fails closed.
    if [ "$_codeybox_group_set_dumped" != "1" ]; then
        _codeybox_group_set="$(id -G 2>/dev/null)"
        _codeybox_group_set_dumped=1
    fi
    [ -n "$_codeybox_group_set" ] || return 1
    printf '%s\n' "$_codeybox_group_set" | tr ' ' '\n' \
        | grep -qxF "$1" 2>/dev/null || return 1

    # Proof 1 — NSS enumeration is mandatory; see the header above.
    command -v getent >/dev/null 2>&1 || return 1

    # Proof 1b — the enumeration must also be COMPLETE across every configured
    # NSS source for both databases; see the header above. Without this a
    # non-empty dump from enumerable modules alone would look identical to a
    # fully-covered one while sss/ldap entries stay invisible.
    _codeybox_nss_enumeration_complete || return 1

    # Proof 2 — member list across EVERY entry for this gid in EVERY NSS
    # module. A KEYED `getent group "$1"` returns only the first module's
    # match for a numeric key (getgrgid), so a duplicate-gid entry in a
    # later module — exactly the files+sssd/LDAP split this proof exists
    # for — would go unexamined while initgroups still honours it. The
    # keyless `getent group` enumerates the whole db through setgrent/
    # getgrent and is filtered on the gid field instead; it is dumped once
    # per process like the passwd db. /etc/group is scanned as well so a
    # files-db entry nsswitch skipped cannot hide members. An empty dump
    # means the group db cannot be enumerated at all — fail closed rather
    # than skip the proof. The member list is the field after the last ':'
    # of each entry; entries are comma-separated login names. Every member
    # of every entry must be this login — empty lists pass, anything else
    # (including members we cannot name ourselves to compare against) fails
    # closed.
    if [ "$_codeybox_group_dumped" != "1" ]; then
        _codeybox_group_dump="$(getent group 2>/dev/null)"
        [ -n "$_codeybox_group_dump" ] || return 1
        _codeybox_group_dumped=1
    fi
    _codeybox_gp_entries="$(printf '%s\n' "$_codeybox_group_dump" \
        | awk -F: -v g="$1" '$3 == g')"
    if [ -f "$_codeybox_group_db" ]; then
        _codeybox_gp_etc="$(awk -F: -v g="$1" '$3 == g' "$_codeybox_group_db" 2>/dev/null)"
        if [ -n "$_codeybox_gp_etc" ]; then
            _codeybox_gp_entries="${_codeybox_gp_entries}${_codeybox_gp_entries:+
}$_codeybox_gp_etc"
        fi
    fi
    _codeybox_gp_foreign="$(printf '%s\n' "$_codeybox_gp_entries" \
        | awk -F: 'NF { print $NF }' \
        | tr ',' '\n' | tr -d ' ' \
        | grep -v '^$' \
        | grep -vxF -e "${_codeybox_login_name:-.codeybox-no-such-login}" \
            2>/dev/null)"
    [ -z "$_codeybox_gp_foreign" ] || return 1
    # Vacuous-pass guard: with no group-db entry for the gid there is no
    # member list to inspect, so "private" would rest on proofs 0 and 3
    # alone. Admit that only for the account's PRIMARY gid — the
    # user-private-group shape (umask 002) where membership is inherent to
    # the account rather than enumerated — and only after proof 3 below
    # still excludes every other account holding it as a primary gid. A
    # held-but-nonprimary gid with no group entry fails closed.
    if [ -z "$_codeybox_gp_entries" ]; then
        if [ "$_codeybox_primary_gid_dumped" != "1" ]; then
            _codeybox_primary_gid="$(id -g 2>/dev/null)"
            _codeybox_primary_gid_dumped=1
        fi
        [ "$1" = "$_codeybox_primary_gid" ] || return 1
    fi

    # Proof 3 — no OTHER account (uid != ours) holds $1 as its PRIMARY gid.
    # getent passwd resolves every account through NSS; /etc/passwd is
    # unioned in so a nsswitch without the files db cannot hide a local
    # account. An empty getent dump means the account db cannot be
    # enumerated at all — fail closed rather than skip the proof.
    if [ "$_codeybox_passwd_dumped" != "1" ]; then
        _codeybox_passwd_dump="$(getent passwd 2>/dev/null)"
        [ -n "$_codeybox_passwd_dump" ] || return 1
        if [ -f "$_codeybox_passwd_db" ]; then
            _codeybox_passwd_dump="$_codeybox_passwd_dump
$(cat "$_codeybox_passwd_db" 2>/dev/null)"
        fi
        _codeybox_passwd_dumped=1
    fi
    if printf '%s\n' "$_codeybox_passwd_dump" | awk -F: \
        -v g="$1" -v u="$_codeybox_euid" \
        '$4 == g && $3 != u { found = 1 } END { exit(found ? 0 : 1) }'; then
        return 1
    fi
    _codeybox_private_group_verdict=yes
    return 0
}

# Canonicalize directory $1 (cd/pwd -P, so a planted or legitimate symlink
# resolves to its real target) and print the resolved path when the whole
# chain is safe to rely on. The leaf is judged by the rule named in $2:
#
#   private — owned by this uid, never other-writable, and group-writable
#             only when the group is provably private to this account
#             (_codeybox_group_is_private). The only acceptable state for
#             storage we export (a healed HOME, the user-scoped mutex dir),
#             probe-write into, or seed under: a merely-writable foreign dir —
#             a co-tenant 777 dir, a uid-owned dir in a shared group, or a
#             stamped path resolving through a planted symlink into one —
#             otherwise leaves the NuGet.Config every restore reads, and the
#             writes below, inside attacker-controlled storage.
#   trusted — like private but root ownership is admitted too, for data we
#             only CONSUME read-only (an image-baked package cache); the
#             platform's root is already trusted, a third uid is not.
#   anchor  — owned by this uid or root. Other-writable is tolerated only
#             under the sticky bit (the /tmp rule: co-tenants may add
#             entries but can never rename or replace ours). Group-writable
#             is tolerated under the sticky bit OR — for uid-owned entries
#             — a provably private group (_codeybox_group_is_private); a
#             private group does NOT excuse other-writable. Also the rule
#             applied to every ANCESTOR of the leaf, so nothing along the
#             chain can be swapped out after these checks.
# $3 "writable" additionally requires the leaf be writable by this user.
_codeybox_secure_dir() {
    # Empty input must fail before the cd: `cd ""` silently stays in the
    # current directory in several shells, which would "verify" the cwd.
    [ -n "${1:-}" ] || return 1
    _codeybox_sec="$(cd "$1" 2>/dev/null && pwd -P)" || return 1
    _codeybox_sec_rule=${2:-private}
    # Fail closed on an unrecognised rule or modifier: anything else would
    # silently degrade to the trusted policy below, so a typo in a caller's
    # rule name must refuse rather than weaken the admission check.
    case $_codeybox_sec_rule in
        private|trusted|anchor) ;;
        *) return 1 ;;
    esac
    case ${3:-} in
        ""|writable) ;;
        *) return 1 ;;
    esac
    if [ "${3:-}" = "writable" ] && [ ! -w "$_codeybox_sec" ]; then
        return 1
    fi
    _codeybox_sec_walk=$_codeybox_sec
    _codeybox_sec_leaf=1
    while :; do
        _codeybox_lstat_dir "$_codeybox_sec_walk" || return 1
        case $_codeybox_stat_mode in
            ??????????+*)
                # An extended POSIX ACL (ls marks it with a "+" suffix) can
                # grant a co-tenant access the 10 mode bits deny — refuse
                # rather than judge this dir private from the mode alone.
                return 1 ;;
        esac
        if [ "$_codeybox_sec_leaf" -eq 1 ] && [ "$_codeybox_sec_rule" = private ]; then
            [ "$_codeybox_stat_uid" = "$_codeybox_euid" ] || return 1
        else
            { [ "$_codeybox_stat_uid" = "$_codeybox_euid" ] \
                || [ "$_codeybox_stat_uid" = "0" ]; } || return 1
        fi
        case $_codeybox_stat_mode in
            ????????w*)
                # Other-writable: leaf rules stricter than anchor never tolerate
                # other write bits; an anchor tolerates them only under the sticky bit.
                if [ "$_codeybox_sec_leaf" -eq 1 ] \
                    && [ "$_codeybox_sec_rule" != anchor ]; then
                    return 1
                fi
                case $_codeybox_stat_mode in
                    ?????????[tT]*) ;;
                    *) return 1 ;;
                esac ;;
            ?????w*)
                # Group-writable: tolerated only when the group is provably
                # private to this account — the umask-002 / user-private-group
                # shape. A uid-owned dir in a SHARED group is just as
                # co-tenant-writable as a foreign-owned one, so it is judged
                # by the same rule: leaf rules reject it, an anchor tolerates
                # it only under the sticky bit.
                if [ "$_codeybox_stat_uid" != "$_codeybox_euid" ] \
                    || ! _codeybox_group_is_private "$_codeybox_stat_gid"; then
                    if [ "$_codeybox_sec_leaf" -eq 1 ] \
                        && [ "$_codeybox_sec_rule" != anchor ]; then
                        return 1
                    fi
                    case $_codeybox_stat_mode in
                        ?????????[tT]*) ;;
                        *) return 1 ;;
                    esac
                fi ;;
        esac
        _codeybox_sec_leaf=0
        [ "$_codeybox_sec_walk" = "/" ] && break
        _codeybox_sec_walk=${_codeybox_sec_walk%/*}
        [ -n "$_codeybox_sec_walk" ] || _codeybox_sec_walk=/
    done
    printf '%s\n' "$_codeybox_sec"
}

# Print the canonical path of directory $1 when it is — or can safely become —
# private per-user storage. An existing entry is admitted only under the
# "private" rule above; no mkdir runs against an unvalidated path (a bare
# `mkdir -p` would descend planted symlinked intermediates — an attacker-chosen
# dir-create — and a leaf created under a co-tenant-writable parent could be
# swapped for a symlink before the check). A missing leaf is created only
# after the parent verifies as a writable anchor, then by ONE `mkdir -m 700`
# — never -p — so the new dir is born owned by this uid with no writable
# window for a co-tenant plant. $2 "writable" additionally requires the leaf
# be writable by us. ("Private" here includes the private-group carve-out: a
# uid-owned group-writable dir is admitted only when its gid is provably
# private to this account.)
_codeybox_adopt_dir() {
    [ -n "${1:-}" ] || return 1
    if [ -e "$1" ] || [ -L "$1" ]; then
        _codeybox_secure_dir "$1" private "${2:-}"
        return
    fi
    _codeybox_adopt_leaf="${1%/}"
    _codeybox_adopt_parent="$(dirname "$_codeybox_adopt_leaf" 2>/dev/null)" \
        || return 1
    _codeybox_adopt_parent="$(_codeybox_secure_dir \
        "$_codeybox_adopt_parent" anchor writable)" || return 1
    # Create the leaf under the CANONICAL parent, not the raw env path: an
    # intermediate swapped for a symlink between the anchor check and the
    # mkdir cannot redirect the create (worst case the mkdir simply fails).
    mkdir -m 700 "$_codeybox_adopt_parent/${_codeybox_adopt_leaf##*/}" \
        2>/dev/null || return 1
    _codeybox_secure_dir "$1" private "${2:-}"
}

# Adopt a possibly-nested private-storage path one verified component at a
# time. _codeybox_adopt_dir deliberately requires the parent to exist already;
# the repository-local fallback home may sit under a not-yet-created .build
# directory, so create each missing level through the same single-mkdir
# admission rather than `mkdir -p` descending planted symlinks. Best-effort
# like its callee: any unverifiable component refuses the whole path.
_codeybox_adopt_dir_deep() {
    [ -n "${1:-}" ] || return 1
    if [ -e "$1" ] || [ -L "$1" ]; then
        _codeybox_adopt_dir "$1" "${2:-}"
        return
    fi
    _codeybox_deep_parent="$(dirname "$1" 2>/dev/null)" || return 1
    [ "$_codeybox_deep_parent" != "$1" ] || return 1
    _codeybox_adopt_dir_deep "$_codeybox_deep_parent" "${2:-}" >/dev/null \
        || return 1
    _codeybox_adopt_dir "$1" "${2:-}"
}

# Print a writable per-user home dir for the heal's redirected-home and
# runtime-dir fallbacks; returns nonzero when no candidate could be obtained.
# $1, when non-empty, is the preferred candidate (the possibly just-redirected
# DOTNET_CLI_HOME). Every candidate is adopted only as verified private
# storage (canonicalized, owned by this uid, writable, never other-writable,
# group-writable only under a provably private group, under a safe ancestor
# chain) — DOTNET_CLI_HOME is
# environment input, and a co-tenant- or work-branch-planted symlink, a
# foreign-owned dir, or a squatted predictable path must not redirect the
# seeding below and dotnet's user-scoped mutex/runtime storage into
# foreign-controlled directories. The shared per-user dir — kept shared so
# one healed home, its seeded cache, and the mutex location are stable across
# execs — is adopted under the same terms. CODEYBOX_NUGET_REPO_DOTNET_HOME is
# the in-process MSBuild guard's verified repository-local last resort (it is
# unset for every other caller); it is adopted through the deep variant so a
# missing .build parent is created one verified component at a time. A
# private mktemp dir is the final candidate, itself re-verified because
# TMPDIR is env input too. Shared verbatim into the C# preamble (see the
# region sentinel above).
_codeybox_pick_user_dir() {
    _codeybox_pick="${1:-}"
    if [ -n "$_codeybox_pick" ]; then
        _codeybox_pick="$(_codeybox_adopt_dir "$_codeybox_pick" writable)" \
            || _codeybox_pick=
    fi
    if [ -z "$_codeybox_pick" ]; then
        _codeybox_pick="${TMPDIR:-/tmp}/${_codeybox_home_leaf}-${_codeybox_euid}"
        _codeybox_pick="$(_codeybox_adopt_dir "$_codeybox_pick" writable)" \
            || _codeybox_pick=
    fi
    if [ -z "$_codeybox_pick" ] \
        && [ -n "${CODEYBOX_NUGET_REPO_DOTNET_HOME:-}" ]; then
        _codeybox_pick="$(_codeybox_adopt_dir_deep \
            "$CODEYBOX_NUGET_REPO_DOTNET_HOME" writable)" || _codeybox_pick=
    fi
    if [ -z "$_codeybox_pick" ]; then
        _codeybox_pick="$(mktemp -d \
            "${TMPDIR:-/tmp}/${_codeybox_home_leaf}.XXXXXX")" || return 1
        _codeybox_pick="$(_codeybox_secure_dir "$_codeybox_pick" private writable)" \
            || return 1
    fi
    printf '%s\n' "$_codeybox_pick"
}

# Print the canonical "<cli-home>/.nuget/NuGet" settings dir when the NuGet
# user home under cli home $1 (already verified private per-user storage) is
# usable by NuGet; return 1 otherwise. Callers use it in `if` conditions, but
# this is an ENSURE, not a pure predicate: it creates the settings dir with
# `mkdir -p` under the admitted .nuget when missing, and — when $4 is
# non-empty — touches that file inside the resolved settings dir as a real
# create-probe (`touch`, a real command, rather than a shell redirect so a
# failure cannot abort a `set -e` caller). Both consumers run this ONE check —
# $2/$3 name the .nuget admission rule each needs WHEN the settings dir still
# has to be created: `trusted` for the dot-sourced recovery (an unwritable
# .nuget can still be quarantined aside, so the mkdir probe may discover the
# writability itself) vs `trusted writable` for the exec preamble (it can only
# redirect — an unwritable .nuget must fail up front). An already-verified
# settings dir still requires a `trusted` .nuget either way: a
# co-tenant-writable one lets its owner swap the sibling package cache — or
# the settings dir itself — for a symlink after these checks.
_codeybox_ensure_nuget_home_usable() {
    _codeybox_nuget_root="${1%/}/.nuget"
    _codeybox_nuget_user_dir="$_codeybox_nuget_root/NuGet"
    # Never descend through a symlinked .nuget or NuGet component — the mkdir
    # below would create the settings dir inside whatever directory the
    # link's owner chose.
    { [ -L "$_codeybox_nuget_root" ] || [ -L "$_codeybox_nuget_user_dir" ]; } \
        && return 1
    _codeybox_nuget_resolved="$(_codeybox_secure_dir \
        "$_codeybox_nuget_user_dir" private writable)" || _codeybox_nuget_resolved=
    if [ -n "$_codeybox_nuget_resolved" ]; then
        if [ -d "$_codeybox_nuget_root" ]; then
            _codeybox_secure_dir "$_codeybox_nuget_root" trusted \
                >/dev/null 2>&1 || return 1
        fi
    else
        if [ -d "$_codeybox_nuget_root" ]; then
            _codeybox_secure_dir "$_codeybox_nuget_root" \
                "${2:-trusted}" "${3:-}" >/dev/null 2>&1 || return 1
        fi
        mkdir -p "$_codeybox_nuget_user_dir" 2>/dev/null || return 1
        _codeybox_nuget_resolved="$(_codeybox_secure_dir \
            "$_codeybox_nuget_user_dir" private writable)" || return 1
    fi
    # A present-but-unreadable config fails the same fatal restore read as a
    # missing one.
    if [ -e "$_codeybox_nuget_resolved/NuGet.Config" ] \
        && [ ! -r "$_codeybox_nuget_resolved/NuGet.Config" ]; then
        return 1
    fi
    # A usable SETTINGS dir does not make the consumed sibling cache safe:
    # <cli-home>/.nuget/packages is NuGet's default global-packages folder on
    # this home (RestorePackagesPath also pins it back to the original home),
    # and an extracted package's build/.props/.targets run inside the
    # consuming build — so a present entry is admitted only when its WHOLE
    # tree passes the same _codeybox_secure_package_cache check the reuse and
    # seed paths apply. A dangling symlink reports -e false, so -L is tested
    # too; a symlinked entry is judged by its target's ownership and modes.
    # On failure the home is refused so the caller quarantines or redirects
    # and NuGet re-extracts into clean storage.
    if { [ -e "$_codeybox_nuget_root/packages" ] \
            || [ -L "$_codeybox_nuget_root/packages" ]; } \
        && ! _codeybox_secure_package_cache \
            "$_codeybox_nuget_root/packages" >/dev/null 2>&1; then
        return 1
    fi
    if [ -n "${4:-}" ]; then
        touch "$4" 2>/dev/null || return 1
    fi
    printf '%s\n' "$_codeybox_nuget_resolved"
}

# Print $1 with every control character stripped — the single policy for
# putting env- or branch-derived text into operator-facing output (the heal
# notice and the gate's per-target echo): a crafted value must not spoof the
# terminal/log with ANSI escapes.
_codeybox_strip_ctrl() {
    printf '%s' "${1:-}" | LC_ALL=C tr -d '[:cntrl:]'
}

# Upper bound on entries a consumed package cache may hold. The admission
# scan below verifies EVERY descendant before the cache is consumed, so an
# unbounded tree would be an unbounded scan — a cache larger than a real
# restore baseline needs is declined rather than verified.
_codeybox_cache_scan_limit=500000

# Print the canonical path of package-cache candidate $1 only when the WHOLE
# tree is consumable storage — the root canonicalizes under the "trusted"
# rule AND every descendant is owned by this uid or root, never
# other-writable, group-writable only on uid-owned entries whose gid is
# provably private (_codeybox_group_is_private), and no ACL broadening
# access beyond what the mode bits show.
# Verifying the root alone would admit a co-tenant-writable or foreign-owned
# subtree, and NuGet consumes extracted entries as-is: a present
# .nupkg.metadata skips re-extraction, and a package's build/.props/.targets
# execute inside the consuming build, so one planted or tampered descendant
# is attacker code in the gate.
#
# The verification runs the tree through `find -L` driving one batched
# `ls -ldnL` per ARG_MAX chunk: every entry's mode, owner, and
# alternate-access marker are evaluated as its link TARGET, so a planted
# symlink cannot smuggle in a writable target outside the tree under a
# clean-looking link — a dangling link cannot be stat()ed and surfaces as an
# error instead. Traversal failures fail CLOSED: find/ls diagnostics share
# the scanned stream, so any entry the scan cannot reach — a directory that
# grants execute without read (its children are unlistable yet still
# consumable by NuGet through a constructed path), a mid-scan chmod or
# delete, a symlink loop — trips the malformed-line check, and a partial
# traversal can never admit an unverified subtree. The tree is bounded by
# _codeybox_cache_scan_limit and the scan stops at the first offender.
_codeybox_secure_package_cache() {
    _codeybox_cache_root="$(_codeybox_secure_dir "${1:-}" trusted)" \
        || return 1
    # `set --` word-splits the (possibly space-padded) wc output so the count
    # is a bare number on every wc dialect. An existing dir always
    # enumerates at least itself, so a zero count means find could not scan
    # at all — decline rather than trust an unverified tree. This cheap
    # -print pass runs BEFORE the per-entry `ls` scans below so the size
    # bound actually bounds them: a cache larger than
    # _codeybox_cache_scan_limit is rejected after this traversal only,
    # never fully stat'ed first. Discarding its stderr is safe because the
    # verification pass below re-traverses and refuses on any reachability
    # error.
    set -- $(find -L "$_codeybox_cache_root" -print 2>/dev/null \
        | head -n "$_codeybox_cache_scan_limit" | wc -l)
    _codeybox_cache_entries=${1:-0}
    if [ "$_codeybox_cache_entries" -lt 1 ] \
        || [ "$_codeybox_cache_entries" -ge "$_codeybox_cache_scan_limit" ]; then
        return 1
    fi
    # Group-write inside the tree is admitted only for uid-owned entries
    # whose gid is provably private (_codeybox_group_is_private). The awk
    # scan below cannot run that lookup per entry, so EVERY DISTINCT gid a
    # uid-owned group-writable descendant carries is resolved once here —
    # the root's own gid (setgid inheritance) and this process's egid
    # (normal creation) are merely the common cases, not the only admissible
    # ones. Any group-writable descendant whose gid fails the proof fails
    # the scan closed and the cache is simply not consumed. The tree is
    # already bounded by _codeybox_cache_scan_limit above, so this pass and
    # the verification pass below each cost at most that many entries.
    _codeybox_cache_private_gids=
    _codeybox_cache_gid_candidates="$(find -L "$_codeybox_cache_root" \
        -exec ls -ldnL {} + 2>/dev/null \
        | awk -v euid="$_codeybox_euid" \
            '$3 + 0 == euid + 0 && substr($1, 6, 1) == "w" { print $4 }' \
        | sort -u)"
    for _codeybox_cache_gid in $_codeybox_cache_gid_candidates; do
        if _codeybox_group_is_private "$_codeybox_cache_gid"; then
            _codeybox_cache_private_gids="${_codeybox_cache_private_gids}${_codeybox_cache_gid}
"
        fi
    done
    _codeybox_cache_bad="$(find -L "$_codeybox_cache_root" \
            -exec ls -ldnL {} + 2>&1 \
        | awk -v euid="$_codeybox_euid" \
            -v pg="$_codeybox_cache_private_gids" '
            BEGIN { pgset = "\n" pg "\n" }
            {
                mode = $1
                n = length(mode)
                # An ls -l entry line is exactly type char + 9 mode chars
                # plus an optional alternate-access marker. Anything else —
                # a find/ls diagnostic for an entry the scan could not
                # reach, garbled output — means part of the tree escaped
                # verification: fail closed.
                if (n != 10 && n != 11) { print; exit }
                if (substr(mode, 1, 1) !~ /[-bcdlps]/) { print; exit }
                if (substr(mode, 2, 9) !~ /^[-rwxsStT]+$/) { print; exit }
                # "+" marks a POSIX ACL the mode bits cannot represent — it
                # could grant a co-tenant the access this scan denies, so
                # refuse it (any other marker is not an ACL). "." (SELinux
                # context) and "@" (xattrs) do not widen DAC access.
                if (n == 11 && substr(mode, 11, 1) !~ /^[.@]$/) { print; exit }
                if ($3 + 0 != euid + 0 && $3 + 0 != 0) { print; exit }
                if (substr(mode, 9, 1) == "w") { print; exit }
                # Group-write: only a uid-owned entry whose gid was proven
                # private above may carry it — a shared-group or foreign
                # group-write bit is co-tenant access and fails closed. The
                # newline-delimited pgset membership test cannot be fooled by
                # a gid that is a numeric prefix of another.
                if (substr(mode, 6, 1) == "w" \
                    && !($3 + 0 == euid + 0 \
                        && index(pgset, "\n" $4 "\n") > 0)) {
                    print; exit
                }
            }
            END { if (NR == 0) exit 1 }')" || return 1
    [ -z "$_codeybox_cache_bad" ] || return 1
    printf '%s\n' "$_codeybox_cache_root"
}

# Export NUGET_PACKAGES=<canonical $1> when inherited package-cache candidate
# $1 verifies as a consumable tree (_codeybox_secure_package_cache above) and
# NUGET_PACKAGES is not already set — an operator-provided value always wins.
# Restore only READS an already-extracted package (a present .nupkg.metadata
# skips re-extraction), so a read-only cache satisfies every restore and
# keeps offline/pinned images working after DOTNET_CLI_HOME relocates the
# global-packages folder away from the original home's .nuget. Best-effort:
# an unverifiable candidate is simply not exported. Shared by every site
# that preserves an inherited cache — the exec preamble (both branches) and
# the gate's env-isolation fragment — so the admission policy has one source.
_codeybox_reuse_nuget_packages() {
    [ -z "${NUGET_PACKAGES:-}" ] || return 0
    _codeybox_reuse="$(_codeybox_secure_package_cache "${1:-}")" || return 0
    NUGET_PACKAGES="$_codeybox_reuse"
    export NUGET_PACKAGES
    return 0
}

# Pin HOME and XDG_RUNTIME_DIR to verified private per-user storage for this
# process tree — the shared runtime-dir skeleton run by BOTH the dot-sourced
# recovery and the exec preamble, so the two can never drift on which dir is
# picked or on what happens when no fallback exists.
#
# Since runtime 10.0.13, user-scoped named mutexes — NuGet's first-use
# "NuGet-Migrations" among them — resolve under $XDG_RUNTIME_DIR, falling back
# to $HOME/.dotnet/corefx/Runtime/SharedMemory, and NuGet builds that key user
# paths off $HOME ignore DOTNET_CLI_HOME and hit the same broken home. On
# images whose $HOME is unavailable the CLI aborts before MSBuild with "The
# system cannot open the device or file specified: 'NuGet-Migrations'"
# (dotnet/runtime#135726, #136802).
#
# HOME / XDG_RUNTIME_DIR are launch-env input, so "usable" means the full
# "private" rule — canonicalized, owned by this uid, writable, never
# other-writable, group-writable only under a provably private group, under a
# safe ancestor chain — not merely -d/-w.
# Verified values are re-exported under their canonical path so a symlinked
# env value cannot be retargeted after the check; an unusable variable is
# pinned to one shared pick — the (possibly just-healed) DOTNET_CLI_HOME,
# else the shared per-user dir, else a private mktemp dir. When NO private
# fallback can be materialized, the rejected variable is UNSET rather than
# left exported: keeping it would hand the process co-tenant-controlled
# storage, while unset falls back to the passwd entry / platform defaults.
#
# Out-params for the caller's remediation step: _codeybox_pin_old_home holds
# the pre-pin $HOME (empty when unset) and _codeybox_pin_home_redirected holds
# the picked dir when HOME was redirected (empty otherwise) — the dot-sourced
# recovery uses the pair to seed the new home on disk; the exec preamble
# exports NUGET_PACKAGES instead.
codeybox_pin_runtime_dirs() {
    _codeybox_pin_old_home="${HOME:-}"
    _codeybox_pin_home_redirected=
    _codeybox_home_dir="$(_codeybox_secure_dir "${HOME:-}" private writable)" \
        || _codeybox_home_dir=
    _codeybox_xdg_dir="$(_codeybox_secure_dir "${XDG_RUNTIME_DIR:-}" private writable)" \
        || _codeybox_xdg_dir=

    if [ -n "$_codeybox_home_dir" ] && [ -n "$_codeybox_xdg_dir" ]; then
        HOME="$_codeybox_home_dir"
        export HOME
        XDG_RUNTIME_DIR="$_codeybox_xdg_dir"
        export XDG_RUNTIME_DIR
        return 0
    fi

    _codeybox_user_dir="$(_codeybox_pick_user_dir "${DOTNET_CLI_HOME:-}")" \
        || _codeybox_user_dir=
    if [ -z "$_codeybox_user_dir" ]; then
        # Total fallback failure: re-export only what verified and unset the
        # rejected values — leaving them exported would hand the process
        # foreign-controlled storage.
        if [ -n "$_codeybox_home_dir" ]; then
            HOME="$_codeybox_home_dir"
            export HOME
        else
            unset HOME
        fi
        if [ -n "$_codeybox_xdg_dir" ]; then
            XDG_RUNTIME_DIR="$_codeybox_xdg_dir"
            export XDG_RUNTIME_DIR
        else
            unset XDG_RUNTIME_DIR
        fi
        return 0
    fi

    if [ -n "$_codeybox_home_dir" ]; then
        HOME="$_codeybox_home_dir"
    else
        HOME="$_codeybox_user_dir"
        _codeybox_pin_home_redirected="$_codeybox_user_dir"
    fi
    export HOME
    if [ -n "$_codeybox_xdg_dir" ]; then
        XDG_RUNTIME_DIR="$_codeybox_xdg_dir"
    else
        XDG_RUNTIME_DIR="$_codeybox_user_dir"
    fi
    export XDG_RUNTIME_DIR
    return 0
}

# Unset the environment variable a REJECTED per-user home arrived as —
# DOTNET_CLI_HOME when set (the home resolved from it), else HOME — so the
# refused value cannot stay exported for the caller's dotnet: left exported
# it would hand the CLI foreign-controlled storage, while unset falls back
# to $HOME / the passwd entry / platform defaults. Shared by the dot-sourced
# recovery and the exec preamble so the "which variable" policy cannot
# drift; each caller decides WHEN it applies — the dot-sourced recovery
# calls it only when the cli home itself was rejected (a verified cli home
# is canonical private storage and stays exported even when its .nuget
# repair failed), while the preamble has no in-place repair and calls it
# whenever the home failed verification and no private fallback exists.
_codeybox_unset_home_env() {
    if [ -n "${DOTNET_CLI_HOME:-}" ]; then
        unset DOTNET_CLI_HOME
    else
        unset HOME
    fi
}

# The runtime-dir pinning above only fixes THIS process tree: exported values
# die with the exec that sourced this file. Audit gates run as separate execs
# whose environment we do not control — the `sh -c` preamble wrapping them is
# baked into the orchestrator binary and can predate the HOME/XDG pinning —
# and the named-mutex directory is resolved from the launch environment, so an
# MSBuild-time `Environment.SetEnvironmentVariable` cannot help either. What a
# broken runtime dir actually breaks is narrower than dotnet in general: the
# "NuGet-Migrations" mutex is created only while NuGet's applied-migration
# marker is ABSENT. MigrationRunner checks
# File.Exists(<user-profile>/.local/share/NuGet/Migrations/1) — or
# $XDG_DATA_HOME/NuGet/Migrations/1 — BEFORE creating the mutex
# (NuGet.Common.Migrations.MigrationRunner), so when the marker is present,
# first-use and restore skip the mutex outright and the launch env's runtime
# dir is never consulted.
#
# Seed that marker under every home a later exec in this sandbox could resolve:
# the stamped repo-local CLI home (DotnetCliHomeConventions pins HOME/
# DOTNET_CLI_HOME to <workdir>/.dotnet-cli-home for raw dotnet execs), the
# ambient $HOME/$DOTNET_CLI_HOME, the shared per-user fallback both preamble
# versions redirect to, the passwd-entry home (the UserProfile fallback when
# $HOME is unset), and $XDG_DATA_HOME's marker layout when that is set. The
# marker is the exact state NuGet leaves after a no-op migration run — an empty
# file — and File.Exists only needs the path to be readable, which is precisely
# what distinguishes it from the SharedMemory dir the mutex would have needed
# to CREATE. Seeding is strictly best-effort: homes that cannot be written are
# skipped (a marker cannot be planted there either), and an existing marker is
# never touched so real migration state is preserved.
#
# The candidates are less-trusted-influenceable paths — a predictable name in
# the world-writable ${TMPDIR:-/tmp} can be squatted by a co-tenant, and any of
# them can contain a planted symlink — so seeding must never become a
# confused-deputy write into an attacker-chosen directory: `mkdir -p` descends
# through symlinked intermediate components and `: >` writes through a symlink
# leaf. Every candidate is therefore admitted only as verified private
# per-user storage — canonicalized, owned by this uid, writable, never
# other-writable, group-writable only under a provably private group, under a
# safe ancestor chain (the same
# _codeybox_secure_dir check every other admission in this file uses, so a
# co-tenant cannot swap a marker-tree component for a symlink between the
# checks and the write) — each Migrations-tree component is then created level
# by level while refusing to descend through a symlink, and a symlink at the
# marker leaf is never written through.
#
# Materialize a heal-controlled candidate home leaf via the shared adopt
# chain: a missing leaf is created with ONE mode-700 `mkdir` under a verified
# anchor parent — never `-p`, which would descend planted symlinked
# intermediates — and an existing entry must already be private per-user
# storage (a squatted or planted path is refused). The seeder's private-dir
# checks re-verify either way. Best-effort only: an adoption failure is
# swallowed — the candidate stays absent and the seeder simply skips it.
_codeybox_try_adopt_home() {
    [ -n "${1:-}" ] || return 0
    _codeybox_adopt_dir "$1" writable >/dev/null 2>&1 || true
}

# Seed NuGet's applied-migration marker under candidate home $1, at relative
# directory path $2 (the _codeybox_nuget_migrations_marker dir for a home,
# "NuGet/Migrations" for $XDG_DATA_HOME). Safe to call with anything: a
# missing, non-directory,
# foreign-owned, co-tenant-writable, or foreign-owned-resolving candidate is
# skipped, as is any symlink found while descending.
_codeybox_seed_migration_marker() {
    [ -n "${1:-}" ] || return 0
    # Admit the candidate only as verified private per-user storage — the
    # guard the preamble's WritableHomeLeaf comment already promises for a
    # squatted or root-left directory in a world-writable temp, now with the
    # full chain check so nothing above the base can be swapped for a symlink
    # between the checks and the write.
    # The `||` fallback keeps the assignment's status at zero: a bare
    # `var=$(cd ...)` would propagate the failed substitution's status and
    # abort a `set -e` caller merely because a candidate is absent.
    _codeybox_marker_base="$(_codeybox_secure_dir "$1" private writable)" \
        || _codeybox_marker_base=
    [ -n "$_codeybox_marker_base" ] || return 0
    _codeybox_marker_dir="$_codeybox_marker_base/$2"
    # Newline-separated exact match: a space-joined list cannot represent a
    # candidate home whose path itself contains a space.
    if printf '%s\n' "${_codeybox_seen_marker_dirs:-}" \
        | grep -qxF "$_codeybox_marker_dir" 2>/dev/null; then
        return 0
    fi
    _codeybox_seen_marker_dirs="${_codeybox_seen_marker_dirs:-}${_codeybox_seen_marker_dirs:+
}$_codeybox_marker_dir"

    # Descend one component at a time rather than `mkdir -p`: a planted
    # symlink at any intermediate level would otherwise be followed, planting
    # the marker tree under a directory the candidate's owner never chose.
    _codeybox_marker_walk="$_codeybox_marker_base"
    _codeybox_marker_rel="$2"
    while [ -n "$_codeybox_marker_rel" ]; do
        case $_codeybox_marker_rel in
            */*)
                _codeybox_marker_part=${_codeybox_marker_rel%%/*}
                _codeybox_marker_rel=${_codeybox_marker_rel#*/}
                ;;
            *)
                _codeybox_marker_part=$_codeybox_marker_rel
                _codeybox_marker_rel=
                ;;
        esac
        _codeybox_marker_walk="$_codeybox_marker_walk/$_codeybox_marker_part"
        [ -L "$_codeybox_marker_walk" ] && return 0
        if [ -d "$_codeybox_marker_walk" ]; then
            # Re-verify the descended component like the base: a foreign-owned
            # or co-tenant-writable intermediate can be swapped for a symlink
            # between these checks and the write, so only verified private
            # storage is traversed.
            _codeybox_secure_dir "$_codeybox_marker_walk" private writable \
                >/dev/null 2>&1 || return 0
        else
            mkdir -m 700 "$_codeybox_marker_walk" 2>/dev/null || return 0
        fi
    done

    _codeybox_marker="$_codeybox_marker_walk/1"
    # An existing marker (or a symlink placeholder, which `-e` alone cannot
    # see when dangling) is real migration state — never overwrite it and
    # never write through a symlink.
    { [ -e "$_codeybox_marker" ] || [ -L "$_codeybox_marker" ]; } && return 0
    : > "$_codeybox_marker" 2>/dev/null || true
    return 0
}

_codeybox_seed_home_migration_marker() {
    _codeybox_seed_migration_marker "$1" "$_codeybox_nuget_migrations_marker"
}

codeybox_seed_nuget_migration_state() {
    _codeybox_seen_marker_dirs=
    _codeybox_seed_home_migration_marker "${HOME:-}"
    # Stamped/redirected homes the heal itself materializes may not exist yet;
    # create just the leaf, then let the seeder's private-dir checks re-verify.
    _codeybox_try_adopt_home "${DOTNET_CLI_HOME:-}"
    _codeybox_seed_home_migration_marker "${DOTNET_CLI_HOME:-}"
    # DotnetCliHomeConventions.ResolvePath(WorkingDirectory) — the repo-local
    # home the orchestrator stamps onto raw dotnet auditor execs;
    # _codeybox_repo_home_leaf mirrors the DotnetCliHomeConventions.DirectoryName
    # constant (see the constants block at the top of this file; pinned by the
    # mirror test). Only the repository
    # root can be that working directory — an MSBuild-Exec'd run has a project
    # directory as its cwd and must not grow a stray home there.
    if [ -f "$(pwd -P)/scripts/nuget-home-heal.sh" ]; then
        _codeybox_repo_cli_home="$(pwd -P)/${_codeybox_repo_home_leaf}"
        _codeybox_try_adopt_home "$_codeybox_repo_cli_home"
        _codeybox_seed_home_migration_marker "$_codeybox_repo_cli_home"
    fi
    # The shared per-user fallback both preamble generations redirect to.
    _codeybox_fallback_home="${TMPDIR:-/tmp}/${_codeybox_home_leaf}-${_codeybox_euid}"
    _codeybox_try_adopt_home "$_codeybox_fallback_home"
    _codeybox_seed_home_migration_marker "$_codeybox_fallback_home"
    # Environment.GetFolderPath(UserProfile) falls back to the passwd entry
    # when $HOME is unset — cover a gate exec that resolves no HOME at all.
    # `getent` does not exist on macOS, where this candidate silently skips.
    _codeybox_seed_home_migration_marker \
        "$(getent passwd "$_codeybox_euid" 2>/dev/null | cut -d: -f6)"
    # $XDG_DATA_HOME relocates the whole Migrations tree (no .local/share) —
    # same private-dir and no-symlink rules via the shared helper.
    if [ -n "${XDG_DATA_HOME:-}" ]; then
        _codeybox_seed_migration_marker "$XDG_DATA_HOME" "NuGet/Migrations"
    fi
    return 0
}
# === codeybox-shared-helpers: end ===

# Make a freshly created per-user NuGet home usable for the next `dotnet`: link
# in an inherited package cache (so restore stays offline-safe without a
# re-download) and seed a minimal readable user config (so the fatal user-config
# *read* succeeds without depending on first-run creation). $1 is the
# ".../.nuget/NuGet" directory; $2, when non-empty and existing, is the package
# cache to reuse as the sibling global-packages folder. Every step is
# best-effort so a failure can never abort a `set -e` caller. Shared by the
# in-place and DOTNET_CLI_HOME-redirect recovery paths so both stay offline-safe.
# A merely-existing $2 is not enough: it is consumed only when its WHOLE tree
# verifies as trusted storage (canonical root owned by this uid or root, and
# every descendant owned by this uid or root, never other-writable,
# group-writable only on uid-owned entries whose gid is provably private —
# see _codeybox_secure_package_cache) — a co-tenant-controlled cache is
# refused.
_codeybox_seed_nuget_home() {
    _codeybox_seed_user_dir="$1"
    _codeybox_seed_cache_src="$2"
    # Never create or seed through a symlinked .nuget / NuGet component — the
    # link resolves into whatever directory its owner chose.
    { [ -L "$_codeybox_seed_user_dir" ] \
        || [ -L "${_codeybox_seed_user_dir%/NuGet}" ]; } && return 0
    mkdir -p "$_codeybox_seed_user_dir" 2>/dev/null || return 0
    # The resolved settings dir must stay private per-user storage.
    _codeybox_seed_resolved="$(_codeybox_secure_dir \
        "$_codeybox_seed_user_dir" private writable)" || return 0
    # Strip the trailing "/NuGet" to address the .nuget root that owns the
    # global-packages folder alongside the user config directory.
    _codeybox_seed_root="${_codeybox_seed_resolved%/NuGet}"
    # NuGet only READS an already-extracted package (it checks the
    # .nupkg.metadata marker and skips extraction), so a read-only inherited
    # cache satisfies every restore; a writable one additionally lets NuGet add
    # packages the baseline lacks. But readable is not trustworthy: only a
    # cache whose WHOLE TREE is owned by this uid or root, never
    # other-writable, and group-writable only on uid-owned entries whose gid
    # is provably private may be consumed — the root check alone would admit a
    # co-tenant-writable or foreign-owned descendant, and a tampered extracted
    # entry's build/.props/.targets run attacker code in the gate build.
    _codeybox_seed_cache=
    if [ -n "$_codeybox_seed_cache_src" ]; then
        _codeybox_seed_cache="$(_codeybox_secure_package_cache \
            "$_codeybox_seed_cache_src")" || _codeybox_seed_cache=
    fi
    if [ -n "$_codeybox_seed_cache" ] \
        && [ ! -e "$_codeybox_seed_root/packages" ] \
        && [ ! -L "$_codeybox_seed_root/packages" ]; then
        ln -s "$_codeybox_seed_cache" "$_codeybox_seed_root/packages" 2>/dev/null || true
    fi
    # Repository package sources come from RestoreConfigFile, so an empty user
    # config adds no overrides and clears no sources. Two execs can share the
    # per-user fallback home and race this write, so render to a sibling temp
    # and install with `ln` — an atomic no-clobber create that can never leave
    # a half-written config behind or overwrite one a peer (or NuGet) wrote
    # first. The staging file comes from mktemp (an unpredictable O_EXCL
    # create): a PID-predictable `>` write would follow a co-tenant-planted
    # symlink at the temp path, turning the seed into an arbitrary-path file
    # create/truncate as this user.
    if [ ! -e "$_codeybox_seed_resolved/NuGet.Config" ]; then
        _codeybox_seed_tmp="$(mktemp \
            "$_codeybox_seed_resolved/.NuGet.Config.tmp.XXXXXX" 2>/dev/null)" \
            || _codeybox_seed_tmp=
        if [ -n "$_codeybox_seed_tmp" ] && printf '%s\n' \
            '<?xml version="1.0" encoding="utf-8"?>' \
            '<configuration />' \
            > "$_codeybox_seed_tmp" 2>/dev/null; then
            ln "$_codeybox_seed_tmp" "$_codeybox_seed_resolved/NuGet.Config" 2>/dev/null || true
        fi
        rm -f "$_codeybox_seed_tmp" 2>/dev/null || true
    fi
}

# Release the quarantine lock acquired in codeybox_heal_nuget_home: drop the
# owner-pid file first (the lock dir is only empty once it is gone), then the
# dir itself. Best-effort — a failed release leaves a stale lock the next
# invocation's pid check reaps.
_codeybox_release_quarantine_lock() {
    [ "${_codeybox_lock_acquired:-0}" -eq 1 ] || return 0
    rm -f "$_codeybox_quarantine_lock/pid" 2>/dev/null || true
    rmdir "$_codeybox_quarantine_lock" 2>/dev/null || true
    return 0
}

# ENSURE the cli home's NuGet settings tree is usable: run the shared ensure
# with the "trusted" admission (an unwritable .nuget can still be quarantined
# aside, so its writability is left for the check's own mkdir probe — the
# exec preamble uses "trusted writable" because it cannot recreate in place)
# and remove the writability probe on success. This is NOT a pure predicate —
# like its callee it can create the settings dir and touch the probe — so the
# name carries the same "ensure" warning. Reads $_codeybox_cli_home and
# $_codeybox_nuget_probe; returns the ensure verdict.
_codeybox_ensure_cli_home_usable() {
    if _codeybox_ensure_nuget_home_usable \
        "$_codeybox_cli_home" trusted "" "$_codeybox_nuget_probe" >/dev/null; then
        rm -f "$_codeybox_nuget_probe" 2>/dev/null || true
        return 0
    fi
    return 1
}

# Try to take the quarantine lock, retrying for a bounded budget — fast
# retries first, then a capped 1s backoff — before giving up so the caller
# can defer to the redirect fallback. Always returns 0; the outcome is
# _codeybox_lock_acquired. Reads $_codeybox_quarantine_lock and
# $_codeybox_quarantine_marker.
#
# Stale-lock recovery: reap a lock whose recorded pid no longer exists. A
# lock with a missing/garbage pid file is normally a holder mid-acquire —
# but a holder killed in the mkdir→pid-write window leaves it pidless
# forever, so it is reaped only once the lock dir is provably older than
# that window could ever be (the pid write is immediate;
# _codeybox_lock_pidless_stale_minutes bounds how old "older than the
# acquire window" must be). And a lock whose pid cannot be recorded at
# acquisition is released on the spot rather than left unreapable. A wrong
# reap is still safe: the usable re-check under the lock and the atomic mv
# bound the damage to at most one quarantine directory.
_codeybox_acquire_quarantine_lock() {
    _codeybox_lock_acquired=0
    _codeybox_lock_attempts=0
    _codeybox_lock_max_attempts=100
    _codeybox_lock_fast_attempts=10
    _codeybox_lock_pidless_stale_minutes=1
    while [ "$_codeybox_lock_attempts" -lt "$_codeybox_lock_max_attempts" ]; do
        if mkdir "$_codeybox_quarantine_lock" 2>/dev/null; then
            if printf '%s\n' "$$" \
                > "$_codeybox_quarantine_lock/pid" 2>/dev/null; then
                _codeybox_lock_acquired=1
            else
                rmdir "$_codeybox_quarantine_lock" 2>/dev/null || true
            fi
            break
        fi
        _codeybox_lock_pid="$(cat "$_codeybox_quarantine_lock/pid" \
            2>/dev/null)"
        case $_codeybox_lock_pid in
            ''|*[!0-9]*)
                if find "$_codeybox_quarantine_lock" -maxdepth 0 \
                        -mmin "+${_codeybox_lock_pidless_stale_minutes}" \
                        2>/dev/null | grep -q .; then
                    rm -f "$_codeybox_quarantine_lock/pid" 2>/dev/null || true
                    rmdir "$_codeybox_quarantine_lock" 2>/dev/null || true
                fi ;;
            *)
                if ! kill -0 "$_codeybox_lock_pid" 2>/dev/null; then
                    rm -f "$_codeybox_quarantine_lock/pid" 2>/dev/null || true
                    rmdir "$_codeybox_quarantine_lock" 2>/dev/null || true
                fi ;;
        esac
        if [ "$_codeybox_lock_attempts" -lt "$_codeybox_lock_fast_attempts" ]; then
            sleep 0.1 2>/dev/null || sleep 1 2>/dev/null || true
        else
            sleep 1 2>/dev/null || true
        fi
        _codeybox_lock_attempts=$((_codeybox_lock_attempts + 1))
        if [ -f "$_codeybox_quarantine_marker" ]; then
            break
        fi
    done
    return 0
}

# Run one quarantine EPISODE for the unusable .nuget under the verified
# private cli home: honour the done-marker, take the advisory lock, re-check
# usability under it, rename the broken tree aside at most once, seed the
# replacement home, and record the marker. Returns 0 when the cli home is
# usable afterwards — already usable, healed by a peer, or recreated here —
# and 1 when it is still unusable, in which case the caller takes the
# redirect fallback. Reads $_codeybox_cli_home, $_codeybox_nuget_root,
# $_codeybox_nuget_probe, $_codeybox_quarantine_marker and
# $_codeybox_quarantine_lock; never runs more than one mv per invocation.
_codeybox_quarantine_unusable_nuget_home() {
    if [ -f "$_codeybox_quarantine_marker" ]; then
        # Quarantine already ran for this unusable home. If the home
        # verifies usable again the episode is over: drop the marker so a
        # later breakage can quarantine afresh, then done. While it stays
        # unusable the rename is suppressed and the caller's redirect
        # fallback applies — the marker carries no build identity, so it
        # must never outlive the broken state it recorded.
        if _codeybox_ensure_cli_home_usable; then
            rm -f "$_codeybox_quarantine_marker" 2>/dev/null || true
            return 0
        fi
        return 1
    fi

    _codeybox_acquire_quarantine_lock

    # Re-check under lock (or after waiting): another node may have already healed it.
    if _codeybox_ensure_cli_home_usable; then
        _codeybox_release_quarantine_lock
        return 0
    fi

    if [ "$_codeybox_lock_acquired" -eq 1 ] && [ ! -f "$_codeybox_quarantine_marker" ]; then
        _codeybox_quarantine="$_codeybox_nuget_root.codeybox-unwritable.$$"
        _codeybox_quarantined=0
        if { [ ! -e "$_codeybox_nuget_root" ] && [ ! -L "$_codeybox_nuget_root" ]; }; then
            _codeybox_quarantined=1
        elif mv "$_codeybox_nuget_root" "$_codeybox_quarantine" 2>/dev/null; then
            _codeybox_quarantined=1
            echo "nuget-home self-heal: quarantined unusable $(_codeybox_strip_ctrl "$_codeybox_nuget_root") to $(_codeybox_strip_ctrl "$_codeybox_quarantine")" >&2
        fi

        if [ "$_codeybox_quarantined" -eq 1 ]; then
            if _codeybox_ensure_cli_home_usable; then
                _codeybox_seed_nuget_home "$_codeybox_nuget_root/NuGet" \
                    "$_codeybox_quarantine/packages"
                : > "$_codeybox_quarantine_marker" 2>/dev/null || true
                _codeybox_release_quarantine_lock
                return 0
            fi
            # The broken tree is already renamed aside (or was absent):
            # there is nothing left to quarantine, so mark the attempt
            # done even though recreating the home failed — that is the
            # redirect fallback's job now.
            : > "$_codeybox_quarantine_marker" 2>/dev/null || true
        fi
        # When the rename itself failed the broken tree is still in
        # place: leave the marker unset so a later invocation can retry
        # the quarantine instead of being suppressed — for the MSBuild
        # InitialTargets caller the DOTNET_CLI_HOME redirect cannot
        # propagate back to dotnet, so skipping the rename would
        # strand the home unusable on every later build.
    fi

    _codeybox_release_quarantine_lock
    return 1
}

codeybox_heal_nuget_home() {
    # Outcome token for CODEYBOX_NUGET_HEAL_REPORT consumers (the in-process
    # NuGetHomeGuard.targets task): usable = the env-provided cli home
    # verified in place; redirected = an unusable home was bypassed via the
    # exported fallback DOTNET_CLI_HOME; failed = nothing usable could be
    # materialized and the rejected env values were unset. Set "failed" up
    # front so every early return reports the safe verdict.
    _codeybox_heal_verdict=failed
    # CODEYBOX_NUGET_HOME_UNDER_TEST, when set, names the .nuget directory to
    # repair — an override so tests can exercise the heal against a temporary
    # home without touching the caller's real $HOME; the heal operates on the
    # cli home above it. Either way this hook's mission is the $HOME-based home
    # — an InitialTarget's exports cannot reach the already-running build
    # anyway — so heal $HOME specifically, not a stamped DOTNET_CLI_HOME (that
    # home is the sibling InitialTarget's job, Directory.NuGetHomeHeal.targets).
    # Unset it so the shared recovery resolves HOME as the cli home to repair;
    # the variable then comes back non-empty only when the heal took its
    # redirect fallback. This is a TEST-ONLY override (the identical hook in
    # scripts/ensure-writable-nuget-home.sh carries the same contract): it
    # must never be set for a real build — an env var leaking into one would
    # silently redirect which home is healed and discard an operator-set
    # DOTNET_CLI_HOME.
    if [ -n "${CODEYBOX_NUGET_HOME_UNDER_TEST:-}" ]; then
        _codeybox_cli_home_env="$(dirname "${CODEYBOX_NUGET_HOME_UNDER_TEST%/}")"
        HOME="$_codeybox_cli_home_env"
        export HOME
        unset DOTNET_CLI_HOME
    else
        _codeybox_cli_home_env="${DOTNET_CLI_HOME:-${HOME:-}}"
    fi
    [ -n "$_codeybox_cli_home_env" ] || return 0

    # Admit the env-influenced cli home only as verified private per-user
    # storage BEFORE any write beneath it: canonicalized so a planted or
    # legitimate symlink resolves to the real directory, owned by this uid,
    # writable, never other-writable, and group-writable only under a
    # provably private group — a merely-writable foreign dir
    # would otherwise keep the NuGet.Config every restore reads, the
    # writability probe, and the quarantine writes below inside
    # co-tenant-controlled storage. A missing leaf is materialized only under
    # a verified anchor parent (a stamped repo-local home is env-only until
    # first use). Anything else falls through to the owned redirect chain at
    # the bottom.
    _codeybox_cli_home="$(_codeybox_adopt_dir \
        "$_codeybox_cli_home_env" writable)" || _codeybox_cli_home=
    if [ -n "$_codeybox_cli_home" ]; then
        # Re-export the canonical path under the variable it arrived as, so a
        # symlinked env value cannot be retargeted between these checks and a
        # later dotnet resolution.
        if [ -n "${DOTNET_CLI_HOME:-}" ]; then
            DOTNET_CLI_HOME="$_codeybox_cli_home"
            export DOTNET_CLI_HOME
        else
            HOME="$_codeybox_cli_home"
            export HOME
        fi
        _codeybox_nuget_root="$_codeybox_cli_home/.nuget"
        _codeybox_nuget_probe="$_codeybox_nuget_root/NuGet/.codeybox-writable-probe"
        _codeybox_quarantine_marker="$_codeybox_cli_home/.codeybox-quarantine.done"
        _codeybox_quarantine_lock="$_codeybox_cli_home/.codeybox-quarantine.lock"

        # "trusted" — not "trusted writable": an unwritable .nuget can still
        # be quarantined aside below, so its writability is left for the
        # shared check's own mkdir probe to discover. The exec preamble runs
        # the same check with "trusted writable" because it cannot recreate
        # the directory in place.
        if _codeybox_ensure_cli_home_usable; then
            # A usable home means any earlier quarantine episode is over:
            # drop the marker so a LATER breakage quarantines afresh rather
            # than being suppressed by a stale marker forever.
            rm -f "$_codeybox_quarantine_marker" 2>/dev/null || true
            _codeybox_heal_verdict=usable
            return 0
        fi

        # The inherited per-user NuGet directory is not usable — e.g. an image
        # baked $HOME/.nuget owned by root, a planted link, or a consumed
        # .nuget/packages cache whose tree failed the shared whole-tree
        # admission. The cli home is verified private storage above.
        #
        # Make the heal idempotent and race-free:
        # 1. Destructive repair (the quarantine mv) is NEVER run from a
        #    per-project MSBuild InitialTarget: the MSBuild hook sets
        #    CODEYBOX_NUGET_HEAL_NO_QUARANTINE=1 for project-level
        #    invocations, and only the once-per-solution hook
        #    (Directory.Solution.props) leaves it unset — that Exec explicitly
        #    clears any inherited value first, so an ambient or mistyped
        #    variable can never leak suppression into the mandated repair.
        #    Only the literal value "1" suppresses: "0", "false", or any
        #    other presence-based value does NOT disable the quarantine.
        #    Direct dot-source callers (build.sh, the audit
        #    gates) are single-shot and always allowed.
        # 2. Quarantine runs at most once per unusable-home EPISODE via a
        #    marker file in private storage: the marker suppresses repeat
        #    renames only while the home stays unusable, and is removed as
        #    soon as a usable home is observed again — so a LATER breakage
        #    quarantines afresh rather than being suppressed forever.
        # 3. An exclusive lock in private storage coordinates concurrent
        #    processes, so a concurrent node never renames a directory
        #    another node may be reading; a holder killed mid-quarantine
        #    leaves a stale lock that _codeybox_acquire_quarantine_lock
        #    reaps — by recorded pid when present, by age when the pid was
        #    never written.
        # 4. Never create more than one quarantine directory per invocation.
        if [ "${CODEYBOX_NUGET_HEAL_NO_QUARANTINE:-}" != "1" ] \
            && _codeybox_quarantine_unusable_nuget_home; then
            _codeybox_heal_verdict=usable
            return 0
        fi
    fi

    # The cli-home itself is not safely usable — e.g. an inherited read-only
    # mount that cannot be relocated aside, or foreign-controlled storage:
    # keep the build hermetic by redirecting the .NET CLI home to a verified
    # private per-user directory for this process tree, chosen by the shared
    # _codeybox_pick_user_dir chain — the shared per-user dir, else a private
    # mktemp dir; same chain as the C# preamble (NuGetHomeSelfHeal.Preamble).
    # Seed the chosen home the same way as the in-place path — a readable user
    # config so the fatal read succeeds, and a link to the inherited (trusted)
    # package cache so restore stays offline-safe there too.
    _codeybox_scratch_home="$(_codeybox_pick_user_dir)" || _codeybox_scratch_home=
    if [ -z "$_codeybox_scratch_home" ]; then
        # No private fallback could be materialized: an env value this
        # function REJECTED (empty _codeybox_cli_home) must not stay exported
        # for the caller's dotnet — the shared helper unsets the variable it
        # arrived as. A verified cli home is canonical private storage and
        # stays exported even when its .nuget repair failed; the preamble
        # calls the same helper whenever verification failed AND no fallback
        # exists, because it has no in-place repair.
        if [ -z "$_codeybox_cli_home" ]; then
            _codeybox_unset_home_env
        fi
        return 0
    fi
    DOTNET_CLI_HOME="$_codeybox_scratch_home"
    export DOTNET_CLI_HOME
    _codeybox_seed_nuget_home "$_codeybox_scratch_home/.nuget/NuGet" \
        "$_codeybox_cli_home_env/.nuget/packages"
    _codeybox_heal_verdict=redirected
    return 0
}

# The NuGet-settings heal above does not cover dotnet's OTHER per-user runtime
# need — the user-scoped named-mutex location resolved from
# $XDG_RUNTIME_DIR / $HOME (see codeybox_pin_runtime_dirs in the shared region,
# whose skeleton does the pinning itself — the same chain as the C# preamble,
# so both pick the same dir). This call site's only divergence is remediation:
# a redirected HOME gets its replacement home SEEDED on disk (readable user
# config + linked trusted cache), covering children that resolve packages or
# the settings path off $HOME without inheriting exports; the exec preamble
# exports NUGET_PACKAGES instead, equivalent for the single process it wraps.
codeybox_ensure_dotnet_runtime_dir() {
    codeybox_pin_runtime_dirs
    if [ -n "$_codeybox_pin_home_redirected" ]; then
        _codeybox_seed_nuget_home \
            "$_codeybox_pin_home_redirected/.nuget/NuGet" \
            "$_codeybox_pin_old_home/.nuget/packages"
    fi
    return 0
}

codeybox_heal_nuget_home
# Run AFTER the home heal so a just-redirected DOTNET_CLI_HOME is reused as the
# runtime dir rather than spending a second scratch dir.
codeybox_ensure_dotnet_runtime_dir
# Run LAST so the resolved (possibly just-healed) home values are among the
# seeded candidates.
codeybox_seed_nuget_migration_state

# Machine-readable verdict for callers that cannot inherit this process's
# exports — the in-process NuGetHomeGuard.targets MSBuild task dot-sources
# this file with CODEYBOX_NUGET_HEAL_REPORT=1 (and
# CODEYBOX_NUGET_HEAL_NO_QUARANTINE=1, since per-project repair must stay
# non-destructive) and applies the reported DOTNET_CLI_HOME / HOME /
# package-cache decision inside the MSBuild process itself. Values are
# control-stripped so a crafted path cannot break the KEY=VALUE line format.
#
# CODEYBOX_HEAL_NUGET_PACKAGES is the exact RestorePackagesPath the guard may
# consume — never a reconstructed home + "/.nuget/packages" spelling that can
# retarget, and never an inherited NUGET_PACKAGES value (that env var is
# unverified input). For a usable home it is the canonical path under the
# verified cli home; when the packages entry already exists it is re-scanned
# so a symlinked cache reports its verified target rather than the link. For
# a redirected home it is the original env home's packages tree only when
# that tree itself passed _codeybox_secure_package_cache; otherwise it is the
# redirected home's own packages path, which lives under storage the heal
# just verified private. Failed verdicts report an empty value: failed must
# not repoint NuGet at the rejected tree.
if [ "${CODEYBOX_NUGET_HEAL_REPORT:-}" = "1" ]; then
    _codeybox_report_packages=
    _codeybox_report_verdict="${_codeybox_heal_verdict:-failed}"
    if [ "$_codeybox_report_verdict" = "usable" ] \
        && [ -n "${_codeybox_cli_home:-}" ]; then
        _codeybox_report_packages="${_codeybox_cli_home%/}/.nuget/packages"
        if [ -e "$_codeybox_report_packages" ] \
            || [ -L "$_codeybox_report_packages" ]; then
            _codeybox_report_packages="$(_codeybox_secure_package_cache \
                "$_codeybox_report_packages")" \
                || _codeybox_report_packages=
        fi
    elif [ "$_codeybox_report_verdict" = "redirected" ] \
        && [ -n "${DOTNET_CLI_HOME:-}" ]; then
        if [ -n "${_codeybox_cli_home_env:-}" ]; then
            _codeybox_report_packages="$(_codeybox_secure_package_cache \
                "${_codeybox_cli_home_env%/}/.nuget/packages")" \
                || _codeybox_report_packages=
        fi
        if [ -z "$_codeybox_report_packages" ]; then
            _codeybox_report_packages="${DOTNET_CLI_HOME%/}/.nuget/packages"
        fi
    fi
    printf 'CODEYBOX_HEAL_VERDICT=%s\n' \
        "$(_codeybox_strip_ctrl "${_codeybox_heal_verdict:-failed}")"
    printf 'CODEYBOX_HEAL_DOTNET_CLI_HOME=%s\n' \
        "$(_codeybox_strip_ctrl "${DOTNET_CLI_HOME:-}")"
    printf 'CODEYBOX_HEAL_HOME=%s\n' \
        "$(_codeybox_strip_ctrl "${HOME:-}")"
    printf 'CODEYBOX_HEAL_NUGET_PACKAGES=%s\n' \
        "$(_codeybox_strip_ctrl "$_codeybox_report_packages")"
fi
