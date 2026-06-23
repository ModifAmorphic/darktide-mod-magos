#!/usr/bin/env bash
# uninstall.sh — restore the game's original dbghelp.dll from backup.
#
# This is the safe way to undo install.sh. It only restores from the
# .orig backup that install.sh created — it does NOT overwrite an
# arbitrary dbghelp.dll.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

GAME_DIR="${GAME_DIR:-/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries}"
ORIG="$GAME_DIR/dbghelp.dll"
BACKUP="$GAME_DIR/dbghelp.dll.orig"

if [ ! -d "$GAME_DIR" ]; then
    echo "ERROR: game directory not found: $GAME_DIR" >&2
    exit 1
fi

if [ ! -f "$BACKUP" ]; then
    echo "ERROR: no backup at $BACKUP" >&2
    echo "Either install.sh was never run, or it has already been" >&2
    echo "uninstalled. If the proxy is currently installed and you have" >&2
    echo "no backup, you will need to verify game files via Steam to" >&2
    echo "restore the original dbghelp.dll." >&2
    exit 1
fi

# mv is atomic on the same filesystem; if interrupted, the .orig is
# preserved (we copy first, then remove the backup).
cp -f "$BACKUP" "$ORIG"
echo "[uninstall] restored original -> $ORIG"
rm -f "$BACKUP"
echo "[uninstall] removed backup    -> $BACKUP"

# Optionally clean up the log. Keep this conservative — leave it if the
# user might want to inspect it. Remove only if -q / --clean passed.
if [ "${1:-}" = "--clean" ] && [ -f "$GAME_DIR/darktide-poc.log" ]; then
    rm -f "$GAME_DIR/darktide-poc.log"
    echo "[uninstall] removed darktide-poc.log (--clean)"
fi

echo ""
echo "Done. Also remove the Steam launch option:"
echo "  WINEDLLOVERRIDES=\"dbghelp=native\" %command%"
echo "(or replace 'native' with the default, e.g. 'builtin'.)"
