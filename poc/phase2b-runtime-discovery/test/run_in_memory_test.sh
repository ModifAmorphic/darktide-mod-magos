#!/usr/bin/env bash
# run_in_memory_test.sh — Phase 2b Tier A1: in-memory engine correctness.
#
# Builds in_memory_host.exe (mingw, links the engine + capstone statically),
# runs it under Wine against the live Darktide.exe via LoadLibraryEx, and
# verifies the 7 baked-in addresses reproduce via the in-memory path.
#
# This is the strong gate: it proves the engine works against a real
# loader-mapped image (not just our manual RVA-map), which is exactly the
# path the DLL's worker thread uses in the live game.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
P2B_DIR="$SCRIPT_DIR/.."
P2A_DIR="$SCRIPT_DIR/../../phase2-runtime-discovery"
ENGINE_DIR="$P2A_DIR/engine"
VENDOR="$P2A_DIR/vendor/capstone"
BUILD="$SCRIPT_DIR/build"

CC="${CC:-x86_64-w64-mingw32-gcc}"
WINE="${WINE:-$(command -v wine || true)}"
BINARY="${BINARY:-/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe}"

if [ -z "$WINE" ]; then
    echo "ERROR: wine not found. Set WINE=<path>." >&2; exit 1
fi
if [ ! -f "$BINARY" ]; then
    echo "ERROR: $BINARY not found. Set BINARY=<path>." >&2; exit 1
fi

# Convert the Linux path to a Wine path (Z: drive).
# shellcheck disable=SC2001
WINE_PATH="$(echo "$BINARY" | sed 's|^/|Z:\\|g; s|/|\\|g')"

CAPSTONE_LIB="$VENDOR/build-mingw/libcapstone.a"
if [ ! -f "$CAPSTONE_LIB" ]; then
    echo "[A1] building capstone (mingw) via Phase 2b build.sh..."
    ( cd "$P2B_DIR" && bash build.sh > /dev/null )
fi

mkdir -p "$BUILD"
rm -f "$BUILD/in_memory_host.exe"

echo "[A1] compiling in_memory_host.exe (mingw)..."
ENGINE_SRCS=(
    "$ENGINE_DIR/pe.c" "$ENGINE_DIR/scan.c" "$ENGINE_DIR/disasm.c"
    "$ENGINE_DIR/classify.c" "$ENGINE_DIR/lua_pcall.c" "$ENGINE_DIR/discover.c"
    "$ENGINE_DIR/json.c" "$ENGINE_DIR/report.c" "$ENGINE_DIR/sha256.c"
    "$ENGINE_DIR/anchors.c"
)
CFLAGS="-std=c11 -O2 -g -Wall -Wextra -Wno-unused-function \
        -Wno-unused-but-set-variable -Wno-format-truncation \
        -I$ENGINE_DIR -I$VENDOR/include"
"$CC" $CFLAGS \
    "$SCRIPT_DIR/in_memory_host.c" \
    "${ENGINE_SRCS[@]}" \
    "$CAPSTONE_LIB" \
    -static-libgcc -lkernel32 \
    -o "$BUILD/in_memory_host.exe"

echo "[A1] running under $WINE against $WINE_PATH ..."
WINEDEBUG=-all "$WINE" "$BUILD/in_memory_host.exe" "$WINE_PATH"
RC=$?
echo "[A1] in_memory_host.exe exit code: $RC"

if [ "$RC" -ne 0 ]; then
    echo "[A1] FAIL: in_memory_host did not exit 0" >&2
    exit 1
fi

echo "[A1] PASS: in-memory discovery reproduces all 7 addresses."
