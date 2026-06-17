#!/usr/bin/env sh
# Build the ACP bridge as a self-contained, statically-linked NativeAOT
# binary and refresh the embedded resource in CodeyBox.Agents.Claude.
#
# Operator prerequisites on the build host:
#   - .NET 10 SDK
#   - musl-tools  (for the musl static linker the ILCompiler invokes;
#                  apt-get install musl-tools on Debian/Ubuntu hosts)
#
# Without musl-tools installed, the publish step fails and the bridge
# resource falls back to the tracked Resources/acp-bridge.placeholder
# stub — the runtime path then degrades to the print transport on first
# use rather than stranding work items.
#
# Usage: scripts/publish-acp-bridge.sh
set -eu

cd "$(dirname "$0")/.."

BRIDGE_PROJECT="src/CodeyBox.Agents.Claude.AcpBridge/CodeyBox.Agents.Claude.AcpBridge.csproj"
RESOURCE_DIR="src/CodeyBox.Agents.Claude/Resources"
RESOURCE_NAME="acp-bridge"
RID="linux-musl-x64"

mkdir -p "$RESOURCE_DIR"

dotnet publish "$BRIDGE_PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishAot=true \
    -p:StaticallyLinked=true

PUBLISH_DIR="src/CodeyBox.Agents.Claude.AcpBridge/bin/Release/net10.0/$RID/publish"
SOURCE_BIN="$PUBLISH_DIR/CodeyBox.Agents.Claude.AcpBridge"

if [ ! -f "$SOURCE_BIN" ]; then
    echo "ERROR: published binary not found at $SOURCE_BIN" >&2
    exit 1
fi

cp "$SOURCE_BIN" "$RESOURCE_DIR/$RESOURCE_NAME"
chmod 644 "$RESOURCE_DIR/$RESOURCE_NAME"

echo "Embedded resource refreshed:"
ls -la "$RESOURCE_DIR/$RESOURCE_NAME"
file "$RESOURCE_DIR/$RESOURCE_NAME" 2>/dev/null || true
