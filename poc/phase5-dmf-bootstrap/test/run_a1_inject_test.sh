#!/usr/bin/env bash
# run_a1_inject_test.sh — Phase 5 Tier A1: DMF bootstrap test.
#
# Compiles inject.c with -DPHASE4_TEST_API against the system LuaJIT,
# then runs the resulting Linux-native binary. Asserts all of:
#   - 8 offline disasm checks (4 from Phase 4: lua_pcall, luaL_openlibs,
#     lua_pushcclosure, lua_setfield; 4 new for Phase 5: lua_tolstring,
#     lua_createtable, lua_type, lua_tonumber).
#   - Phase 1: poc_print regression (open-libs VM).
#   - Phase 2: DMF bootstrap builds the Mods table correctly (no-libs VM).
#   - Phase 3: c_dofile reads + executes a real Lua file.
#   - Phase 4: negative control (DMF bootstrap disabled) — chunk fails +
#     retries.
#   - Phase 5: dmf_loader.lua loads via c_dofile from the live game
#     install (the POC goal; soft-skipped if the game isn't installed).
#
# This is the STRONG GATE. It proves the DMF bootstrap logic works against
# a real LuaJIT before we risk the live game, AND that the baked-in RVAs
# are correct in the game binary.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PHASE5_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
P2A_DIR="$(cd "$PHASE5_DIR/../phase2-runtime-discovery" && pwd)"
P2B_DIR="$(cd "$PHASE5_DIR/../phase2b-runtime-discovery" && pwd)"
P2B_SRC="$P2B_DIR/src"
CAPSTONE_DIR="$P2A_DIR/vendor/capstone"
WORK="$SCRIPT_DIR/build"

CC="${CC:-gcc}"

# LuaJIT discovery — prefer pkg-config, fall back to known paths.
LUAJIT_CFLAGS="$(pkg-config luajit --cflags 2>/dev/null || true)"
LUAJIT_LIBS="$(pkg-config luajit --libs 2>/dev/null || true)"
if [ -z "$LUAJIT_CFLAGS" ]; then
    if [ -d /usr/include/luajit-2.1 ]; then
        LUAJIT_CFLAGS="-I/usr/include/luajit-2.1"
        LUAJIT_LIBS="-lluajit-5.1"
    else
        echo "ERROR: LuaJIT headers not found." >&2
        exit 1
    fi
fi

if ! command -v "$CC" >/dev/null 2>&1; then
    echo "ERROR: native C compiler $CC not found." >&2
    exit 1
fi

# Sanity: LuaJIT must be linkable.
if ! printf '#include <lauxlib.h>\n#include <lua.h>\nint main(void){lua_State* L=luaL_newstate(); if(!L) return 1; lua_close(L); return 0;}\n' \
        | "$CC" -x c - $LUAJIT_CFLAGS $LUAJIT_LIBS -o /dev/null 2>/dev/null; then
    echo "ERROR: cannot compile/link a trivial LuaJIT program." >&2
    exit 1
fi

# Build capstone (native Linux static lib) if not already built.
CAPSTONE_LIB="$CAPSTONE_DIR/build/libcapstone.a"
if [[ ! -f "$CAPSTONE_LIB" ]]; then
    echo "[a1] configuring capstone (native Linux static)"
    cmake -S "$CAPSTONE_DIR" -B "$CAPSTONE_DIR/build" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCAPSTONE_ARCHITECTURE_DEFAULT=OFF \
        -DCAPSTONE_X86_SUPPORT=ON \
        -DCAPSTONE_BUILD_SHARED=OFF \
        -DCAPSTONE_BUILD_TESTS=OFF \
        -DCAPSTONE_BUILD_CSTOOL=OFF \
        > /dev/null
fi
echo "[a1] building capstone (native Linux static)"
cmake --build "$CAPSTONE_DIR/build" -j "$(nproc)" > /dev/null

mkdir -p "$WORK"
rm -f "$WORK/a1_inject_test"

echo "[a1] compiling inject.c with -DPHASE4_TEST_API + a1_inject_test.c + disasm_check.c ..."
"$CC" -std=gnu11 -O2 -g -Wall -Wextra -Wno-unused \
      -DPHASE4_TEST_API \
      -DPHASE4_INJECT_DELAY_MS=0 \
      -I"$PHASE5_DIR/src" \
      -I"$SCRIPT_DIR" \
      -I"$CAPSTONE_DIR/include" \
      -I"$P2B_SRC" \
      $LUAJIT_CFLAGS \
      "$PHASE5_DIR/src/inject.c" \
      "$SCRIPT_DIR/a1_inject_test.c" \
      "$SCRIPT_DIR/disasm_check.c" \
      $LUAJIT_LIBS \
      "$CAPSTONE_LIB" \
      -o "$WORK/a1_inject_test"

echo "[a1] running $WORK/a1_inject_test ..."
RUN_DIR="$WORK/run"
mkdir -p "$RUN_DIR"
rm -rf "$RUN_DIR"/*

set +e
( cd "$RUN_DIR" && "../a1_inject_test" )
RC=$?
set -e

echo "[a1] exit code: $RC"
if [ "$RC" -ne 0 ]; then
    echo "[a1] FAIL: a1_inject_test did not exit 0" >&2
    exit 1
fi

echo
echo "[a1] PASS: Phase 5 DMF bootstrap proven against real LuaJIT"
echo "[a1] PASS: All 8 Phase 4+5 RVAs verified offline in Darktide.exe"
