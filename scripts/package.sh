#!/usr/bin/env bash
# Build the distributable CodeyBox release tarball for linux-x64.
#
# CodeyBox is deliberately Docker-free: the artifact this produces is a plain
# directory you extract and run. It is framework-dependent-free (self-contained)
# but NOT single-file — the four apps share one copy of the .NET runtime, which
# is why the tarball is a fraction of the size four single-file publishes would
# be. The target host needs no .NET SDK or runtime installed.
#
# Usage:  scripts/package.sh [output-dir]
# Output: <output-dir>/codeybox-<version>-linux-x64.tar.gz
#         <output-dir>/SHA256SUMS
set -euo pipefail

repo_root="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
out_dir="${1:-$repo_root/dist}"
rid="linux-x64"

# Heal an inherited root-owned NuGet home before restore, the same way build.sh
# does. Sourced relative to this script so it works from any working directory.
if [ -f "$repo_root/scripts/nuget-home-heal.sh" ]; then
    # shellcheck source=/dev/null
    . "$repo_root/scripts/nuget-home-heal.sh"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export MSBUILDDISABLENODEREUSE=1

# The version is declared once, in Directory.Build.props, so the tarball name and
# the assembly metadata can never disagree.
version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repo_root/Directory.Build.props" | head -1)"
if [ -z "$version" ]; then
    echo "package.sh: no <VersionPrefix> in Directory.Build.props" >&2
    exit 1
fi

stage="$(mktemp -d "${TMPDIR:-/tmp}/codeybox-package.XXXXXX")"
trap 'rm -rf "$stage"' EXIT
payload="$stage/codeybox-$version-$rid"
mkdir -p "$payload"

# The runtime-sharing apps publish into the SAME directory on purpose: one copy
# of the .NET runtime serves all of them. Publishing them separately would
# multiply the runtime by three. PublishSingleFile is explicitly off for the
# same reason.
publish_shared() {
    local proj="$1"
    echo "==> publishing $proj (shared runtime)"
    dotnet publish "$repo_root/$proj" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:PublishTrimmed=false \
        -p:DebugType=none \
        -o "$payload"
}

# CodeyBox.Cli sets PublishAot. Native compilation implies PublishTrimmed and
# refuses to have it turned off, so this project cannot join the shared-runtime
# publish above — and does not need to: AOT emits one standalone native
# executable with no runtime dependency. It publishes to its own directory and
# only the produced binary is copied in.
publish_aot() {
    local proj="$1" binary="$2"
    echo "==> publishing $proj (native AOT)"
    local aot_out="$stage/aot-$binary"
    dotnet publish "$repo_root/$proj" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -o "$aot_out"
    if [ ! -x "$aot_out/$binary" ]; then
        echo "package.sh: expected native binary '$binary' in $aot_out" >&2
        ls -la "$aot_out" >&2
        exit 1
    fi
    cp "$aot_out/$binary" "$payload/$binary"
}

publish_shared src/CodeyBox.Api/CodeyBox.Api.csproj
publish_shared tools/CodeyBox.Admin/src/CodeyBox.Admin.Web/CodeyBox.Admin.Web.csproj
publish_shared src/CodeyBox.Executor/CodeyBox.Executor.csproj
publish_aot    tools/CodeyBox.Cli/CodeyBox.Cli.csproj CodeyBox.Cli

# Friendly entry points. The assemblies keep their .NET names on purpose:
# CodeyBox.Api's assembly name is ASP.NET Core's ApplicationName (it drives
# content-root and user-secrets discovery) and CodeyBox.Admin.Web is Blazor,
# where it drives the _framework asset paths. Renaming either to get a prettier
# executable would trade a real runtime behaviour for a cosmetic one, so the
# short names are launchers instead.
launcher() {
    local name="$1" target="$2"
    cat > "$payload/$name" <<LAUNCHER
#!/usr/bin/env sh
exec "\$(CDPATH= cd -- "\$(dirname -- "\$0")" && pwd)/$target" "\$@"
LAUNCHER
    chmod +x "$payload/$name"
}
launcher codeybox              CodeyBox.Cli
launcher codeybox-orchestrator CodeyBox.Api
launcher codeybox-admin        CodeyBox.Admin.Web
launcher codeybox-executor     CodeyBox.Executor

# Documentation an operator needs before the first run ships inside the tarball;
# a release downloaded without the repository is otherwise undocumented.
cp "$repo_root/README.md" "$repo_root/LICENSE" "$payload/"
mkdir -p "$payload/docs"
cp -r "$repo_root/docs/." "$payload/docs/" 2>/dev/null || true

mkdir -p "$out_dir"
tarball="$out_dir/codeybox-$version-$rid.tar.gz"
rm -f "$tarball"
# --sort=name and a fixed mtime make the tarball reproducible: the same source
# tree produces the same bytes, so the published SHA256 is verifiable.
tar --sort=name \
    --mtime="@0" --owner=0 --group=0 --numeric-owner \
    -C "$stage" -czf "$tarball" "codeybox-$version-$rid"

( cd "$out_dir" && sha256sum "$(basename "$tarball")" > SHA256SUMS )

echo
echo "built  $tarball  ($(du -h "$tarball" | cut -f1))"
echo "sha256 $(cut -d' ' -f1 "$out_dir/SHA256SUMS")"
