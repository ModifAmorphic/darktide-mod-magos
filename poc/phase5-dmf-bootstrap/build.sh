#!/usr/bin/env bash
# build.sh — Phase 5 mingw cross-compile of the dbghelp proxy DLL with
# the in-process discovery engine + Phase 3 lua_newstate capture +
# Phase 5 DMF bootstrap (built on Phase 4 lua_pcall hook).
#
# Composes (does NOT duplicate) sources from prior phases:
#   - Phase 1's tools/gen_stubs.py        (regenerates stubs.c/.h/.def)
#   - Phase 2a's engine/*.c               (discovery engine)
#   - Phase 2a's vendor/capstone/         (capstone 5.0.3 mingw static lib)
#   - Phase 2b's src/discover_worker.c, src/poc_log.{c,h},
#                     src/expected_addrs.h
#   - Phase 3's vendor/minhook/           (MinHook 1.3.3, x64 static)
#   - Phase 3's src/phase3_hooks.{c,h}    (MODIFIED — adds capture-tick
#                                         getter + PHASE3_INCLUDE_PCALL_OBSERVERS
#                                         macro; composed here with observers
#                                         OFF so Phase 4 still owns lua_pcall)
#   - Phase 5's src/ (built on Phase 4)                       (dllmain.c + inject.{c,h})
#
# Produces build/dbghelp.dll (PE-x86-64): Phase 1 forwarding + Phase 2b
# worker-thread discovery + Phase 3 lua_newstate capture (no observers)
# + Phase 5 DMF bootstrap (Phase 4 lua_pcall execution + Mods table setup + dmf_loader loading).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
P3_DIR="$SCRIPT_DIR/../phase3-state-capture"
P2A_DIR="$SCRIPT_DIR/../phase2-runtime-discovery"
P2B_DIR="$SCRIPT_DIR/../phase2b-runtime-discovery"
P1_DIR="$SCRIPT_DIR/../phase1-proxy-dll"
ENGINE_DIR="$P2A_DIR/engine"
CAPSTONE_DIR="$P2A_DIR/vendor/capstone"
P2B_SRC="$P2B_DIR/src"
P3_SRC="$P3_DIR/src"
MH_DIR="$P3_DIR/vendor/minhook"
BUILD="$SCRIPT_DIR/build"
SRC="$SCRIPT_DIR/src"

# ---- Toolchain --------------------------------------------------------
CC="${CC:-x86_64-w64-mingw32-gcc}"
OBJDUMP="${OBJDUMP:-x86_64-w64-mingw32-objdump}"
WINEDUMP="${WINEDUMP:-$(command -v winedump || true)}"
REF_DBGHELP="${REF_DBGHELP:-/usr/lib/wine/x86_64-windows/dbghelp.dll}"

if ! command -v "$CC" >/dev/null 2>&1; then
    echo "ERROR: cross-compiler $CC not found. Install mingw-w64." >&2
    exit 1
fi
if [ -z "$WINEDUMP" ]; then
    echo "ERROR: winedump not found. Install wine." >&2; exit 1
fi
if [ ! -f "$REF_DBGHELP" ]; then
    echo "ERROR: reference dbghelp not found at $REF_DBGHELP" >&2
    exit 1
fi

# ---- Flags ------------------------------------------------------------
# -DPHASE3_INCLUDE_PCALL_OBSERVERS=0: compile Phase 3's hooks WITHOUT the
#   3 lua_pcall-candidate observers (Phase 4 still owns lua_pcall now). This is
#   the key composition switch.
# -Wno-format-truncation: snprintf into fixed buffers with gcc's conservative
#   bound analysis (the engine truncates safely; inputs are bounded).
CFLAGS="-std=c11 -O2 -g -Wall -Wextra \
        -Wno-unused-function -Wno-unused-but-set-variable \
        -Wno-format-truncation \
        -DPHASE3_INCLUDE_PCALL_OBSERVERS=0 \
        -I$ENGINE_DIR -I$CAPSTONE_DIR/include \
        -I$MH_DIR/include -I$MH_DIR/src -I$MH_DIR/src/hde \
        -I$SRC -I$P3_SRC -I$P2B_SRC"
LDFLAGS="-static-libgcc -lkernel32"

mkdir -p "$BUILD"

# ---- 1. Build MinHook (x64 static) ------------------------------------
echo "[build] building MinHook (x64 static, mingw)"
MINHOOK_OBJS=(
    "$BUILD/mh_buffer.o"
    "$BUILD/mh_hook.o"
    "$BUILD/mh_trampoline.o"
    "$BUILD/mh_hde64.o"
)
MINHOOK_LIB="$BUILD/minhook.a"
MINHOOK_SRCS=(
    "$MH_DIR/src/buffer.c"
    "$MH_DIR/src/hook.c"
    "$MH_DIR/src/trampoline.c"
    "$MH_DIR/src/hde/hde64.c"
)
needs_rebuild=0
if [[ ! -f "$MINHOOK_LIB" ]]; then needs_rebuild=1; fi
for i in "${!MINHOOK_SRCS[@]}"; do
    if [[ "${MINHOOK_SRCS[$i]}" -nt "${MINHOOK_OBJS[$i]}" ]]; then needs_rebuild=1; fi
done
if [[ $needs_rebuild -eq 1 ]]; then
    for i in "${!MINHOOK_SRCS[@]}"; do
        $CC $CFLAGS -c "${MINHOOK_SRCS[$i]}" -o "${MINHOOK_OBJS[$i]}"
    done
    ar rcs "$MINHOOK_LIB" "${MINHOOK_OBJS[@]}"
fi
echo "[build]   minhook.a: $(stat -c %s "$MINHOOK_LIB") bytes"

# ---- 2. Regenerate stubs from the reference dbghelp -------------------
echo "[build] generating stubs from $REF_DBGHELP ..."
python3 "$P1_DIR/tools/gen_stubs.py" \
    --winedump "$WINEDUMP" \
    --reference "$REF_DBGHELP" \
    --out-dir "$SRC"

# ---- 3. Build capstone with mingw (once) ------------------------------
CAPSTONE_BUILD="$CAPSTONE_DIR/build-mingw"
CAPSTONE_LIB="$CAPSTONE_BUILD/libcapstone.a"
if [[ ! -f "$CAPSTONE_LIB" ]]; then
    echo "[build] configuring capstone (mingw cross)"
    cmake -S "$CAPSTONE_DIR" -B "$CAPSTONE_BUILD" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_SYSTEM_NAME=Windows \
        -DCMAKE_C_COMPILER="$CC" \
        -DCMAKE_CXX_COMPILER="x86_64-w64-mingw32-g++" \
        -DCAPSTONE_ARCHITECTURE_DEFAULT=OFF \
        -DCAPSTONE_X86_SUPPORT=ON \
        -DCAPSTONE_BUILD_SHARED=OFF \
        -DCAPSTONE_BUILD_TESTS=OFF \
        -DCAPSTONE_BUILD_CSTOOL=OFF \
        > /dev/null
fi
echo "[build] building capstone (x86 static, mingw)"
cmake --build "$CAPSTONE_BUILD" -j "$(nproc)" > /dev/null

# ---- 4. Compile engine sources (Phase 2a, unmodified) -----------------
echo "[build] compiling engine (mingw)"
ENGINE_SRCS=(
    "$ENGINE_DIR/pe.c"
    "$ENGINE_DIR/scan.c"
    "$ENGINE_DIR/disasm.c"
    "$ENGINE_DIR/classify.c"
    "$ENGINE_DIR/lua_pcall.c"
    "$ENGINE_DIR/discover.c"
    "$ENGINE_DIR/json.c"
    "$ENGINE_DIR/report.c"
    "$ENGINE_DIR/sha256.c"
    "$ENGINE_DIR/anchors.c"
)
ENGINE_OBJS=()
for src in "${ENGINE_SRCS[@]}"; do
    obj="$BUILD/$(basename "${src%.c}").o"
    ENGINE_OBJS+=("$obj")
    if [[ "$src" -nt "$obj" ]] || [[ ! -f "$obj" ]]; then
        $CC $CFLAGS -c "$src" -o "$obj"
    fi
done

# ---- 5. Compile Phase 5 DLL sources ----------------------------------
echo "[build] compiling Phase 5 DLL sources (mingw)"
# Phase 3 hooks (composed with observers OFF via CFLAGS).
$CC $CFLAGS -c "$P3_SRC/phase3_hooks.c" -o "$BUILD/phase3_hooks.o"
# Phase 5 own sources (inject.c is the Phase 5 variant).
$CC $CFLAGS -c "$SRC/dllmain.c"            -o "$BUILD/dllmain.o"
$CC $CFLAGS -c "$SRC/inject.c"             -o "$BUILD/inject.o"
# Phase 2b worker + logger (unchanged).
$CC $CFLAGS -c "$P2B_SRC/poc_log.c"        -o "$BUILD/poc_log.o"
$CC $CFLAGS -c "$P2B_SRC/discover_worker.c" -o "$BUILD/discover_worker.o"
# Regenerated stubs.
$CC $CFLAGS -c "$SRC/stubs.c"              -o "$BUILD/stubs.o"

DLL_OBJS=(
    "$BUILD/dllmain.o"
    "$BUILD/phase3_hooks.o"
    "$BUILD/inject.o"
    "$BUILD/poc_log.o"
    "$BUILD/discover_worker.o"
    "$BUILD/stubs.o"
)

# ---- 6. Link -----------------------------------------------------------
echo "[build] linking dbghelp.dll"
$CC -shared \
    -o "$BUILD/dbghelp.dll" \
    "${DLL_OBJS[@]}" \
    "${ENGINE_OBJS[@]}" \
    "$SRC/dbghelp.def" \
    "$CAPSTONE_LIB" \
    "$MINHOOK_LIB" \
    $LDFLAGS

# ---- 7. Verify ---------------------------------------------------------
echo "[build] verifying output..."
FILE_FMT="$("$OBJDUMP" -f "$BUILD/dbghelp.dll" | awk '/file format/ {print $NF; exit}')"
if [ "$FILE_FMT" != "pei-x86-64" ] && [ "$FILE_FMT" != "pe-x86-64" ]; then
    echo "ERROR: build output is $FILE_FMT, expected pei-x86-64" >&2
    exit 1
fi

EXPORT_COUNT_HEX="$("$OBJDUMP" -p "$BUILD/dbghelp.dll" \
    | grep -E 'Name Pointer/Ordinal' | grep -oE '[0-9a-f]+$')"
EXPORT_COUNT="$((16#$EXPORT_COUNT_HEX))"
REF_COUNT="$("$WINEDUMP" -j export "$REF_DBGHELP" 2>/dev/null \
    | awk '/# of Names:/ {print $4; exit}')"
SIZE_KB="$(du -k "$BUILD/dbghelp.dll" | cut -f1)"

echo "[build] OK: $BUILD/dbghelp.dll ($FILE_FMT)"
echo "[build]   exports: built=$EXPORT_COUNT reference=$REF_COUNT"
echo "[build]   size:    ${SIZE_KB} KB"
if [ "$EXPORT_COUNT" != "$REF_COUNT" ]; then
    echo "[build] WARNING: export count differs from reference" >&2
fi
echo "[build] done."
