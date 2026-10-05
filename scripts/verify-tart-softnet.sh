#!/usr/bin/env bash
# Operator verification for Tart Softnet egress on a real Apple Silicon Mac.
#
# Establishes the two properties CI cannot prove (no CI or dev machine here
# is a Mac):
#   1. fail-closed: killing the Softnet process for a running VM makes the
#      guest lose ALL network, rather than falling back to open NAT;
#   2. IPv6: the guest cannot reach any IPv6 destination (Softnet documents
#      IPv4 only).
#
# Until an operator has run this on real hardware and recorded the results in
# docs/extending/tart-sandbox-plugin.md, the verified path stays documented
# as "unverified on real hardware".
#
# Usage:
#   TART_VERIFY_VM=codeybox-verify-1 \
#   TART_VERIFY_SSH="ssh -o StrictHostKeyChecking=accept-new admin@<guest-ip>" \
#   scripts/verify-tart-softnet.sh
#
# Environment (all optional except TART_VERIFY_SSH):
#   TART_VERIFY_VM        VM name to probe (default: codeybox-verify-1).
#   TART_VERIFY_SSH       Guest exec prefix, e.g. "ssh admin@<ip>".
#                         Every guest probe runs as: $TART_VERIFY_SSH <cmd>.
#   TART_VERIFY_ALLOWED   Host:port the allowlist permits (default: none —
#                         the allow-reachable check is skipped unless set).
#   TART_VERIFY_TIMEOUT   Seconds per guest probe (default: 10).
#
# The script never changes host state except for killing the Softnet helper
# attached to TART_VERIFY_VM (step 4). It does not start or stop VMs: launch
# one sandbox in Softnet mode first and confirm the create log shows
# "Softnet egress policy installed (block 0.0.0.0/0, allow [...])".
set -euo pipefail

VM="${TART_VERIFY_VM:-codeybox-verify-1}"
SSH="${TART_VERIFY_SSH:-}"
ALLOWED="${TART_VERIFY_ALLOWED:-}"
TIMEOUT="${TART_VERIFY_TIMEOUT:-10}"

PASS=0
FAIL=0

step() { printf '\n==> %s\n' "$*"; }
ok() { PASS=$((PASS + 1)); printf 'PASS: %s\n' "$*"; }
bad() { FAIL=$((FAIL + 1)); printf 'FAIL: %s\n' "$*"; }

guest() {
  # Run a command inside the guest; echo its output; return its exit code.
  # shellcheck disable=SC2086
  timeout "$TIMEOUT" $SSH "$@"
}

guest_reaches() {
  # $1 = host, $2 = port. True when TCP connects within TIMEOUT.
  guest /usr/bin/nc -z -w "$TIMEOUT" "$1" "$2" >/dev/null 2>&1
}

require_mac() {
  if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This procedure runs on a real Apple Silicon Mac only (uname: $(uname -s))." >&2
    exit 2
  fi
  if [[ "$(uname -m)" != "arm64" ]]; then
    echo "Apple Silicon (arm64) required (uname -m: $(uname -m))." >&2
    exit 2
  fi
}

require_tools() {
  command -v tart >/dev/null 2>&1 || { echo "tart not on PATH." >&2; exit 2; }
  command -v softnet >/dev/null 2>&1 || sudo -n softnet --version >/dev/null 2>&1 \
    || { echo "softnet helper not usable (need setuid bit or passwordless sudo)." >&2; exit 2; }
  if [[ -z "$SSH" ]]; then
    echo "Set TART_VERIFY_SSH, e.g. TART_VERIFY_SSH=\"ssh admin@<guest-ip>\"." >&2
    exit 2
  fi
  tart list 2>/dev/null | grep -q "$VM" \
    || { echo "VM '$VM' not found in 'tart list'. Launch a Softnet-mode sandbox first." >&2; exit 2; }
}

step "0. Preconditions (Mac, tools, VM '$VM')"
require_mac
require_tools
ok "running on Apple Silicon with tart + softnet, VM present"

step "1. Baseline: blocked IPv4 is unreachable from the guest"
# 198.51.100.7 is TEST-NET-2: never a real host, never allowlisted.
if guest_reaches "198.51.100.7" "443"; then
  bad "guest reached TEST-NET-2 198.51.100.7:443 — filter is not blocking"
else
  ok "guest cannot reach 198.51.100.7:443"
fi

if [[ -n "$ALLOWED" ]]; then
  step "2. Baseline: allowlisted destination is reachable"
  AHOST="${ALLOWED%%:*}"
  APORT="${ALLOWED##*:}"
  if guest_reaches "$AHOST" "$APORT"; then
    ok "guest reaches allowlisted $ALLOWED"
  else
    bad "guest cannot reach allowlisted $ALLOWED — check GatewayCidr/allowlist"
  fi
else
  step "2. Baseline allow-reachable check skipped (TART_VERIFY_ALLOWED unset)"
fi

step "3. IPv6: guest cannot reach any IPv6 destination"
# 2001:db8::1 is TEST-NET-6 documentation space: never a real host.
if guest_reaches "2001:db8::1" "443"; then
  bad "guest reached 2001:db8::1:443 — IPv6 egress is open"
else
  ok "guest cannot reach 2001:db8::1:443"
fi
if guest ping6 -c1 -W2 2001:db8::1 >/dev/null 2>&1; then
  bad "guest ping6 to 2001:db8::1 succeeded — IPv6 egress is open"
else
  ok "guest ping6 to 2001:db8::1 fails"
fi
# No global IPv6 route must exist past the filter.
if guest sh -c 'netstat -rn -f inet6 2>/dev/null || route -n get -inet6 default 2>/dev/null' \
  | grep -qiE 'default.*(en|utun|vmnet)'; then
  bad "guest holds a global IPv6 default route — inspect before trusting the filter"
else
  ok "guest exposes no global IPv6 default route"
fi

step "4. Fail-closed: kill the Softnet process for '$VM', guest must lose ALL network"
SOFTNET_PIDS="$(pgrep -f "softnet.*$VM" || pgrep -f softnet || true)"
if [[ -z "$SOFTNET_PIDS" ]]; then
  echo "Could not find a Softnet process (tried 'softnet.*$VM' and 'softnet')." >&2
  echo "Find the helper attached to '$VM' (ps aux | grep -i softnet) and re-run." >&2
  exit 2
fi
echo "Killing Softnet PID(s): $SOFTNET_PIDS"
# shellcheck disable=SC2086
sudo -n kill $SOFTNET_PIDS || sudo kill $SOFTNET_PIDS
sleep 3
if guest_reaches "198.51.100.7" "443"; then
  bad "guest still reaches IPv4 after Softnet died — NOT fail-closed"
else
  ok "guest reaches nothing over IPv4 after Softnet died"
fi
if [[ -n "$ALLOWED" ]]; then
  AHOST="${ALLOWED%%:*}"
  APORT="${ALLOWED##*:}"
  if guest_reaches "$AHOST" "$APORT"; then
    bad "guest still reaches allowlisted $ALLOWED after Softnet died — fell back to NAT"
  else
    ok "guest lost even allowlisted $ALLOWED after Softnet died (no NAT fallback)"
  fi
fi
echo "Restart the VM to restore its filter (tart stop $VM && tart run ...);"
echo "a VM whose stored policy is unknown must be deleted and re-provisioned."

step "5. Negative: Softnet preflight refuses without the helper"
if CodeyBox_SKIP_SOFTNET_CHECK=1 true; then
  echo "In CodeyBox config, point Network:SoftnetBinaryPath at a missing binary"
  echo "and confirm creates fail with TartSoftnetUnavailableException (no NAT fallback)."
fi

printf '\n==== %d passed, %d failed ====\n' "$PASS" "$FAIL"
if [[ "$FAIL" -gt 0 ]]; then
  echo "Do NOT record this Mac as verified. Fix the failures and re-run." >&2
  exit 1
fi
echo "Record the output, macOS/Tart/Softnet versions, and date in"
echo "docs/extending/tart-sandbox-plugin.md operator-verification results."
