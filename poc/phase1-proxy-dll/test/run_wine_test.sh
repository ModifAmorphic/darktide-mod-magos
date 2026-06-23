#!/usr/bin/env bash
# run_wine_test.sh — Tier A standalone Wine test for the dbghelp proxy.
#
# Builds the test exe, copies the proxy DLL alongside it as dbghelp.dll
# (the same name the game imports), runs it under system wine, and
# verifies:
#   - the host exe exits 0 (all in-process checks passed)
#   - the DllMain attach line appears in darktide-poc.log
#   - the DllMain detach line also appears (proves DllMain ran to completion)
#
# The test deliberately does NOT set WINEDLLOVERRIDES. Our host.exe
# LoadLibrary's "dbghelp.dll" by relative name, which the loader finds in
# the current directory via standard search order. The proxy then
# LoadLibrary's the real builtin from C:\Windows\System32\dbghelp.dll.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROXY_DLL="$SCRIPT_DIR/../build/dbghelp.dll"
HOST_SRC="$SCRIPT_DIR/host.c"
WORK="$SCRIPT_DIR/build"
WINE="${WINE:-$(command -v wine || true)}"
CC="${CC:-x86_64-w64-mingw32-gcc}"

if [ -z "$WINE" ]; then
    echo "ERROR: wine not found on PATH. Set WINE=<path> or install wine." >&2
    exit 1
fi
if [ ! -f "$PROXY_DLL" ]; then
    echo "ERROR: $PROXY_DLL not found. Run ./build.sh first." >&2
    exit 1
fi

mkdir -p "$WORK"
rm -f "$WORK/darktide-poc.log" "$WORK/dbghelp.dll" "$WORK/host.exe"

echo "[test] building host.exe ..."
"$CC" -O2 -g -Wall -Wextra \
    -o "$WORK/host.exe" "$HOST_SRC"

# Drop the proxy alongside the exe under the name the loader will find.
cp "$PROXY_DLL" "$WORK/dbghelp.dll"

echo "[test] running host.exe under $WINE ..."
WINEDEBUG=-all "$WINE" "$WORK/host.exe"
RC=$?
echo "[test] host.exe exit code: $RC"

if [ "$RC" -ne 0 ]; then
    echo "[test] FAIL: host.exe did not exit 0" >&2
    exit 1
fi

if [ ! -f "$WORK/darktide-poc.log" ]; then
    echo "[test] FAIL: darktide-poc.log not written" >&2
    exit 1
fi

ATTACH="$(grep -c 'DLL_PROCESS_ATTACH' "$WORK/darktide-poc.log" || true)"
DETACH="$(grep -c 'DLL_PROCESS_DETACH' "$WORK/darktide-poc.log" || true)"
MARKER="$(grep -c '\[darktide-poc\]' "$WORK/darktide-poc.log" || true)"

echo "[test] log analysis: attach=$ATTACH detach=$DETACH marker=$MARKER"

if [ "$ATTACH" -lt 1 ] || [ "$DETACH" -lt 1 ] || [ "$MARKER" -lt 1 ]; then
    echo "[test] FAIL: log missing required entries" >&2
    echo "----- log content -----" >&2
    cat "$WORK/darktide-poc.log" >&2
    echo "----- end log -----" >&2
    exit 1
fi

echo "[test] log content:"
cat "$WORK/darktide-poc.log"

echo
echo "[test] PASS: all Tier A checks succeeded"
