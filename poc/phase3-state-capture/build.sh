#!/usr/bin/env bash
# build.sh — Phase 3 mingw cross-compile of the dbghelp proxy DLL with
# the in-process discovery engine AND the Phase 3 hooks (lua_newstate +
# 3 lua_pcall candidates) linked in.
#
# Composes (does NOT duplicate) sources from prior phases:
#   - Phase 1's tools/gen_stubs.py   (regenerates stubs.c/.h/.def from the
#                                     reference dbghelp — same 200 exports)
#   - Phase 2a's engine/*.c          (the discovery engine, with the
#                                     Phase 2b should-fixes applied in place)
#   - Phase 2a's vendor/capstone/    (capstone 5.0.3; built here with mingw)
#   - Phase 2b's src/discover_worker.c, src/poc_log.c, src/poc_log.h,
#                                     src/expected_addrs.h
#                                     (the worker thread + logger; unchanged)
#   - Phase 3's vendor/minhook/      (MinHook 1.3.3, x64; built here)
#   - Phase 3's src/                 (dllmain.c extended for Phase 3,
#                                     phase3_hooks.{c,h})
#
# Produces build/dbghelp.dll (PE-x86-64): Phase 1 forwarding + Phase 2b
# worker-thread discovery + Phase 3 lua_newstate capture + lua_pcall
# resolution.
#
# Flags:
#   --minhook-only    Build only build/minhook.a and exit (used by the A1
#                     test script when no DLL is needed yet).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
P2A_DIR="$SCRIPT_DIR/../phase2-runtime-discovery"
P2B_DIR="$SCRIPT_DIR/../phase2b-runtime-discovery"
P1_DIR="$SCRIPT_DIR/../phase1-proxy-dll"
ENGINE_DIR="$P2A_DIR/engine"
CAPSTONE_DIR="$P2A_DIR/vendor/capstone"
P2B_SRC="$P2B_DIR/src"
MH_DIR="$SCRIPT_DIR/vendor/minhook"
BUILD="$SCRIPT_DIR/build"
SRC="$SCRIPT_DIR/src"

MINHOOK_ONLY=0
if [[ "${1:-}" == "--minhook-only" ]]; then MINHOOK_ONLY=1; fi

# ---- Toolchain --------------------------------------------------------
CC="${CC:-x86_64-w64-mingw32-gcc}"
OBJDUMP="${OBJDUMP:-x86_64-w64-mingw32-objdump}"
WINEDUMP="${WINEDUMP:-$(command -v winedump || true)}"
REF_DBGHELP="${REF_DBGHELP:-/usr/lib/wine/x86_64-windows/dbghelp.dll}"

if ! command -v "$CC" >/dev/null 2>&1; then
    echo "ERROR: cross-compiler $CC not found. Install mingw-w64." >&2
    exit 1
fi
if [ "$MINHOOK_ONLY" -eq 0 ] && [ -z "$WINEDUMP" ]; then
    echo "ERROR: winedump not found. Install wine." >&2; exit 1
fi
if [ "$MINHOOK_ONLY" -eq 0 ] && [ ! -f "$REF_DBGHELP" ]; then
    echo "ERROR: reference dbghelp not found at $REF_DBGHELP" >&2; exit 1
fi

# ---- Flags ------------------------------------------------------------
# -Wno-format-truncation: snprintf into fixed buffers with gcc's conservative
#   bound analysis (the engine truncates safely; inputs are bounded).
CFLAGS="-std=c11 -O2 -g -Wall -Wextra \
        -Wno-unused-function -Wno-unused-but-set-variable \
        -Wno-format-truncation \
        -I$ENGINE_DIR -I$CAPSTONE_DIR/include \
        -I$MH_DIR/include -I$MH_DIR/src -I$MH_DIR/src/hde \
        -I$SRC -I$P2B_SRC"
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

if [[ $MINHOOK_ONLY -eq 1 ]]; then
    echo "[build] --minhook-only: stopping after MinHook."
    exit 0
fi

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
    # Capstone's project() enables C and CXX (no language arg). Pass both
    # compilers so cmake's CXX compiler test passes; capstone is pure C so
    # CXX is never actually used for any TU.
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

# ---- 4. Compile engine sources (from Phase 2a, unmodified) ------------
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

# ---- 5. Compile Phase 3 DLL sources ----------------------------------
echo "[build] compiling Phase 3 DLL sources (mingw)"
DLL_OBJS=(
    "$BUILD/dllmain.o"
    "$BUILD/phase3_hooks.o"
    "$BUILD/poc_log.o"
    "$BUILD/discover_worker.o"
    "$BUILD/stubs.o"
)
$CC $CFLAGS -c "$SRC/dllmain.c"            -o "$BUILD/dllmain.o"
$CC $CFLAGS -c "$SRC/phase3_hooks.c"       -o "$BUILD/phase3_hooks.o"
# poc_log.c and discover_worker.c come from Phase 2b unchanged (composed).
$CC $CFLAGS -c "$P2B_SRC/poc_log.c"        -o "$BUILD/poc_log.o"
$CC $CFLAGS -c "$P2B_SRC/discover_worker.c" -o "$BUILD/discover_worker.o"
$CC $CFLAGS -c "$SRC/stubs.c"              -o "$BUILD/stubs.o"

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
