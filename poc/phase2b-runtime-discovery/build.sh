#!/usr/bin/env bash
# build.sh — Phase 2b mingw cross-compile of the dbghelp proxy DLL with
# the in-process discovery engine linked in.
#
# Reuses (does NOT duplicate):
#   - Phase 1's tools/gen_stubs.py   (regenerates stubs.c/.h/.def from the
#                                     reference dbghelp — same 200 exports)
#   - Phase 2a's engine/*.c          (the discovery engine, with the
#                                     Phase 2b should-fixes applied in place)
#   - Phase 2a's vendor/capstone/    (capstone 5.0.3; built here with mingw)
#
# Produces build/dbghelp.dll (PE-x86-64): Phase 1 forwarding + Phase 2b
# worker-thread discovery.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
P2A_DIR="$SCRIPT_DIR/../phase2-runtime-discovery"
P1_DIR="$SCRIPT_DIR/../phase1-proxy-dll"
ENGINE_DIR="$P2A_DIR/engine"
VENDOR="$P2A_DIR/vendor/capstone"
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
    echo "ERROR: reference dbghelp not found at $REF_DBGHELP" >&2; exit 1
fi

# ---- Flags ------------------------------------------------------------
# -Wno-format-truncation: snprintf into fixed buffers with gcc's conservative
#   bound analysis (the engine truncates safely; inputs are bounded).
CFLAGS="-std=c11 -O2 -g -Wall -Wextra \
        -Wno-unused-function -Wno-unused-but-set-variable \
        -Wno-format-truncation \
        -I$ENGINE_DIR -I$VENDOR/include"
# -static-libgcc: link the gcc support lib statically so the DLL doesn't
#   depend on libgcc_s_seh-1.dll at runtime.
# -lkernel32: GetModuleHandleW, CreateFileW, CreateThread, GetSystemTime,
#   InitializeCriticalSection, etc. (the only Win32 API the DLL needs.)
# -lmsvcrt / ucrt: the mingw default CRT (snprintf, fopen, calloc, malloc,
#   free, _wfopen). User32 is NOT linked: the worker uses no user32 APIs.
LDFLAGS="-static-libgcc -lkernel32"

mkdir -p "$BUILD"

# ---- 1. Regenerate stubs from the reference dbghelp -------------------
echo "[build] generating stubs from $REF_DBGHELP ..."
python3 "$P1_DIR/tools/gen_stubs.py" \
    --winedump "$WINEDUMP" \
    --reference "$REF_DBGHELP" \
    --out-dir "$SRC"

# ---- 2. Build capstone with mingw (once) ------------------------------
CAPSTONE_BUILD="$VENDOR/build-mingw"
CAPSTONE_LIB="$CAPSTONE_BUILD/libcapstone.a"
if [[ ! -f "$CAPSTONE_LIB" ]]; then
    echo "[build] configuring capstone (mingw cross)"
    # Capstone's project() enables C and CXX (no language arg). Pass both
    # compilers so cmake's CXX compiler test passes; capstone is pure C so
    # CXX is never actually used for any TU.
    cmake -S "$VENDOR" -B "$CAPSTONE_BUILD" \
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

# ---- 3. Compile engine sources (from Phase 2a, unmodified) ------------
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

# ---- 4. Compile Phase 2b DLL sources ----------------------------------
echo "[build] compiling Phase 2b DLL sources (mingw)"
DLL_OBJS=(
    "$BUILD/dllmain.o"
    "$BUILD/poc_log.o"
    "$BUILD/discover_worker.o"
    "$BUILD/stubs.o"
)
$CC $CFLAGS -c "$SRC/dllmain.c"         -o "$BUILD/dllmain.o"
$CC $CFLAGS -c "$SRC/poc_log.c"         -o "$BUILD/poc_log.o"
$CC $CFLAGS -c "$SRC/discover_worker.c" -o "$BUILD/discover_worker.o"
$CC $CFLAGS -c "$SRC/stubs.c"           -o "$BUILD/stubs.o"

# ---- 5. Link -----------------------------------------------------------
echo "[build] linking dbghelp.dll"
$CC -shared \
    -o "$BUILD/dbghelp.dll" \
    "${DLL_OBJS[@]}" \
    "${ENGINE_OBJS[@]}" \
    "$SRC/dbghelp.def" \
    "$CAPSTONE_LIB" \
    $LDFLAGS

# ---- 6. Verify ---------------------------------------------------------
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
