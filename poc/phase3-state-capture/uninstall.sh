#!/usr/bin/env bash
# uninstall.sh — restore the game's original dbghelp.dll from backup.
#
# Safe undo for install.sh. Only restores from the .orig backup; does NOT
# overwrite an arbitrary dbghelp.dll. Same discipline as Phase 1/2b.
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
    echo "no backup, verify game files via Steam to restore the original." >&2
    exit 1
fi

cp -f "$BACKUP" "$ORIG"
echo "[uninstall] restored original -> $ORIG"
rm -f "$BACKUP"
echo "[uninstall] removed backup    -> $BACKUP"

# Optionally clean up POC outputs. Conservative: leave them unless --clean.
if [ "${1:-}" = "--clean" ]; then
    for f in darktide-poc.log darktide-poc-discovery.json; do
        if [ -f "$GAME_DIR/$f" ]; then
            rm -f "$GAME_DIR/$f"
            echo "[uninstall] removed $f (--clean)"
        fi
    done
fi

echo ""
echo "Done. Also remove the Steam launch option:"
echo "  WINEDLLOVERRIDES=\"dbghelp=native,builtin\" %command%"
