#!/bin/sh
# Modificus Curator Linux AppImage uninstaller.
#
# Removes the AppImage distribution and its owned integration/update state from
# this Linux user account. Must not be run as root; no processes are killed
# (close Curator before running). All paths are user-space and absolute.
#
# Two modes:
#   (default)         Uninstall the AppImage and its owned integration/update
#                     state while preserving shared user data (profiles, mods,
#                     config, app-state, logs) and the standalone Linux
#                     distribution (app/, relay/) and its command link.
#   --purge-data      Full clean Linux uninstall: also remove the entire Curator
#                     Linux data root, including profiles, mods, config,
#                     app-state, logs, AppImage payload, AppImage NXM managed
#                     files, and any standalone app/ + relay/ payload.
#   --help            Print usage and exit without changing anything.
#
# Testing overrides (env vars, not needed for normal use):
#   INSTALL_ROOT=<dir>          the Curator data root (default XDG/Modificus Curator)
#   BIN_LINK=<path>             the launcher symlink (default ~/.local/bin/modificus-curator)
#   VELOPACK_STATE_DIR=<dir>    the app-specific Velopack state dir (default
#                               /var/tmp/velopack/ModifAmorphic.ModificusCurator;
#                               final component must be exactly ModifAmorphic.ModificusCurator)
#   HOME / XDG_DATA_HOME        select the default user-data tree
set -u

# Stable installed filenames (mirror install.sh + the nxm registrar).
APPIMAGE_FILENAME="Modificus.Curator.AppImage"
DESKTOP_FILENAME="modificus-curator.desktop"
NXM_DESKTOP_FILENAME="modificus-curator-nxm-handler.desktop"
ICON_NAME="modificus-curator"
MANAGED_DIR_NAME="nxm-handler"
MANAGED_HANDLER_NAME="Modificus.Curator.NxmHandler"
MANAGED_SYMLINK_NAME="Modificus.Curator"
VELOPACK_APP_ID="ModifAmorphic.ModificusCurator"
EXPECTED_ROOT_BASE="Modificus Curator"

msg() { printf '%s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# Counted across the whole run. Any owned-item removal failure makes the final
# exit nonzero so a caller can tell a partial cleanup from a clean one. Absent
# paths do not count (idempotent re-runs stay successful).
failures=0

print_help() {
    cat <<'HELP'
Usage: uninstall.sh [--purge-data | --help]

Removes the Modificus Curator AppImage distribution from this Linux user
account. Must not be run as root (sudo); no processes are killed (close Curator
first). All paths are user-space and absolute.

Modes:
  (default)      Uninstall the AppImage and its owned integration/update state
                 while preserving shared user data and the standalone Linux
                 distribution.
  --purge-data   Full clean Linux uninstall: also remove the entire Curator
                 Linux data root (profiles, mods, config, app-state, logs,
                 AppImage payload, AppImage NXM managed files, and any
                 standalone app/ + relay/ payload).
  --help         Print this help and exit without changing anything.

Default mode removes:
  - The installed AppImage, then the appimage/ dir only if empty afterward.
  - The AppImage-owned main desktop entry and application icon.
  - The shared command link only when it is a symlink whose immediate target
    equals the exact installed AppImage path.
  - The AppImage NXM desktop entry only when it contains the exact full Exec
    line the registrar emits for the managed nxm-handler copy; plus the exact
    managed handler copy and its sibling AppImage symlink (the managed dir is
    removed only if empty).
  - The app-specific Velopack update state (cached/pending packages, lock,
    beta id, temp patch state). Removed in both modes so a pending local
    target cannot advance a freshly installed base.

Default mode preserves byte-for-byte:
  - profiles/, mods/, config.json (including AppUpdates.SourceOverride),
    app-state.json, and logs/.
  - The standalone Linux distribution (app/, relay/) and its command link.
  - A regular file or standalone/unrelated command link at the bin path.
  - A standalone NXM desktop entry whose Exec points into app/ or elsewhere.
  - Unexpected files left inside appimage/ or nxm-handler/.

After a default uninstall, local AppUpdates.SourceOverride is preserved and
must be cleared in config.json before testing production update sources.

--purge-data additionally:
  - Removes the shared command link when its target is the AppImage path or
    anything under the Curator install root (covers the standalone link too).
  - Removes the exact Curator NXM desktop entry whether it points at the
    AppImage-managed or the standalone handler. It does not invoke xdg-mime
    or claim another association.
  - Recursively deletes the entire Curator install root after strict basename
    validation. This removes ALL Curator Linux user data and BOTH Linux
    distributions under that shared root. The user-level applications/ and
    icon hierarchies are never removed, only the exact desktop/icon files.

Exit status is nonzero if any owned item could not be removed (see warnings).
Absent items are not errors, so re-running on an already-uninstalled tree
succeeds.

Environment overrides (mainly for testing):
  INSTALL_ROOT, BIN_LINK, VELOPACK_STATE_DIR. HOME and XDG_DATA_HOME select
  the default user-data tree. All override values must be absolute paths.
HELP
}

# --- argument parsing (strict: no repeats, no conflicts, no unknowns) ------
seen_help=0
seen_purge=0
for arg in "$@"; do
    case "$arg" in
        --help) seen_help=$((seen_help + 1)) ;;
        --purge-data) seen_purge=$((seen_purge + 1)) ;;
        *) die "Unknown argument: $arg" ;;
    esac
done
if [ "$seen_help" -gt 1 ] || [ "$seen_purge" -gt 1 ]; then
    die "Repeated arguments are not allowed."
fi
if [ "$seen_help" -ge 1 ] && [ "$seen_purge" -ge 1 ]; then
    die "Conflicting arguments: --help and --purge-data cannot be combined."
fi

if [ "$seen_help" -ge 1 ]; then
    print_help
    exit 0
fi

if [ "$seen_purge" -ge 1 ]; then
    mode="purge"
else
    mode="default"
fi

# --- reject root before any path resolution or mutation --------------------
# --help already exited above, so it works unchanged when run as root. The
# uid lookup goes through PATH so tests can inject a fake `id` without weakening
# production; no special env hook is honored by the uninstaller itself.
if [ "$(id -u 2>/dev/null)" = "0" ]; then
    die "Do not run with sudo/root. This script only touches user-space paths."
fi

# --- resolve paths ----------------------------------------------------------
HOME="${HOME:-}"
[ -n "$HOME" ] || die "HOME is not set; cannot determine the uninstall scope."
xdg_data="${XDG_DATA_HOME:-$HOME/.local/share}"
INSTALL_ROOT="${INSTALL_ROOT:-$xdg_data/$EXPECTED_ROOT_BASE}"
BIN_LINK="${BIN_LINK:-$HOME/.local/bin/modificus-curator}"
VELOPACK_STATE_DIR="${VELOPACK_STATE_DIR:-/var/tmp/velopack/$VELOPACK_APP_ID}"

appimage_dir="$INSTALL_ROOT/appimage"
appimage_path="$appimage_dir/$APPIMAGE_FILENAME"
apps_dir="$xdg_data/applications"
icon_apps_dir="$xdg_data/icons/hicolor/256x256/apps"
desktop_path="$apps_dir/$DESKTOP_FILENAME"
nxm_desktop_path="$apps_dir/$NXM_DESKTOP_FILENAME"
icon_path="$icon_apps_dir/$ICON_NAME.png"
managed_dir="$INSTALL_ROOT/$MANAGED_DIR_NAME"
managed_handler_path="$managed_dir/$MANAGED_HANDLER_NAME"
managed_symlink_path="$managed_dir/$MANAGED_SYMLINK_NAME"

# --- validate BEFORE any filesystem mutation --------------------------------
# Reject empty, root (/), or relative paths. Absolute-path validation also
# ensures a malformed value can never reach the destructive utilities (which
# additionally use an explicit `--` option terminator).
require_safe_abs_path() {
    _v="$1"; _name="$2"
    case "$_v" in
        ''|'/') die "Refusing unsafe $_name: '$_v'." ;;
        /*) ;;
        *) die "$_name must be an absolute path (got '$_v')." ;;
    esac
}
require_safe_abs_path "$INSTALL_ROOT" INSTALL_ROOT
require_safe_abs_path "$BIN_LINK" BIN_LINK
require_safe_abs_path "$VELOPACK_STATE_DIR" VELOPACK_STATE_DIR

velo_base=$(basename "$VELOPACK_STATE_DIR")
if [ "$velo_base" != "$VELOPACK_APP_ID" ]; then
    die "Refusing Velopack cleanup: VELOPACK_STATE_DIR final component must be '$VELOPACK_APP_ID', got '$velo_base'."
fi
if [ "$mode" = "purge" ]; then
    root_base=$(basename "$INSTALL_ROOT")
    if [ "$root_base" != "$EXPECTED_ROOT_BASE" ]; then
        die "Refusing purge: INSTALL_ROOT final component must be '$EXPECTED_ROOT_BASE', got '$root_base'."
    fi
fi

# The exact AppImage-managed NXM desktop Exec line. Default-mode ownership is
# matched against this verbatim (fixed-line, not a substring). Mirrors
# LinuxNxmHandlerRegistrar.FormatExec: Exec="<handler path>" %u.
expected_nxm_exec_line='Exec="'"$managed_handler_path"'" %u'

# --- status ----------------------------------------------------------------
msg "Close Curator before continuing. This script will not kill any process."
msg "Mode:        $mode"
msg "Install root: $INSTALL_ROOT"
msg "Bin link:     $BIN_LINK"
msg "Velopack:     $VELOPACK_STATE_DIR"
msg ""

# --- helpers ---------------------------------------------------------------
# Remove a single owned file or symlink if present. Never follows a symlink
# target: `rm -f --` on a symlink unlinks the directory entry only. A removal
# failure is recorded so the run exits nonzero; an absent path is not an error.
remove_owned_file() {
    _p="$1"
    if [ -L "$_p" ] || [ -e "$_p" ]; then
        if rm -f -- "$_p" 2>/dev/null; then
            msg "Removed: $_p"
        else
            warn "Could not remove (left in place): $_p"
            failures=$((failures + 1))
        fi
    else
        msg "Absent: $_p"
    fi
}

# Remove a directory only when empty. Never recursive: an unexpected entry
# keeps the directory in place rather than risk deleting something unowned.
# A non-empty (or otherwise non-removable) dir is reported as preserved and is
# NOT counted as a failure (conditional cleanup, not an owned item).
rmdir_if_empty() {
    _d="$1"
    if [ -d "$_d" ]; then
        if rmdir -- "$_d" 2>/dev/null; then
            msg "Removed empty directory: $_d"
        else
            msg "Preserved (not empty): $_d"
        fi
    else
        msg "Absent: $_d"
    fi
}

# Remove the app-specific Velopack state. The caller already validated the
# basename; this never touches the parent /var/tmp/velopack.
remove_velopack_state() {
    if [ -d "$VELOPACK_STATE_DIR" ] || [ -L "$VELOPACK_STATE_DIR" ]; then
        if rm -rf -- "$VELOPACK_STATE_DIR" 2>/dev/null; then
            msg "Removed: $VELOPACK_STATE_DIR (Velopack update state)"
        else
            warn "Could not remove Velopack state (left in place): $VELOPACK_STATE_DIR"
            failures=$((failures + 1))
        fi
    else
        msg "Absent: $VELOPACK_STATE_DIR (Velopack update state)"
    fi
}

# --- common external integration cleanup (both modes) ----------------------
msg "== External integration =="
remove_owned_file "$desktop_path"
remove_owned_file "$icon_path"
remove_velopack_state

# --- mode-specific cleanup --------------------------------------------------
if [ "$mode" = "default" ]; then
    msg ""
    msg "== AppImage payload =="
    remove_owned_file "$appimage_path"
    rmdir_if_empty "$appimage_dir"

    msg ""
    msg "== Command link =="
    if [ -L "$BIN_LINK" ]; then
        bin_target=$(readlink "$BIN_LINK")
        if [ "$bin_target" = "$appimage_path" ]; then
            if rm -f -- "$BIN_LINK" 2>/dev/null; then
                msg "Removed: $BIN_LINK (symlink to AppImage)"
            else
                warn "Could not remove (left in place): $BIN_LINK"
                failures=$((failures + 1))
            fi
        else
            msg "Preserved: $BIN_LINK (symlink target is not the AppImage: $bin_target)"
        fi
    elif [ -e "$BIN_LINK" ]; then
        msg "Preserved: $BIN_LINK (regular file, not a symlink)"
    else
        msg "Absent: $BIN_LINK"
    fi

    msg ""
    msg "== AppImage NXM handler integration =="
    # Desktop: remove only when it contains the exact full Exec line the
    # AppImage registrar emits. Fixed-line ownership (-Fx), never a substring,
    # so a standalone-managed, near-match, or unrelated entry whose content
    # merely mentions the managed handler path is preserved.
    if [ -f "$nxm_desktop_path" ]; then
        if grep -Fxq -- "$expected_nxm_exec_line" "$nxm_desktop_path" 2>/dev/null; then
            if rm -f -- "$nxm_desktop_path" 2>/dev/null; then
                msg "Removed: $nxm_desktop_path (AppImage-managed)"
            else
                warn "Could not remove (left in place): $nxm_desktop_path"
                failures=$((failures + 1))
            fi
        else
            msg "Preserved: $nxm_desktop_path (Exec does not match the AppImage-managed handler)"
        fi
    else
        msg "Absent: $nxm_desktop_path"
    fi
    # Exact managed handler copy + sibling AppImage symlink. The symlink target
    # is never followed or deleted; a dangling symlink is removed correctly.
    remove_owned_file "$managed_handler_path"
    remove_owned_file "$managed_symlink_path"
    rmdir_if_empty "$managed_dir"

    msg ""
    msg "Reminder: local AppUpdates.SourceOverride is preserved. Clear it in"
    msg "config.json before testing production update sources."
else
    # --purge-data
    msg ""
    msg "== Command link =="
    if [ -L "$BIN_LINK" ]; then
        bin_target=$(readlink "$BIN_LINK")
        case "$bin_target" in
            "$INSTALL_ROOT"|"$INSTALL_ROOT"/*)
                if rm -f -- "$BIN_LINK" 2>/dev/null; then
                    msg "Removed: $BIN_LINK (symlink into install root: $bin_target)"
                else
                    warn "Could not remove (left in place): $BIN_LINK"
                    failures=$((failures + 1))
                fi
                ;;
            *)
                msg "Preserved: $BIN_LINK (symlink target outside install root: $bin_target)"
                ;;
        esac
    elif [ -e "$BIN_LINK" ]; then
        msg "Preserved: $BIN_LINK (regular file, not a symlink)"
    else
        msg "Absent: $BIN_LINK"
    fi

    msg ""
    msg "== NXM handler desktop =="
    # Clean uninstall removes the exact Curator NXM desktop entry whether it
    # points at the AppImage-managed or the standalone handler. Does not invoke
    # xdg-mime or claim another association. Only the file is removed; the
    # shared applications/ directory is never touched.
    remove_owned_file "$nxm_desktop_path"

    msg ""
    msg "== Curator install root =="
    if [ -d "$INSTALL_ROOT" ] || [ -L "$INSTALL_ROOT" ]; then
        if rm -rf -- "$INSTALL_ROOT" 2>/dev/null; then
            msg "Removed: $INSTALL_ROOT (entire Curator data root)"
        else
            warn "Could not remove install root (partial state may remain): $INSTALL_ROOT"
            failures=$((failures + 1))
        fi
    else
        msg "Absent: $INSTALL_ROOT"
    fi
    # Shared XDG directories (applications/, the icon hierarchy) are never
    # removed, even when now empty; only the exact desktop/icon files above.

    if [ "$failures" -eq 0 ]; then
        msg ""
        msg "Modificus Curator was fully uninstalled from this Linux user account."
        msg "All profiles, mods, config, logs, app state, and both Linux"
        msg "distributions under $INSTALL_ROOT have been removed."
    else
        msg ""
        warn "Uninstall completed with $failures error(s); see warnings above."
    fi
fi

if [ "$failures" -gt 0 ]; then
    exit 1
fi
exit 0
