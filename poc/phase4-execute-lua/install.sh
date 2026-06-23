#!/usr/bin/env bash
# install.sh — place the Phase 4 dbghelp.dll into the Darktide binaries dir.
#
# Same backup discipline as Phase 1/2b/3: back up the game's original
# dbghelp.dll to dbghelp.dll.orig BEFORE overwriting, and refuse to clobber
# an existing backup (run uninstall.sh first to restore). Also cleans stale
# log/json/output so captures come from this run only.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

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

# Do NOT clobber an existing backup.
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

# Sanity: confirm the proxy is PE32+ x86-64 before touching the game dir.
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
    exit 1
fi

# Detect a stale prior-phase proxy: warn (don't fail).
if [ -f "$GAME_DIR/darktide-poc.log" ]; then
    if grep -q 'discover summary' "$GAME_DIR/darktide-poc.log" 2>/dev/null && \
       ! grep -q 'inject targets:\|Phase 4 inject' "$GAME_DIR/darktide-poc.log" 2>/dev/null; then
        echo "NOTE: $GAME_DIR/darktide-poc.log looks like a pre-Phase-4 run" >&2
        echo "      (no Phase 4 inject markers). If a prior-phase proxy is still" >&2
        echo "      installed at $ORIG, uninstall it first." >&2
        echo "" >&2
    fi
fi

# ---- Perform the install ----------------------------------------------
cp -p "$ORIG" "$BACKUP"
echo "[install] backed up original -> $BACKUP"

cp -f "$PROXY" "$ORIG"
echo "[install] installed proxy   -> $ORIG"

# Clean up stale outputs from a prior run (including Phase 4's exec marker).
for stale in darktide-poc.log darktide-poc-discovery.json darktide-poc-executed.txt; do
    if [ -f "$GAME_DIR/$stale" ]; then
        rm -f "$GAME_DIR/$stale"
        echo "[install] removed stale $stale"
    fi
done

echo ""
echo "Done. Set this Steam launch option for Darktide:"
echo "  WINEDLLOVERRIDES=\"dbghelp=native,builtin\" %command%"
echo ""
echo "Then launch the game and watch the log:"
echo "  tail -f \"$GAME_DIR/darktide-poc.log\""
echo ""
echo "Headline success signals (in order):"
echo "  grep 'captured lua_State*'           \"$GAME_DIR/darktide-poc.log\""
echo "  grep 'lua_gettop(L)'                 \"$GAME_DIR/darktide-poc.log\""
echo "  grep 'inject lua_pcall hook installed'   \"$GAME_DIR/darktide-poc.log\""
echo "  grep 'openlibs called'               \"$GAME_DIR/darktide-poc.log\""
echo "  grep 'injected load_rc=0 pcall_rc=0' \"$GAME_DIR/darktide-poc.log\""
echo ""
echo "The deterministic observable (file write — appears only if pcall_rc=0):"
echo "  find \"$GAME_DIR\" -name 'darktide-poc-executed.txt' -newer \"$ORIG\""
echo ""
echo "Phase 5 Step 2 diagnostic outcome:"
echo "  pcall_rc=0 → openlibs fixed the sandbox (Scenario a)"
echo "  pcall_rc=2 (persistent) → deeper investigation needed (Scenario b/c)"
