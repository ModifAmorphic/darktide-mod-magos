#!/bin/sh
# Modificus Curator Linux AppImage installer.
#
# Installs the latest Velopack-generated Linux x64 AppImage into Curator's
# default app-data root under a stable filename, marks it executable, adds a
# launcher symlink in ~/.local/bin, and installs a user desktop entry plus
# application icon. Served from raw/main:
#
#   curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install-appimage.sh | sh
#
# By default the script installs the latest STABLE AppImage. Pass --prerelease
# (or set CURATOR_PRERELEASE=1) to install the latest prerelease instead. The
# asset URL is resolved from scripts/release.env (manifest keys
# APPIMAGE_RELEASE_URL / APPIMAGE_PRE_RELEASE_URL), which the release pipeline
# maintains on every release. The script never queries the GitHub API and never
# infers the filename.
#
# The standalone tarball installer (scripts/install.sh) is unaffected and may
# coexist. The most recently run installer repoints the shared
# ~/.local/bin/modificus-curator convenience symlink to its own executable; NXM
# (nexus mod) link registration remains Curator's explicit Integrations action.
#
# Testing overrides (env vars, not needed for normal use):
#   INSTALL_ROOT=<dir>         install into this dir instead of the default root
#   BIN_LINK=<path>            create the launcher symlink here
#   CURATOR_REPO=<owner/repo>  read the manifest from this repo
#   CURATOR_APPIMAGE=<file>    use a local AppImage instead of downloading
#   CURATOR_PRERELEASE=1       install the latest prerelease
set -eu

REPO="${CURATOR_REPO:-ModifAmorphic/darktide-modificus-curator}"
UA="modificus-curator-installer"
# Stable installed filename. Velopack's generated filename carries the channel
# and is renamed to this stable name so desktop/Steam shortcuts keep one path
# across updates.
APPIMAGE_FILENAME="Modificus.Curator.AppImage"
DESKTOP_FILENAME="modificus-curator.desktop"
ICON_NAME="modificus-curator"
# Velopack 1.2.0 generated root names inside the AppImage squashfs root.
PACK_DESKTOP="ModifAmorphic.ModificusCurator.desktop"
PACK_ICON="ModifAmorphic.ModificusCurator.png"
PACK_DIRICON=".DirIcon"

msg() { printf '%s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# Stable by default; prerelease is opt-in via --prerelease or CURATOR_PRERELEASE=1.
prerelease=0
if [ "${CURATOR_PRERELEASE:-0}" = "1" ]; then
    prerelease=1
fi
for arg in "$@"; do
    case "$arg" in
        --prerelease) prerelease=1 ;;
        *) die "Unknown argument: $arg" ;;
    esac
done

# Resolve HOME before anything else; the install location is derived from it.
HOME="${HOME:-}"
[ -n "$HOME" ] || { echo "ERROR: HOME is not set; cannot determine install location." >&2; exit 1; }
xdg_data="${XDG_DATA_HOME:-$HOME/.local/share}"
INSTALL_ROOT="${INSTALL_ROOT:-$xdg_data/Modificus Curator}"
BIN_LINK="${BIN_LINK:-$HOME/.local/bin/modificus-curator}"

# Reject unsafe install roots BEFORE any filesystem mutation. An empty or "/"
# root would let a botched cleanup delete the wrong tree.
case "$INSTALL_ROOT" in
    ''|'/') die "Refusing to install into unsafe INSTALL_ROOT: '$INSTALL_ROOT'." ;;
esac
case "$BIN_LINK" in
    ''|'/') die "Refusing to use unsafe BIN_LINK: '$BIN_LINK'." ;;
esac

# Validate a local override before any temp or install tree is touched, so a
# missing override fails with zero side effects.
if [ -n "${CURATOR_APPIMAGE:-}" ]; then
    [ -f "$CURATOR_APPIMAGE" ] || die "CURATOR_APPIMAGE does not exist: $CURATOR_APPIMAGE"
fi

# Pick whichever HTTP client is available.
if command -v curl >/dev/null 2>&1; then
    fetch() { curl -fsSL -A "$UA" "$1"; }
    fetch_to() { curl -fsSL -A "$UA" -o "$2" "$1"; }
elif command -v wget >/dev/null 2>&1; then
    fetch() { wget -q -U "$UA" -O- "$1"; }
    fetch_to() { wget -q -U "$UA" -O "$2" "$1"; }
else
    die "Neither curl nor wget found. Install one and re-run."
fi

# Isolated work directory for AppImage extraction (may live on another
# filesystem; only the final rename must share a filesystem with the target).
work_dir=$(mktemp -d 2>/dev/null || mktemp -d -t curator-appimage) \
    || die "Could not create a temp work directory."
extract_dir="$work_dir/extract"
mkdir -p "$extract_dir" || die "Could not create extraction directory."
candidate=""
cleanup() {
    if [ -n "${candidate:-}" ]; then rm -f "$candidate" 2>/dev/null || :; fi
    if [ -n "${work_dir:-}" ]; then rm -rf "$work_dir" 2>/dev/null || :; fi
}
trap cleanup EXIT

# Ensure the destination directory exists so the candidate lands on the same
# filesystem as the final AppImage (required for the atomic rename).
appimage_dir="$INSTALL_ROOT/appimage"
mkdir -p "$appimage_dir" || die "Could not create install directory: $appimage_dir"
candidate="$appimage_dir/.curator-appimage.tmp.$$"
appimage_path="$appimage_dir/$APPIMAGE_FILENAME"

# Stage the candidate on the destination filesystem.
if [ -n "${CURATOR_APPIMAGE:-}" ]; then
    msg "Using local AppImage: $CURATOR_APPIMAGE (testing override)"
    cp "$CURATOR_APPIMAGE" "$candidate" || die "Could not stage local AppImage."
    tag="local AppImage"
else
    if [ "$prerelease" = "1" ]; then
        channel="prerelease"
    else
        channel="stable"
    fi
    msg "Resolving the latest $channel AppImage release of $REPO ..."
    MANIFEST_URL="https://raw.githubusercontent.com/${REPO}/main/scripts/release.env"
    manifest=$(fetch "$MANIFEST_URL") \
        || die "Could not fetch the release manifest. If this persists, check your connection or CURATOR_REPO."
    # Parse the manifest as data (never source it). Only the two expected keys
    # are honored; unknown lines are ignored.
    if [ "$prerelease" = "1" ]; then
        asset_url=$(printf '%s\n' "$manifest" | sed -n 's/^APPIMAGE_PRE_RELEASE_URL=\(.*\)$/\1/p')
        [ -n "$asset_url" ] || die "No AppImage prerelease is currently published."
    else
        asset_url=$(printf '%s\n' "$manifest" | sed -n 's/^APPIMAGE_RELEASE_URL=\(.*\)$/\1/p')
        [ -n "$asset_url" ] || die "No stable Modificus Curator AppImage release is available yet. Re-run with --prerelease to install the latest prerelease."
    fi
    asset_name=$(basename "$asset_url")
    tag=$(printf '%s\n' "$asset_url" | sed -n 's#.*/download/\([^/]*\)/.*#\1#p')
    [ -n "$tag" ] || tag="latest"
    msg "Asset:         $asset_name"
    msg "Downloading $asset_url ..."
    fetch_to "$asset_url" "$candidate" || die "Download failed for $asset_url."
fi

# Executable mode before validation: the candidate is executed for extraction.
chmod +x "$candidate" || die "Could not mark candidate AppImage executable."

# Structural validation without FUSE: extract the candidate into an isolated
# work dir using the Type 2 AppImage --appimage-extract mode and require the
# Velopack 1.2.0 generated layout. A candidate that cannot extract or lacks
# required structure must fail BEFORE the prior AppImage is touched.
msg "Validating AppImage structure ..."
if (cd "$extract_dir" && "$candidate" --appimage-extract) >/dev/null 2>&1; then
    :
else
    die "AppImage extraction failed; the previous installation was left unchanged."
fi
root="$extract_dir/squashfs-root"
missing=""
for req in \
    "$PACK_DESKTOP" \
    "$PACK_ICON" \
    "$PACK_DIRICON" \
    "usr/bin/Modificus.Curator" \
    "usr/bin/Modificus.Curator.NxmHandler" \
    "usr/bin/relay/modificus_relay.exe" \
    "usr/bin/UpdateNix" \
    "usr/bin/sq.version"; do
    [ -e "$root/$req" ] || missing="$missing $req"
done
if [ -n "$missing" ]; then
    die "AppImage is missing required structure:$missing"
fi

# Atomically rename the validated candidate over the stable path. Both paths
# share the install directory, so mv performs a rename(2) that replaces the
# destination in one step. A failure here leaves the prior AppImage usable.
if ! mv -f "$candidate" "$appimage_path" 2>/dev/null; then
    die "Could not install AppImage to $appimage_path."
fi
msg "Installed: $appimage_path"

# Desktop entry + application icon. Owned by this installer and replaced on
# re-run. Written under the user XDG hierarchy; no root/system directories.
apps_dir="$xdg_data/applications"
icon_dir="$xdg_data/icons/hicolor/256x256/apps"
desktop_path="$apps_dir/$DESKTOP_FILENAME"
icon_path="$icon_dir/$ICON_NAME.png"

if mkdir -p "$apps_dir" "$icon_dir" 2>/dev/null; then
    # Escape the stable path for the desktop Exec field: wrap in double quotes
    # and backslash-escape embedded backslashes and quotes. Spaces are handled
    # by the quoting.
    exec_escaped=$(printf '%s' "$appimage_path" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g')
    cat > "$desktop_path" <<EOF
[Desktop Entry]
Type=Application
Name=Modificus Curator
Comment=Mod manager for Warhammer 40,000: Darktide
Exec="$exec_escaped" %U
Icon=$ICON_NAME
Terminal=false
Categories=Game;Utility;
StartupWMClass=ModifAmorphic.ModificusCurator
EOF
    msg "Desktop:   $desktop_path"
    if cp "$root/$PACK_ICON" "$icon_path" 2>/dev/null; then
        msg "Icon:      $icon_path"
    else
        warn "Could not install application icon to $icon_path."
    fi
else
    warn "Could not create desktop integration directories under $xdg_data."
    warn "The AppImage is installed; desktop integration was skipped."
fi

# Convenience symlink. Repoints the shared launcher to this AppImage; the
# standalone installer may repoint it back later. Report the target clearly.
bin_dir=$(dirname "$BIN_LINK")
if ! mkdir -p "$bin_dir" 2>/dev/null; then
    warn "Could not create bin directory: $bin_dir"
fi
if ln -sf "$appimage_path" "$BIN_LINK" 2>/dev/null; then
    msg "Symlinked: $BIN_LINK -> $appimage_path"
else
    warn "Could not create symlink $BIN_LINK (directory missing or not writable)."
    warn "Run Curator directly: $appimage_path"
fi

msg ""
msg "Modificus Curator AppImage $tag installed."
msg "  AppImage:  $appimage_path"
msg "  Launcher:  $BIN_LINK"
msg ""
msg "Next steps:"
msg "  - Make sure ~/.local/bin is on your PATH (or run the AppImage directly)."
msg "  - Start Curator: modificus-curator"
msg "  - NXM (nexus mod) link registration is a separate action: open Curator's"
msg "    Integrations dialog to register the nxm:// handler."
