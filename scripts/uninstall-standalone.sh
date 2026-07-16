#!/bin/sh
# Modificus Curator Linux standalone tarball uninstaller.
#
# Removes the standalone distribution (app/ + relay/) and its owned integration
# from this Linux user account. Must not be run as root; no processes are
# killed (close Curator before running). All paths are user-space and absolute.
#
# Two modes:
#   (default)         Uninstall the standalone app/ + relay/ payload and the
#                     standalone-owned command link + NXM desktop entry while
#                     preserving shared user data (profiles, mods, config,
#                     app-state, logs) and the AppImage distribution
#                     (appimage/, nxm-handler/, AppImage desktop/icon, and
#                     app-specific Velopack state).
#   --purge-data      Full clean Linux uninstall: remove the exact main Curator
#                     desktop/icon files, the app-specific Velopack state, the
#                     command link when it points into the install root, the
#                     Curator NXM desktop entry regardless of its target, and
#                     the entire Curator Linux data root (both distributions
#                     plus profiles, mods, config, app-state, logs).
#   --help            Print usage and exit without changing anything.
#
# The purge semantics mirror scripts/uninstall.sh --purge-data so
# either uninstaller performs a complete Linux removal. This script is
# intentionally self-contained (no sourced or downloaded helper) so a raw-piped
# invocation stays standalone and network-independent.
#
# Testing overrides (env vars, not needed for normal use):
#   INSTALL_ROOT=<dir>          the Curator data root (default XDG/Modificus Curator)
#   BIN_LINK=<path>             the launcher symlink (default ~/.local/bin/modificus-curator)
#   VELOPACK_STATE_DIR=<dir>    the app-specific Velopack state dir (default
#                               /var/tmp/velopack/ModifAmorphic.ModificusCurator;
#                               final component must be exactly ModifAmorphic.ModificusCurator)
#   HOME / XDG_DATA_HOME        select the default user-data tree
set -u

# Stable installed filenames (mirror the installers + the nxm registrar). The
# standalone UI + handler land under app/; the AppImage-owned main desktop/icon
# + the nxm-handler managed dir are only touched in purge mode.
STANDALONE_UI_NAME="Modificus.Curator"
STANDALONE_HANDLER_NAME="Modificus.Curator.NxmHandler"
DESKTOP_FILENAME="modificus-curator.desktop"
NXM_DESKTOP_FILENAME="modificus-curator-nxm-handler.desktop"
ICON_NAME="modificus-curator"
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
Usage: uninstall-standalone.sh [--purge-data | --help]

Removes the Modificus Curator standalone tarball distribution from this Linux
user account. Must not be run as root (sudo); no processes are killed (close
Curator first). All paths are user-space and absolute.

Modes:
  (default)      Uninstall the standalone app/ + relay/ payload and the
                 standalone-owned command link + NXM desktop entry while
                 preserving shared user data and the AppImage distribution.
  --purge-data   Full clean Linux uninstall: remove the exact main Curator
                 desktop/icon files, the app-specific Velopack state, the
                 command link when it points into the install root, the Curator
                 NXM desktop entry regardless of its target, and the entire
                 Curator Linux data root (both distributions plus all shared
                 user data).
  --help         Print this help and exit without changing anything.

Default mode removes:
  - The standalone app/ and relay/ directories (recursively) under the install
    root. These are the standalone installer's owned payload.
  - The shared command link only when it is a symlink whose immediate readlink
    value is exactly <install root>/app/Modificus.Curator.
  - The Curator NXM desktop entry only when it contains the exact full line
    Exec="<install root>/app/Modificus.Curator.NxmHandler" %u (fixed-line
    ownership, not a substring match).

Default mode preserves byte-for-byte:
  - profiles/, mods/, config.json (including AppUpdates.SourceOverride),
    app-state.json, and logs/.
  - The AppImage distribution: appimage/, nxm-handler/ (managed handler copy +
    sibling symlink), the AppImage-owned main desktop entry and application
    icon, and the app-specific Velopack update state.
  - A regular file or AppImage-target/unrelated-target command link at the bin
    path (only the exact standalone-target symlink is removed).
  - An AppImage-managed or unrelated NXM desktop entry whose Exec does not match
    the exact standalone-handler line.
  - Shared parent directories (the install root, the user applications/ and icon
    hierarchies) are never removed.

After a default uninstall, local AppUpdates.SourceOverride is preserved and
must be cleared in config.json before testing production update sources.

--purge-data additionally:
  - Removes the exact main Curator desktop entry and application icon
    (AppImage-owned integration) and the app-specific Velopack state.
  - Removes the shared command link when its target is the install root itself
    or anything under it (covers both the standalone and AppImage links).
  - Removes the exact Curator NXM desktop entry whether it points at the
    standalone or the AppImage-managed handler. It does not invoke xdg-mime
    or claim another association.
  - Recursively deletes the entire Curator install root after strict basename
    validation. This removes ALL Curator Linux user data and BOTH Linux
    distributions under that shared root. The user-level applications/ and
    icon hierarchies are never removed, only the exact desktop/icon files.

The purge behavior matches scripts/uninstall.sh --purge-data, so one
uninstaller command is sufficient for a complete Linux removal.

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

standalone_ui_path="$INSTALL_ROOT/app/$STANDALONE_UI_NAME"
standalone_handler_path="$INSTALL_ROOT/app/$STANDALONE_HANDLER_NAME"
apps_dir="$xdg_data/applications"
icon_apps_dir="$xdg_data/icons/hicolor/256x256/apps"
desktop_path="$apps_dir/$DESKTOP_FILENAME"
nxm_desktop_path="$apps_dir/$NXM_DESKTOP_FILENAME"
icon_path="$icon_apps_dir/$ICON_NAME.png"

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

# The exact standalone-target command link + the exact standalone NXM desktop
# Exec line. Default-mode ownership is matched against these verbatim.
expected_bin_target="$standalone_ui_path"
# Build the exact Exec line the standalone registrar emits:
# Exec="<handler path>" %u  (see LinuxNxmHandlerRegistrar.FormatExec).
expected_nxm_exec_line='Exec="'"$standalone_handler_path"'" %u'

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

# Remove an owned directory tree (the standalone payload). Never follows a
# symlink target: rm -rf on a path that is itself a symlink removes the link,
# not its target; on a real directory it removes the directory and its
# contents. A removal failure is recorded; an absent path is not an error.
remove_owned_tree() {
    _d="$1"
    if [ -d "$_d" ] || [ -L "$_d" ]; then
        if rm -rf -- "$_d" 2>/dev/null; then
            msg "Removed: $_d"
        else
            warn "Could not remove (left in place): $_d"
            failures=$((failures + 1))
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

if [ "$mode" = "default" ]; then
    # --- default mode: standalone payload + standalone-owned integration ---
    msg "== Standalone payload =="
    remove_owned_tree "$INSTALL_ROOT/app"
    remove_owned_tree "$INSTALL_ROOT/relay"

    msg ""
    msg "== Command link =="
    # Remove only the exact standalone-target symlink. AppImage-target,
    # unrelated-target, dangling-unrelated, and regular-file entries are
    # preserved; the symlink target itself is never followed or deleted.
    if [ -L "$BIN_LINK" ]; then
        bin_target=$(readlink "$BIN_LINK")
        if [ "$bin_target" = "$expected_bin_target" ]; then
            if rm -f -- "$BIN_LINK" 2>/dev/null; then
                msg "Removed: $BIN_LINK (symlink to standalone UI)"
            else
                warn "Could not remove (left in place): $BIN_LINK"
                failures=$((failures + 1))
            fi
        else
            msg "Preserved: $BIN_LINK (symlink target is not the standalone UI: $bin_target)"
        fi
    elif [ -e "$BIN_LINK" ]; then
        msg "Preserved: $BIN_LINK (regular file, not a symlink)"
    else
        msg "Absent: $BIN_LINK"
    fi

    msg ""
    msg "== Standalone NXM handler desktop =="
    # Remove the NXM desktop entry only when it contains the exact full Exec
    # line the standalone registrar emits. Fixed-line ownership (-Fx), never a
    # substring, so an AppImage-managed or near-match entry is preserved.
    if [ -f "$nxm_desktop_path" ]; then
        if grep -Fxq -- "$expected_nxm_exec_line" "$nxm_desktop_path" 2>/dev/null; then
            if rm -f -- "$nxm_desktop_path" 2>/dev/null; then
                msg "Removed: $nxm_desktop_path (standalone-managed)"
            else
                warn "Could not remove (left in place): $nxm_desktop_path"
                failures=$((failures + 1))
            fi
        else
            msg "Preserved: $nxm_desktop_path (Exec does not match the standalone handler)"
        fi
    else
        msg "Absent: $nxm_desktop_path"
    fi

    msg ""
    msg "Preserved: AppImage distribution (appimage/, nxm-handler/, AppImage"
    msg "desktop/icon), Velopack update state, and shared user data (profiles,"
    msg "mods, config, app-state, logs)."
    msg ""
    msg "Reminder: local AppUpdates.SourceOverride is preserved. Clear it in"
    msg "config.json before testing production update sources."
else
    # --- purge mode: complete Linux removal (mirrors uninstall.sh) ---
    msg "== External integration =="
    remove_owned_file "$desktop_path"
    remove_owned_file "$icon_path"
    remove_velopack_state

    msg ""
    msg "== Command link =="
    # Remove when the symlink target is the install root itself or anything
    # under it (covers both the standalone and AppImage links). External
    # targets and regular files are preserved; the target is never followed.
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
    # points at the standalone or the AppImage-managed handler. Does not invoke
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
