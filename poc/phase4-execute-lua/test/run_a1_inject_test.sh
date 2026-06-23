#!/usr/bin/env bash
# run_a1_inject_test.sh — Phase 4 Tier A1: mock-VM injection test.
#
# Compiles inject.c with -DPHASE4_TEST_API against the system LuaJIT,
# then runs the resulting Linux-native binary. Asserts all of:
#   - Darktide.exe @ RVA 0xc744c0 matches lua_pcall's source-compiled
#     shape (NEW offline check — the disasm_check link against the
#     vendored capstone static lib). This catches a wrong RVA before
#     the live game does.
#   - load_rc == 0 (luaL_loadbuffer succeeded)
#   - pcall_rc == 0 (lua_pcall executed our chunk)
#   - inject_count == 1 (one-shot)
#   - detour_depth == 2 (reentry guard caught the recursion)
#   - mock print file contains "[INJECTED] Hello from the DLL"
#   - the io.open file write happened (the deterministic observable)
#
# This is the STRONG GATE. It proves the loadbuffer+pcall+reentry-guard
# logic works against a real LuaJIT before we risk the live game, AND
# that the baked-in lua_pcall RVA is correct in the game binary. The
# inject.c source is the EXACT SAME FILE the production DLL links;
# only the harness differs (Linux native vs MinHook + DllMain).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PHASE4_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
P2A_DIR="$(cd "$PHASE4_DIR/../phase2-runtime-discovery" && pwd)"
P2B_DIR="$(cd "$PHASE4_DIR/../phase2b-runtime-discovery" && pwd)"
P2B_SRC="$P2B_DIR/src"
CAPSTONE_DIR="$P2A_DIR/vendor/capstone"
WORK="$SCRIPT_DIR/build"

CC="${CC:-gcc}"

# LuaJIT discovery — prefer pkg-config, fall back to known paths.
LUAJIT_CFLAGS="$(pkg-config luajit --cflags 2>/dev/null || true)"
LUAJIT_LIBS="$(pkg-config luajit --libs 2>/dev/null || true)"
if [ -z "$LUAJIT_CFLAGS" ]; then
    # Fall back to the standard Debian/Ubuntu path.
    if [ -d /usr/include/luajit-2.1 ]; then
        LUAJIT_CFLAGS="-I/usr/include/luajit-2.1"
        LUAJIT_LIBS="-lluajit-5.1"
    else
        echo "ERROR: LuaJIT headers not found (no pkg-config, no /usr/include/luajit-2.1)." >&2
        echo "       Install libluajit-5.1-dev (Debian/Ubuntu) or equivalent." >&2
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
    echo "       CFLAGS: $LUAJIT_CFLAGS" >&2
    echo "       LIBS:   $LUAJIT_LIBS" >&2
    exit 1
fi

# Build capstone (native Linux static lib) if not already built.
# This is the same vendored capstone the Phase 2a discovery engine uses.
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
# Note: -DPHASE4_INJECT_DELAY_MS=0 makes the threshold check pass
# immediately (the mock VM has all globals registered before we trigger
# the detour, so no waiting is needed). Production uses the default 3000.
# -std=gnu11 (not c11) for clock_gettime / CLOCK_MONOTONIC in inject.c's
# Linux portability shim.
"$CC" -std=gnu11 -O2 -g -Wall -Wextra -Wno-unused \
      -DPHASE4_TEST_API \
      -DPHASE4_INJECT_DELAY_MS=0 \
      -I"$PHASE4_DIR/src" \
      -I"$SCRIPT_DIR" \
      -I"$CAPSTONE_DIR/include" \
      -I"$P2B_SRC" \
      $LUAJIT_CFLAGS \
      "$PHASE4_DIR/src/inject.c" \
      "$SCRIPT_DIR/a1_inject_test.c" \
      "$SCRIPT_DIR/disasm_check.c" \
      $LUAJIT_LIBS \
      "$CAPSTONE_LIB" \
      -o "$WORK/a1_inject_test"

echo "[a1] running $WORK/a1_inject_test ..."
# Run in a temp dir so the io.open write lands somewhere we own and clean up.
# DARKTIDE_EXE may be set to point at the binary; default is the dev-box path.
RUN_DIR="$WORK/run"
mkdir -p "$RUN_DIR"
rm -f "$RUN_DIR/darktide-poc-executed.txt" "$RUN_DIR/a1_mock_print_output.txt"

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
echo "[a1] PASS: Lua execution via inject.c proven against real LuaJIT"
echo "[a1] PASS: lua_pcall RVA verified offline in Darktide.exe"
