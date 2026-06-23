#!/usr/bin/env bash
# build.sh — cross-compile the dbghelp proxy DLL with mingw-w64.
#
# Produces build/dbghelp.dll (PE-x86-64). Run from anywhere; paths are
# resolved relative to this script.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---- Configuration -----------------------------------------------------
REF_DBGHELP="${REF_DBGHELP:-/usr/lib/wine/x86_64-windows/dbghelp.dll}"
WINEDUMP="${WINEDUMP:-$(command -v winedump || true)}"
CC="${CC:-x86_64-w64-mingw32-gcc}"
OBJDUMP="${OBJDUMP:-x86_64-w64-mingw32-objdump}"

# ---- Sanity checks -----------------------------------------------------
if [ -z "$WINEDUMP" ]; then
    echo "ERROR: winedump not found on PATH. Set WINEDUMP=<path> or install wine." >&2
    exit 1
fi
if [ ! -f "$REF_DBGHELP" ]; then
    echo "ERROR: reference dbghelp not found at $REF_DBGHELP" >&2
    exit 1
fi
if ! command -v "$CC" >/dev/null 2>&1; then
    echo "ERROR: cross-compiler $CC not found. Install mingw-w64." >&2
    exit 1
fi

# ---- Step 1: regenerate stubs from the reference dbghelp ---------------
echo "[build] generating stubs from $REF_DBGHELP ..."
python3 "$SCRIPT_DIR/tools/gen_stubs.py" \
    --winedump "$WINEDUMP" \
    --reference "$REF_DBGHELP" \
    --out-dir "$SCRIPT_DIR/src"

# ---- Step 2: compile ---------------------------------------------------
mkdir -p "$SCRIPT_DIR/build"

echo "[build] compiling with $CC ..."
# Note: do NOT use -nostdlib here. We need the mingw CRT for:
#   - DllMainCRTStartup (the entry point that calls our DllMain)
#   - snprintf (used in dllmain.c for log-line formatting)
# Linking against msvcrt (default) keeps the DLL small and avoids pulling
# in any static runtime that would enlarge the binary materially.
"$CC" -shared \
    -O2 -g -Wall -Wextra \
    -o "$SCRIPT_DIR/build/dbghelp.dll" \
    "$SCRIPT_DIR/src/dllmain.c" \
    "$SCRIPT_DIR/src/stubs.c" \
    "$SCRIPT_DIR/src/dbghelp.def" \
    -lkernel32 -luser32

# ---- Step 3: verify ----------------------------------------------------
echo "[build] verifying output..."
FILE_FMT="$("$OBJDUMP" -f "$SCRIPT_DIR/build/dbghelp.dll" | awk '/file format/ {print $NF; exit}')"
if [ "$FILE_FMT" != "pei-x86-64" ] && [ "$FILE_FMT" != "pe-x86-64" ]; then
    echo "ERROR: build output is $FILE_FMT, expected pei-x86-64" >&2
    exit 1
fi
echo "[build] OK: $SCRIPT_DIR/build/dbghelp.dll ($FILE_FMT)"

# Export count from the PE export directory's "Name Pointer/Ordinal Table"
# entry (number of named exports). Compare against winedump's count of the
# reference DLL.
EXPORT_COUNT_HEX="$("$OBJDUMP" -p "$SCRIPT_DIR/build/dbghelp.dll" \
    | grep -E 'Name Pointer/Ordinal' | grep -oE '[0-9a-f]+$')"
EXPORT_COUNT="$((16#$EXPORT_COUNT_HEX))"
REF_COUNT="$("$WINEDUMP" -j export "$REF_DBGHELP" 2>/dev/null \
    | awk '/# of Names:/ {print $4; exit}')"
echo "[build] exports: built=$EXPORT_COUNT reference=$REF_COUNT"
if [ "$EXPORT_COUNT" != "$REF_COUNT" ]; then
    echo "[build] WARNING: export count differs from reference" >&2
fi

echo "[build] done."
