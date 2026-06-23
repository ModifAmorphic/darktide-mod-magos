#!/usr/bin/env bash
# run_disasm.sh — Phase 3: build & run the lua_pcall-candidate disassembler.
#
# Compiles tool/disasm_candidates.c against the vendored capstone (Phase 2a
# vendor — same one the engine uses), runs it under Wine against the live
# Darktide.exe, and prints each candidate's first ~24 instructions plus
# an arg-count / 4-arg-detour-safety summary. Output feeds the report's
# "candidate arg-count evidence" section.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PHASE3_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
P2A_DIR="$(cd "$PHASE3_DIR/../phase2-runtime-discovery" && pwd)"
VENDOR="$P2A_DIR/vendor/capstone"
WORK="$SCRIPT_DIR/build"
WINE="${WINE:-$(command -v wine || true)}"
CC="${CC:-x86_64-w64-mingw32-gcc}"

if [ -z "$WINE" ]; then
    echo "ERROR: wine not found. Set WINE=<path>." >&2; exit 1
fi

CAPSTONE_LIB="$VENDOR/build-mingw/libcapstone.a"
if [[ ! -f "$CAPSTONE_LIB" ]]; then
    # Phase 2b's build produces build-mingw; fall back to Phase 2a's native build.
    CAPSTONE_LIB="$VENDOR/build/libcapstone.a"
fi

mkdir -p "$WORK"
rm -f "$WORK/disasm_candidates.exe"

echo "[disasm] building disasm_candidates.exe (mingw)..."
"$CC" -std=c11 -O2 -g -Wall -Wextra -Wno-unused-function -Wno-unused-but-set-variable \
      -I"$VENDOR/include" \
      "$SCRIPT_DIR/disasm_candidates.c" \
      "$CAPSTONE_LIB" \
      -lkernel32 -static-libgcc \
      -o "$WORK/disasm_candidates.exe"

echo "[disasm] running under $WINE ..."
WINEDEBUG=-all "$WINE" "$WORK/disasm_candidates.exe"
echo "[disasm] done"
