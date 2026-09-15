#!/usr/bin/env bash
# CodeyBox installer — Linux only.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/AdamFrisby/CodeyBox/main/install.sh | bash
#   ./install.sh [--yes] [--dir PATH] [--provider incus|multipass] [--no-network-setup]
#
# Every step is idempotent: re-running after a failure resumes rather than
# duplicating. The script exits non-zero if any step it reports as done did not
# actually succeed — a caller can trust the exit code.
set -euo pipefail

readonly REPO_URL="https://github.com/AdamFrisby/CodeyBox.git"
readonly DOTNET_MAJOR="10"

# When run as `curl … | bash`, stdin is the script itself, so prompts must read
# from the terminal. If there is no terminal we are non-interactive and fall
# back to defaults (equivalent to --yes) rather than blocking forever.
if [ -r /dev/tty ] && [ -t 1 ]; then TTY=/dev/tty; else TTY=""; fi

ASSUME_YES=0
INSTALL_DIR=""
PROVIDER=""
DO_NETWORK_SETUP=1

if [ -n "${NO_COLOR:-}" ] || [ ! -t 1 ]; then
    B=""; R=""; Y=""; G=""; N=""
else
    B=$'\033[1m'; R=$'\033[31m'; Y=$'\033[33m'; G=$'\033[32m'; N=$'\033[0m'
fi

say()  { printf '%s\n' "$*"; }
step() { printf '\n%s==>%s %s%s%s\n' "$G" "$N" "$B" "$*" "$N"; }
warn() { printf '%s warning:%s %s\n' "$Y" "$N" "$*" >&2; }
die()  { printf '%s error:%s %s\n' "$R" "$N" "$*" >&2; exit 1; }

ask() {
    # ask <prompt> <default: y|n> -> returns 0 for yes
    local prompt="$1" default="${2:-y}" reply=""
    if [ "$ASSUME_YES" = 1 ] || [ -z "$TTY" ]; then
        [ "$default" = "y" ]; return
    fi
    local hint="[Y/n]"; [ "$default" = "n" ] && hint="[y/N]"
    printf '%s %s ' "$prompt" "$hint" > "$TTY"
    read -r reply < "$TTY" || reply=""
    reply="${reply:-$default}"
    case "$reply" in [yY]*) return 0 ;; *) return 1 ;; esac
}

askval() {
    # askval <prompt> <default> -> echoes the answer
    local prompt="$1" default="$2" reply=""
    if [ "$ASSUME_YES" = 1 ] || [ -z "$TTY" ]; then printf '%s' "$default"; return; fi
    printf '%s [%s] ' "$prompt" "$default" > "$TTY"
    read -r reply < "$TTY" || reply=""
    printf '%s' "${reply:-$default}"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --yes|-y)           ASSUME_YES=1 ;;
        --dir)              INSTALL_DIR="${2:-}"; shift ;;
        --provider)         PROVIDER="${2:-}"; shift ;;
        --no-network-setup) DO_NETWORK_SETUP=0 ;;
        -h|--help)          sed -n '2,12p' "$0"; exit 0 ;;
        *)                  die "unknown option: $1 (try --help)" ;;
    esac
    shift
done

# ---------------------------------------------------------------- platform ---
step "Checking platform"

case "$(uname -s)" in
    Linux) : ;;
    Darwin) die "macOS is not supported.

CodeyBox enforces its sandbox egress allowlist with nftables on host-managed
Linux bridges. That mechanism does not exist on macOS, so sandboxes there would
run with uncontrolled network access while the product claims an allowlist.

Supporting macOS and Windows properly is tracked work, targeted at a later
release. See docs/concepts/security.md for what the guarantee depends on." ;;
    *) die "$(uname -s) is not supported. CodeyBox 0.7 is Linux only." ;;
esac
say "  Linux $(uname -r) on $(uname -m)"

if [ ! -e /dev/kvm ]; then
    warn "/dev/kvm is missing. Without KVM you cannot run the VM-backed sandbox
  providers (incus, multipass) — only the shared-kernel 'bubblewrap' provider or
  the isolation-free 'process' one, neither of which is suitable for untrusted
  work. Install KVM, or continue for local experiments only."
    ask "  Continue anyway?" n || die "stopped; install KVM and re-run"
else
    say "  /dev/kvm present"
fi

# ------------------------------------------------------------ prerequisites ---
step "Checking prerequisites"

need_cmd() { command -v "$1" >/dev/null 2>&1; }

# Install one package using whichever package manager this distribution has.
# Deliberately limited to well-known names that are the same across distros.
pkg_install() {
    local pkg="$1"
    if   need_cmd apt-get; then sudo apt-get update -qq && sudo apt-get install -y -qq "$pkg"
    elif need_cmd dnf;     then sudo dnf install -y -q "$pkg"
    elif need_cmd pacman;  then sudo pacman -Sy --noconfirm "$pkg"
    elif need_cmd zypper;  then sudo zypper --non-interactive install "$pkg"
    elif need_cmd apk;     then sudo apk add --quiet "$pkg"
    else return 1
    fi
}

if ! need_cmd git; then
    say "  git is not installed."
    if ask "  Install git now?" y; then
        pkg_install git || die "could not install git automatically. Install it with your
  package manager and re-run."
        need_cmd git || die "git was installed but is not on PATH"
    else
        die "git is required"
    fi
fi
say "  git $(git --version | awk '{print $3}')"

dotnet_ok=0
if need_cmd dotnet; then
    if dotnet --list-sdks 2>/dev/null | grep -q "^${DOTNET_MAJOR}\."; then
        dotnet_ok=1
        say "  .NET SDK $(dotnet --list-sdks | grep "^${DOTNET_MAJOR}\." | tail -1 | awk '{print $1}')"
    else
        warn ".NET is installed but no ${DOTNET_MAJOR}.x SDK was found."
    fi
fi

if [ "$dotnet_ok" = 0 ]; then
    say "  CodeyBox needs the .NET ${DOTNET_MAJOR} SDK."
    if ask "  Install it now via the official dotnet-install script?" y; then
        tmp="$(mktemp -d)"
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp/dotnet-install.sh" \
            || die "could not download dotnet-install.sh"
        bash "$tmp/dotnet-install.sh" --channel "${DOTNET_MAJOR}.0" --install-dir "$HOME/.dotnet" \
            || die ".NET SDK install failed"
        rm -rf "$tmp"
        export PATH="$HOME/.dotnet:$PATH"
        need_cmd dotnet || die ".NET installed to \$HOME/.dotnet but dotnet is still not on PATH"
        say "  Add this to your shell profile:  export PATH=\"\$HOME/.dotnet:\$PATH\""
    else
        die "the .NET ${DOTNET_MAJOR} SDK is required — see https://dotnet.microsoft.com/download"
    fi
fi

# --------------------------------------------------------------- provider ----
step "Choosing a sandbox provider"

if [ -z "$PROVIDER" ]; then
    say "  incus     — persistent headless VMs, needs Incus 6.3+ and a ZFS or Btrfs pool"
    say "  multipass — simplest to install, good for a first run"
    PROVIDER="$(askval "  Which provider?" "multipass")"
fi

case "$PROVIDER" in
    incus|multipass) : ;;
    *) die "unknown provider '$PROVIDER' (expected 'incus' or 'multipass')" ;;
esac
say "  Using: $PROVIDER"

if ! need_cmd "$PROVIDER"; then
    say "  $PROVIDER is not installed."
    if [ "$PROVIDER" = multipass ]; then
        if ask "  Install multipass now (sudo snap install multipass)?" y; then
            if ! need_cmd snap; then
                say "  multipass ships as a snap, and snapd is not installed."
                pkg_install snapd || die "could not install snapd automatically. Install
  snapd (or multipass directly) with your package manager and re-run."
                need_cmd snap || die "snapd was installed but 'snap' is not on PATH"
            fi
            sudo snap install multipass || die "multipass install failed"
            # snapd installs into /snap/bin and adds it to PATH via
            # /etc/profile.d/snapd.sh, which only applies to a *new* login
            # shell. Add it here so the rest of this run can see multipass.
            if ! need_cmd multipass && [ -x /snap/bin/multipass ]; then
                export PATH="$PATH:/snap/bin"
                say "  Added /snap/bin to PATH for this run"
            fi
            need_cmd multipass || die "multipass installed but not on PATH (expected /snap/bin/multipass)"
        else
            die "multipass is required for the 'multipass' provider"
        fi
    else
        # Incus has no single install command that is correct across distributions
        # (it is packaged by distributions and by zabbly, not as a snap), and it
        # additionally needs an initialised ZFS or Btrfs storage pool. Guessing
        # here would produce a half-configured host, so send the operator to the
        # upstream instructions instead of pretending.
        die "incus is not installed.

  Install Incus 6.3+ for your distribution (https://linuxcontainers.org/incus/),
  run 'sudo incus admin init' and create a ZFS or Btrfs storage pool, then
  re-run this script. Or re-run with --provider multipass for the simplest path."
    fi
fi
say "  $PROVIDER is installed"

if [ "$PROVIDER" = incus ]; then
    if ! incus storage list >/dev/null 2>&1; then
        warn "Cannot query Incus storage. You may need to be in the 'incus-admin'
  group (newgrp incus-admin), or run 'sudo incus admin init'."
    fi
fi

# ----------------------------------------------------------------- source ----
step "Fetching CodeyBox"

if [ -z "$INSTALL_DIR" ]; then
    INSTALL_DIR="$(askval "  Install to which directory?" "$HOME/codeybox")"
fi

if [ -d "$INSTALL_DIR/.git" ]; then
    say "  Existing checkout at $INSTALL_DIR — updating"
    git -C "$INSTALL_DIR" fetch --quiet origin || die "git fetch failed in $INSTALL_DIR"
    git -C "$INSTALL_DIR" merge --ff-only origin/main --quiet \
        || warn "could not fast-forward $INSTALL_DIR (local changes?); continuing with what is there"
else
    [ -e "$INSTALL_DIR" ] && die "$INSTALL_DIR exists and is not a git checkout"
    git clone --quiet "$REPO_URL" "$INSTALL_DIR" || die "git clone failed"
    say "  Cloned to $INSTALL_DIR"
fi
cd "$INSTALL_DIR"

# ---------------------------------------------------------- host firewall ----
step "Host network isolation"

if [ "$DO_NETWORK_SETUP" = 0 ]; then
    warn "Skipping host network setup at your request.

  Sandboxes will NOT have an enforced egress allowlist. This is the layer an
  agent cannot switch off from inside its VM. Do not point this installation at
  a repository that matters until you have run:
      sudo scripts/setup-host-networks.sh"
else
    say "  Sandbox egress is enforced by nftables rules on host bridges."
    say "  This needs sudo, and is what makes unattended work safe."
    if ask "  Run 'sudo scripts/setup-host-networks.sh' now?" y; then
        sudo scripts/setup-host-networks.sh || die "host network setup failed — sandboxes would have
  no enforced egress allowlist, so stopping here rather than continuing"
        say "  Host bridges and nftables rules are in place"
    else
        warn "Declined. Sandboxes will have NO enforced egress allowlist until you run
      sudo scripts/setup-host-networks.sh
  Treat this installation as a local experiment only."
    fi
fi

# ------------------------------------------------------------------ build ----
step "Building"

./build.sh 2>/dev/null || dotnet build CodeyBox.slnx || die "build failed"
say "  Build succeeded"

# ----------------------------------------------------------------- config ----
step "Configuration"

CONFIG_PATH="${CODEYBOX_EXTRA_CONFIG:-$HOME/.config/codeybox/codeybox.json}"
if [ -f "$CONFIG_PATH" ]; then
    say "  Keeping existing config at $CONFIG_PATH"
else
    mkdir -p "$(dirname "$CONFIG_PATH")"
    PROJECT_ID="$(askval "  A short id for your first project" "my-app")"
    REPO="$(askval "  Repository URL (leave as-is for a scratch local repo)" "https://github.com/owner/my-app.git")"
    BRANCH="$(askval "  Base branch" "main")"
    AGENT="$(askval "  Agent CLI to use (claude, codex, copilot, opencode, …)" "claude")"

    cat > "$CONFIG_PATH" <<EOF
{
  "CodeyBox": {
    "SandboxProvider": "$PROVIDER",
    "Projects": [
      {
        "Id": "$PROJECT_ID",
        "RepositoryUrl": "$REPO",
        "BaseBranch": "$BRANCH",
        "Agent": "$AGENT",
        "Upstream": { "Kind": "noop" }
      }
    ]
  }
}
EOF
    say "  Wrote $CONFIG_PATH"
    say "  Upstream.Kind is 'noop': results stay local and cannot be pushed anywhere."
fi

KEY_FILE="$HOME/.config/codeybox/api-key"
if [ -f "$KEY_FILE" ]; then
    say "  Reusing API key from $KEY_FILE"
else
    mkdir -p "$(dirname "$KEY_FILE")"
    ( umask 077; openssl rand -hex 32 > "$KEY_FILE" ) || die "could not generate an API key"
    say "  Generated an API key at $KEY_FILE (mode 600)"
fi

# ------------------------------------------------------------------- done ----
step "Done"

cat <<EOF

  CodeyBox is installed at $INSTALL_DIR

  Start it with:

      cd $INSTALL_DIR
      export CODEYBOX_API_KEY=\$(cat $KEY_FILE)
      export CODEYBOX_EXTRA_CONFIG=$CONFIG_PATH
      export <YOUR_AGENT_CREDENTIAL_VAR>=...    # see docs/concepts/agents.md
      dotnet run --project src/CodeyBox.Api

  Then queue something small — docs/getting-started.md picks up from here.

  Before pointing this at a repository that matters, read
  docs/concepts/security.md.

EOF
