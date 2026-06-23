#!/usr/bin/env bash
# run_a1_hook_test.sh — Phase 3 Tier A1: MinHook hook-mechanism test.
#
# Builds test/a1_hook_test.c + test/fake_thunk.S against vendored MinHook,
# runs under Wine, and asserts PASS. This is the meaningful Tier A test:
# it proves (a) MinHook initializes under mingw/Wine, (b) hooks install on
# both a normal function and a CFG-thunk-shaped function, (c) the detour
# fires, captures the original's return value, and passes it through to
# the caller unchanged.
#
# CFG-thunk shape (5-byte jmp + int3 padding) is what the live target at
# RVA 0xc7c000 actually looks like, so passing this test on the thunk
# target is the strongest pre-Tier-B evidence the live hook will take.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PHASE3_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
VENDOR="$PHASE3_DIR/vendor/minhook"
WORK="$SCRIPT_DIR/build"
MINHOOK_LIB="$PHASE3_DIR/build/minhook.a"
WINE="${WINE:-$(command -v wine || true)}"
CC="${CC:-x86_64-w64-mingw32-gcc}"

if [ -z "$WINE" ]; then
    echo "ERROR: wine not found. Set WINE=<path>." >&2; exit 1
fi

if [ ! -f "$MINHOOK_LIB" ]; then
    echo "[a1] building MinHook (one-time)..."
    ( cd "$PHASE3_DIR" && ./build.sh --minhook-only )
fi

mkdir -p "$WORK"
rm -f "$WORK/a1_hook_test.exe"

echo "[a1] compiling a1_hook_test.c + fake_thunk.S (mingw)..."
"$CC" -std=c11 -O2 -g -Wall -Wextra \
      -I"$VENDOR/include" \
      "$SCRIPT_DIR/a1_hook_test.c" "$SCRIPT_DIR/fake_thunk.S" \
      "$MINHOOK_LIB" \
      -lkernel32 -static-libgcc \
      -o "$WORK/a1_hook_test.exe"

echo "[a1] running under $WINE ..."
set +e
WINEDEBUG=-all "$WINE" "$WORK/a1_hook_test.exe"
RC=$?
set -e

echo "[a1] exit code: $RC"
if [ "$RC" -ne 0 ]; then
    echo "[a1] FAIL: a1_hook_test.exe did not exit 0" >&2
    exit 1
fi

echo
echo "[a1] PASS: MinHook hook+capture+passthrough proven for plain + CFG-thunk shapes"
