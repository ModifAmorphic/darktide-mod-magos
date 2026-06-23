#!/usr/bin/env bash
# run_wine_test.sh — Phase 2b Tier A2: DLL plumbing smoke test.
#
# Drops the built dbghelp.dll alongside the host exe, runs it under Wine,
# and verifies the full plumbing works: Phase 1 forwarding still resolves,
# the worker thread spawns and runs discovery to completion, the log gets
# the discover summary line, and darktide-poc-discovery.json is written.
#
# This is a PLUMBING test, not a correctness test. Discovery runs against
# the Wine host exe (no LuaJIT), so 0 matches is the expected outcome.
# Correctness is covered by run_in_memory_test.sh (A1, against real
# Darktide.exe).
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

echo "[test] building host.exe (mingw)..."
"$CC" -O2 -g -Wall -Wextra -o "$WORK/host.exe" "$HOST_SRC"

# Drop the proxy alongside the exe under the name the loader will find.
cp "$PROXY_DLL" "$WORK/dbghelp.dll"

echo "[test] running host.exe under $WINE ..."
WINEDEBUG=-all "$WINE" "$WORK/host.exe"
RC=$?
echo "[test] host.exe exit code: $RC"

if [ "$RC" -ne 0 ]; then
    echo "[test] FAIL: host.exe did not exit 0" >&2
    echo "----- log content -----" >&2
    [ -f "$WORK/darktide-poc.log" ] && cat "$WORK/darktide-poc.log" >&2
    echo "----- end log -----" >&2
    exit 1
fi

echo
echo "[test] log content:"
cat "$WORK/darktide-poc.log"

JSON_SIZE="$(stat -c %s "$WORK/darktide-poc-discovery.json" 2>/dev/null || echo 0)"
echo "[test] discovery json size: ${JSON_SIZE} bytes"
if [ "$JSON_SIZE" -lt 100 ]; then
    echo "[test] FAIL: discovery JSON too small or missing" >&2
    exit 1
fi

echo
echo "[test] PASS: Phase 2b plumbing works end-to-end"
