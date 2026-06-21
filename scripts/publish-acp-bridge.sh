#!/usr/bin/env sh
# Build the ACP bridge as a self-contained, statically-linked NativeAOT
# binary, verify it can execute in a Multipass VM, and refresh the embedded
# resource in CodeyBox.Agents.Claude.
#
# Operator prerequisites on the build host:
#   - .NET 10 SDK
#   - musl-tools (for the musl static linker the ILCompiler invokes)
#   - clang (NativeAOT passes --target=x86_64-linux-musl, which GCC rejects)
#   - lld (the project sets LinkerFlavor=lld)
#   - multipass (for the required in-VM runtime verification)
#
# Usage:
#   scripts/publish-acp-bridge.sh
#   CODEYBOX_ACP_BRIDGE_VERIFY_VM=cb-baseline-abc123 scripts/publish-acp-bridge.sh
#
# The verification VM defaults to a temporary Multipass instance launched from
# CODEYBOX_ACP_BRIDGE_VERIFY_IMAGE (default: 24.04). Set
# CODEYBOX_ACP_BRIDGE_VERIFY_VM to an already-baked CodeyBox baseline when the
# release must prove the binary on the exact sandbox image operators will clone.
# Compile-only CI may pass --skip-multipass-verify, but release artifacts must
# not use that escape hatch.
set -eu

SKIP_VM_VERIFY=0
for arg in "$@"; do
    case "$arg" in
        --skip-multipass-verify)
            SKIP_VM_VERIFY=1
            ;;
        *)
            echo "Usage: scripts/publish-acp-bridge.sh [--skip-multipass-verify]" >&2
            exit 64
            ;;
    esac
done

if [ "${CODEYBOX_ACP_BRIDGE_SKIP_VM_VERIFY:-0}" = "1" ]; then
    SKIP_VM_VERIFY=1
fi

cd "$(dirname "$0")/.."

BRIDGE_PROJECT="src/CodeyBox.Agents.Claude.AcpBridge/CodeyBox.Agents.Claude.AcpBridge.csproj"
RESOURCE_DIR="src/CodeyBox.Agents.Claude/Resources"
RESOURCE_NAME="acp-bridge"
RESOURCE_PATH="$RESOURCE_DIR/$RESOURCE_NAME"
TMP_RESOURCE="$RESOURCE_DIR/.$RESOURCE_NAME.$$.new"
RID="linux-musl-x64"
CREATED_VERIFY_VM=""

cleanup()
{
    if [ -n "$TMP_RESOURCE" ]; then
        rm -f "$TMP_RESOURCE"
    fi
    if [ -n "$CREATED_VERIFY_VM" ] && command -v multipass >/dev/null 2>&1; then
        multipass delete --purge "$CREATED_VERIFY_VM" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT INT TERM

mkdir -p "$RESOURCE_DIR"

# Remove the old ignored resource before publishing. If dotnet publish or any
# verification step fails, the parent project can only fall back to the tracked
# placeholder; it cannot silently embed stale bytes from an earlier successful
# run.
rm -f "$RESOURCE_PATH" "$TMP_RESOURCE"

dotnet publish "$BRIDGE_PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishAot=true \
    -p:StaticExecutable=true

PUBLISH_DIR="src/CodeyBox.Agents.Claude.AcpBridge/bin/Release/net10.0/$RID/publish"
SOURCE_BIN="$PUBLISH_DIR/CodeyBox.Agents.Claude.AcpBridge"

if [ ! -f "$SOURCE_BIN" ]; then
    echo "ERROR: published binary not found at $SOURCE_BIN" >&2
    exit 1
fi

cp "$SOURCE_BIN" "$TMP_RESOURCE"
chmod 755 "$TMP_RESOURCE"

echo "Candidate bridge binary:"
ls -la "$TMP_RESOURCE"
file "$TMP_RESOURCE" 2>/dev/null || true

# Sanity-check the static-link claim: the binary must NOT advertise a dynamic
# interpreter. ldd on a static-PIE ELF prints "not a dynamic executable" or
# "statically linked". A dynamically-linked output is a HARD FAILURE: the
# bridge ships into a Multipass sandbox whose glibc version is not guaranteed
# to match the build host.
if command -v ldd >/dev/null 2>&1; then
    LDD_OUT="$(ldd "$TMP_RESOURCE" 2>&1 || true)"
    echo "ldd: $LDD_OUT"
    case "$LDD_OUT" in
        *"not a dynamic executable"*|*"statically linked"*) ;;
        *)
            echo "ERROR: published binary appears dynamically linked — check StaticExecutable and musl-tools." >&2
            exit 1
            ;;
    esac
fi

if [ "$SKIP_VM_VERIFY" = "1" ]; then
    echo "WARNING: skipping required Multipass runtime verification by explicit request." >&2
else
    if ! command -v multipass >/dev/null 2>&1; then
        echo "ERROR: multipass is required to verify the ACP bridge inside the sandbox image." >&2
        echo "       Set CODEYBOX_ACP_BRIDGE_VERIFY_VM to a baked baseline VM, or install Multipass." >&2
        exit 1
    fi

    VERIFY_VM="${CODEYBOX_ACP_BRIDGE_VERIFY_VM:-}"
    if [ -z "$VERIFY_VM" ]; then
        VERIFY_IMAGE="${CODEYBOX_ACP_BRIDGE_VERIFY_IMAGE:-24.04}"
        VERIFY_VM="cb-acp-bridge-verify-$$"
        CREATED_VERIFY_VM="$VERIFY_VM"
        echo "Launching temporary Multipass verification VM $VERIFY_VM from image $VERIFY_IMAGE..."
        multipass launch --name "$VERIFY_VM" --cpus 1 --memory 1G --disk 4G "$VERIFY_IMAGE"
    else
        echo "Using existing Multipass verification VM $VERIFY_VM..."
        multipass start "$VERIFY_VM" >/dev/null 2>&1 || true
    fi

    REMOTE="/tmp/codeybox-acp-bridge-$$"
    multipass transfer "$TMP_RESOURCE" "$VERIFY_VM:$REMOTE"
    multipass exec "$VERIFY_VM" -- chmod 700 "$REMOTE"
    VERIFY_OUT="$(multipass exec "$VERIFY_VM" -- sh -c "$REMOTE </dev/null" 2>&1)" || {
        echo "ERROR: bridge failed to execute inside Multipass VM $VERIFY_VM:" >&2
        echo "$VERIFY_OUT" >&2
        exit 1
    }
    multipass exec "$VERIFY_VM" -- rm -f "$REMOTE" >/dev/null 2>&1 || true
    case "$VERIFY_OUT" in
        *'"type":"bridge_started"'*) ;;
        *)
            echo "ERROR: bridge executed in Multipass but did not emit bridge_started:" >&2
            echo "$VERIFY_OUT" >&2
            exit 1
            ;;
    esac
    echo "Multipass runtime verification passed on $VERIFY_VM."
fi

mv "$TMP_RESOURCE" "$RESOURCE_PATH"
chmod 644 "$RESOURCE_PATH"
TMP_RESOURCE=""

echo "Embedded resource refreshed:"
ls -la "$RESOURCE_PATH"
