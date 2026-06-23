#!/usr/bin/env bash
# build.sh - native Linux build of the Phase 2a engine + tool + test.
#
# Builds capstone (once), the static engine library, the offline CLI tool,
# and the Tier A test harness. Idempotent: re-running rebuilds what's stale.
#
# Phase 2b will re-compile the SAME engine sources with mingw for the DLL;
# nothing in engine/ uses POSIX or Linux-only headers (file I/O lives in
# tool/discover.c only).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD="$HERE/build"
VENDOR="$HERE/vendor/capstone"
ENGINE="$HERE/engine"
TOOL="$HERE/tool"
TEST="$HERE/test"

CC="${CC:-gcc}"
# -Wno-format-truncation: snprintf into fixed buffers with conservative
#   bound analysis (gcc can't prove the inputs are short enough). The
#   engine truncates safely and the actual data never hits the bound.
CFLAGS="${CFLAGS:--std=c11 -O2 -Wall -Wextra -Wno-unused-function -Wno-unused-but-set-variable -Wno-format-truncation}"
LDFLAGS="${LDFLAGS:-}"

mkdir -p "$BUILD"

# ---- 1. Build capstone (only x86, static) -----------------------------
if [[ ! -f "$VENDOR/build/libcapstone.a" ]]; then
    echo "[build] configuring capstone"
    cmake -S "$VENDOR" -B "$VENDOR/build" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCAPSTONE_ARCHITECTURE_DEFAULT=OFF \
        -DCAPSTONE_X86_SUPPORT=ON \
        -DCAPSTONE_BUILD_SHARED=OFF \
        -DCAPSTONE_BUILD_TESTS=OFF \
        -DCAPSTONE_BUILD_CSTOOL=OFF \
        > /dev/null
fi
echo "[build] building capstone (x86 static lib)"
cmake --build "$VENDOR/build" -j "$(nproc)" > /dev/null

CAPSTONE_HDR="$VENDOR/include"
CAPSTONE_LIB="$VENDOR/build/libcapstone.a"

# ---- 2. Build engine static library -----------------------------------
echo "[build] compiling engine"
ENGINE_SRCS=(
    "$ENGINE/pe.c"
    "$ENGINE/scan.c"
    "$ENGINE/disasm.c"
    "$ENGINE/classify.c"
    "$ENGINE/lua_pcall.c"
    "$ENGINE/discover.c"
    "$ENGINE/json.c"
    "$ENGINE/report.c"
    "$ENGINE/sha256.c"
    "$ENGINE/anchors.c"
)
ENGINE_OBJS=()
for src in "${ENGINE_SRCS[@]}"; do
    obj="$BUILD/$(basename "${src%.c}").o"
    ENGINE_OBJS+=("$obj")
    if [[ "$src" -nt "$obj" ]] || [[ ! -f "$obj" ]]; then
        $CC $CFLAGS -I"$ENGINE" -I"$CAPSTONE_HDR" -c "$src" -o "$obj"
    fi
done
ar rcs "$BUILD/libdarktide_engine.a" "${ENGINE_OBJS[@]}"

# ---- 3. Build the offline CLI tool ------------------------------------
echo "[build] linking tool/discover"
$CC $CFLAGS -I"$ENGINE" -I"$CAPSTONE_HDR" \
    "$TOOL/discover.c" \
    "$BUILD/libdarktide_engine.a" "$CAPSTONE_LIB" \
    -lm $LDFLAGS -o "$BUILD/dt_discover"

# ---- 4. Build the Tier A test harness ---------------------------------
echo "[build] linking test/check_crosscheck"
$CC $CFLAGS -I"$ENGINE" -I"$CAPSTONE_HDR" \
    "$TEST/check_crosscheck.c" \
    "$BUILD/libdarktide_engine.a" "$CAPSTONE_LIB" \
    -lm $LDFLAGS -o "$BUILD/check_crosscheck"

echo "[build] done. artifacts in $BUILD/"
echo "    dt_discover         (offline discovery tool)"
echo "    check_crosscheck    (Tier A test harness)"
