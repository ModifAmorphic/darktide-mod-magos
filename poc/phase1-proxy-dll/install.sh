#!/usr/bin/env bash
# install.sh — place the proxy dbghelp.dll into the Darktide binaries dir.
#
# Backs up the game's original dbghelp.dll to dbghelp.dll.orig BEFORE
# overwriting. Refuses to clobber an existing backup (which would lose
# the only known-good original).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Game directory can be overridden for non-standard installs.
GAME_DIR="${GAME_DIR:-/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries}"

PROXY="$SCRIPT_DIR/build/dbghelp.dll"
ORIG="$GAME_DIR/dbghelp.dll"
BACKUP="$GAME_DIR/dbghelp.dll.orig"

# ---- Pre-flight checks ------------------------------------------------
if [ ! -f "$PROXY" ]; then
    echo "ERROR: $PROXY does not exist. Run ./build.sh first." >&2
    exit 1
fi

if [ ! -d "$GAME_DIR" ]; then
    echo "ERROR: game directory not found: $GAME_DIR" >&2
    echo "Set GAME_DIR=<path> if your install is elsewhere." >&2
    exit 1
fi

if [ ! -f "$ORIG" ]; then
    echo "ERROR: $ORIG not found. Is the game installed here?" >&2
    exit 1
fi

# Do NOT clobber an existing backup. The user must explicitly run
# uninstall.sh to restore the original before re-installing.
if [ -e "$BACKUP" ]; then
    echo "ERROR: backup already exists at $BACKUP" >&2
    echo "Refusing to overwrite a previous good backup." >&2
    echo "" >&2
    echo "To proceed:" >&2
    echo "  1. Run ./uninstall.sh to restore the original dbghelp.dll," >&2
    echo "  2. Verify the game still launches," >&2
    echo "  3. Re-run ./install.sh." >&2
    exit 2
fi

# Sanity: confirm the proxy is the right architecture (PE x86-64).
# If the user built it wrong (32-bit, etc.), bail before touching the
# game directory. Prefer mingw's objdump; fall back to `file` if mingw
# tools aren't on PATH (e.g. user copied a pre-built proxy).
FMT_OK=0
if command -v x86_64-w64-mingw32-objdump >/dev/null 2>&1; then
    FMT="$(x86_64-w64-mingw32-objdump -f "$PROXY" 2>/dev/null \
            | awk '/file format/ {print $NF; exit}')"
    if [ "$FMT" = "pei-x86-64" ] || [ "$FMT" = "pe-x86-64" ]; then
        FMT_OK=1
    fi
elif command -v file >/dev/null 2>&1; then
    if file "$PROXY" | grep -qi 'PE32+.*x86-64'; then
        FMT_OK=1
    fi
fi
if [ "$FMT_OK" -ne 1 ]; then
    echo "ERROR: $PROXY is not a PE32+ x86-64 DLL." >&2
    echo "Rebuild with build.sh on a 64-bit mingw toolchain." >&2
    exit 1
fi

# ---- Perform the install ----------------------------------------------
cp -p "$ORIG" "$BACKUP"
echo "[install] backed up original -> $BACKUP"

cp -f "$PROXY" "$ORIG"
echo "[install] installed proxy   -> $ORIG"

# Clean up any stale log from a previous run so the user can see fresh
# attach lines on next launch.
if [ -f "$GAME_DIR/darktide-poc.log" ]; then
    rm -f "$GAME_DIR/darktide-poc.log"
    echo "[install] removed stale darktide-poc.log"
fi

echo ""
echo "Done. Set this Steam launch option for Darktide:"
echo "  WINEDLLOVERRIDES=\"dbghelp=native\" %command%"
echo ""
echo "Then launch the game and watch the log:"
echo "  tail -f \"$GAME_DIR/darktide-poc.log\""
