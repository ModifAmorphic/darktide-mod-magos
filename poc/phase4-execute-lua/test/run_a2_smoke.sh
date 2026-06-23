#!/usr/bin/env bash
# run_a2_smoke.sh — Phase 4 Tier A2: DLL plumbing smoke test.
#
# Drops the built dbghelp.dll alongside the host exe, runs it under Wine,
# and verifies the full plumbing works: Phase 1 forwarding still resolves,
# Phase 3 hook install runs (and either installs or aborts cleanly — the
# Wine host exe is tiny so the bounds check aborts), Phase 4 inject_install
# runs (same), the Phase 2b worker thread spawns and runs discovery to
# completion, the log gets the discover summary line, and
# darktide-poc-discovery.json is written.
#
# This is a PLUMBING test, not an injection-correctness test. The live
# Lua VM is absent under Wine, so no injection can fire. Phase 4 injection
# correctness is covered by run_a1_inject_test.sh.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROXY_DLL="$SCRIPT_DIR/../build/dbghelp.dll"
HOST_SRC="$SCRIPT_DIR/host.c"
WORK="$SCRIPT_DIR/build"
WINE="${WINE:-$(command -v wine || true)}"
CC="${CC:-x86_64-w64-mingw32-gcc}"

if [ -z "$WINE" ]; then
    echo "ERROR: wine not found. Set WINE=<path>." >&2; exit 1
fi
if [ ! -f "$PROXY_DLL" ]; then
    echo "ERROR: $PROXY_DLL not found. Run ../build.sh first." >&2; exit 1
fi

mkdir -p "$WORK"
rm -f "$WORK/darktide-poc.log" "$WORK/darktide-poc-discovery.json" \
      "$WORK/dbghelp.dll" "$WORK/host.exe"

echo "[a2] building host.exe (mingw)..."
"$CC" -O2 -g -Wall -Wextra -o "$WORK/host.exe" "$HOST_SRC"

# Drop the proxy alongside the exe under the name the loader will find.
cp "$PROXY_DLL" "$WORK/dbghelp.dll"

echo "[a2] running host.exe under $WINE ..."
set +e
WINEDEBUG=-all "$WINE" "$WORK/host.exe"
RC=$?
set -e

echo "[a2] host.exe exit code: $RC"

if [ "$RC" -ne 0 ]; then
    echo "[a2] FAIL: host.exe did not exit 0" >&2
    echo "----- log content -----" >&2
    [ -f "$WORK/darktide-poc.log" ] && cat "$WORK/darktide-poc.log" >&2
    echo "----- end log -----" >&2
    exit 1
fi

echo
echo "[a2] log content:"
cat "$WORK/darktide-poc.log"

JSON_SIZE="$(stat -c %s "$WORK/darktide-poc-discovery.json" 2>/dev/null || echo 0)"
echo "[a2] discovery json size: ${JSON_SIZE} bytes"
if [ "$JSON_SIZE" -lt 100 ]; then
    echo "[a2] FAIL: discovery JSON too small or missing" >&2
    exit 1
fi

echo
echo "[a2] PASS: Phase 4 plumbing works end-to-end"
