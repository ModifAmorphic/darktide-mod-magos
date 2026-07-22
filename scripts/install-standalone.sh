#!/bin/sh
# Modificus Curator Linux standalone tarball installer.
#
# Installs the latest Linux x64 standalone release into Curator's default
# app-data root, adds a launcher symlink in ~/.local/bin, and prints the next
# steps. Served from raw/main:
#
#   curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install-standalone.sh | sh
#
# By default the script installs the latest STABLE release. Pass --prerelease
# (or set CURATOR_PRERELEASE=1) to install the latest prerelease instead. The
# asset to download is resolved from scripts/release.env, a manifest the
# release pipeline maintains on every release. The script never queries the
# GitHub API and never infers the filename.
#
# Testing overrides (env vars, not needed for normal use):
#   INSTALL_ROOT=<dir>        install into this dir instead of the default root
#   BIN_LINK=<path>           create the launcher symlink here
#   CURATOR_REPO=<owner/repo> read the manifest from this repo
#   CURATOR_ARCHIVE=<file>    use a local tar.gz instead of downloading (used
#                             to exercise extraction against a fake archive)
#   CURATOR_PRERELEASE=1      install the latest prerelease
set -eu

REPO="${CURATOR_REPO:-ModifAmorphic/darktide-modificus-curator}"
UA="modificus-curator-installer"

msg() { printf '%s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# Stable by default; prerelease is opt-in via --prerelease (or CURATOR_PRERELEASE).
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

# Default install root mirrors AppPaths.AppDataDir, which is
# Environment.SpecialFolder.LocalApplicationData + "Modificus Curator" (the
# space is intentional). On Linux that resolves to XDG_DATA_HOME or
# ~/.local/share. The directory also holds profiles/, mods/, logs/, and
# config.json, so only app/ and relay/ are ever replaced.
HOME="${HOME:-}"
[ -n "$HOME" ] || { echo "ERROR: HOME is not set; cannot determine install location." >&2; exit 1; }
INSTALL_ROOT="${INSTALL_ROOT:-${XDG_DATA_HOME:-$HOME/.local/share}/Modificus Curator}"
BIN_LINK="${BIN_LINK:-$HOME/.local/bin/modificus-curator}"

tmp_dir=""
cleanup() { [ -n "$tmp_dir" ] && rm -rf "$tmp_dir"; }
trap cleanup EXIT

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

# Reject an obviously broken install root before we rm anything under it.
case "$INSTALL_ROOT" in
    ''|'/') die "Refusing to install into unsafe INSTALL_ROOT: '$INSTALL_ROOT'." ;;
esac

tmp_dir=$(mktemp -d 2>/dev/null || mktemp -d -t curator-install) \
    || die "Could not create a temp directory."

# Resolve the asset. CURATOR_ARCHIVE (testing override) skips the network
# entirely and installs the local archive. Otherwise read scripts/release.env,
# a manifest the release pipeline maintains, and select the stable or
# prerelease URL from it. The manifest is plain KEY=value text parsed line by
# line (not sourced), so only the two expected keys are honored.
archive="$tmp_dir/curator-archive.tar.gz"
if [ -n "${CURATOR_ARCHIVE:-}" ]; then
    [ -f "$CURATOR_ARCHIVE" ] || die "CURATOR_ARCHIVE does not exist: $CURATOR_ARCHIVE"
    cp "$CURATOR_ARCHIVE" "$archive"
    tag="local archive"
    msg "Using local archive: $CURATOR_ARCHIVE (testing override)"
else
    if [ "$prerelease" = "1" ]; then
        channel="prerelease"
    else
        channel="stable"
    fi
    msg "Resolving the latest $channel release of $REPO ..."
    MANIFEST_URL="https://raw.githubusercontent.com/${REPO}/main/scripts/release.env"
    manifest=$(fetch "$MANIFEST_URL") \
        || die "Could not fetch the release manifest. If this persists, check your connection or CURATOR_REPO."
    if [ "$prerelease" = "1" ]; then
        asset_url=$(printf '%s\n' "$manifest" | sed -n 's/^PRE_RELEASE_URL=\(.*\)$/\1/p')
        [ -n "$asset_url" ] || die "No prerelease is currently published."
    else
        asset_url=$(printf '%s\n' "$manifest" | sed -n 's/^RELEASE_URL=\(.*\)$/\1/p')
        [ -n "$asset_url" ] || die "No stable Modificus Curator release is available yet. Re-run with --prerelease to install the latest prerelease."
    fi
    tag=$(printf '%s\n' "$asset_url" | sed -n 's#.*/download/\([^/]*\)/.*#\1#p')
    [ -n "$tag" ] || tag="latest"
    asset_name=$(basename "$asset_url")
    msg "Latest release: $tag"
    msg "Asset:         $asset_name"
    msg "Downloading $asset_url ..."
    fetch_to "$asset_url" "$archive" || die "Download failed for $asset_url."
fi

# Extract to a temp dir and validate the expected top-level layout.
extract_dir="$tmp_dir/extract"
mkdir -p "$extract_dir"
tar -xzf "$archive" -C "$extract_dir" || die "Extraction failed."
[ -f "$extract_dir/app/Modificus.Curator" ] \
    || die "Archive is missing app/Modificus.Curator. Got an unexpected layout."
[ -f "$extract_dir/relay/mod_relay.exe" ] \
    || die "Archive is missing relay/mod_relay.exe. Got an unexpected layout."

# Install: replace only app/ and relay/ under the root. Never delete the root
# itself; it also holds profiles/, mods/, logs/, and config.json.
msg "Installing to $INSTALL_ROOT ..."
mkdir -p "$INSTALL_ROOT" || die "Could not create install root: $INSTALL_ROOT"
rm -rf "$INSTALL_ROOT/app" "$INSTALL_ROOT/relay"
cp -a "$extract_dir/app" "$INSTALL_ROOT/app"
cp -a "$extract_dir/relay" "$INSTALL_ROOT/relay"

# Mark the UI and the nxm handler executable.
for bin_name in Modificus.Curator Modificus.Curator.NxmHandler; do
    bin_path="$INSTALL_ROOT/app/$bin_name"
    if [ -e "$bin_path" ]; then
        chmod +x "$bin_path" 2>/dev/null || warn "Could not mark $bin_name executable."
    fi
done

# Launcher symlink in ~/.local/bin (no sudo). Warn + print the path if it fails.
bin_dir=$(dirname "$BIN_LINK")
if ! mkdir -p "$bin_dir" 2>/dev/null; then
    warn "Could not create bin directory: $bin_dir"
fi
if ln -sf "$INSTALL_ROOT/app/Modificus.Curator" "$BIN_LINK" 2>/dev/null; then
    msg "Symlinked: $BIN_LINK -> $INSTALL_ROOT/app/Modificus.Curator"
else
    warn "Could not create symlink $BIN_LINK (directory missing or not writable)."
    warn "Run Curator directly: $INSTALL_ROOT/app/Modificus.Curator"
fi

# Best-effort runtime check. Warn only; do not fail the install.
if command -v dotnet >/dev/null 2>&1; then
    if dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.NETCore.App 10\.'; then
        msg ".NET 10 Runtime detected."
    else
        warn ".NET 10 Runtime was not found in 'dotnet --list-runtimes'."
        warn "Install it from https://dotnet.microsoft.com/download/dotnet/10.0"
    fi
else
    warn "'dotnet' is not on PATH; Curator needs the .NET 10 Runtime."
    warn "Install it from https://dotnet.microsoft.com/download/dotnet/10.0"
fi

msg ""
msg "Modificus Curator $tag installed."
msg "  Install root: $INSTALL_ROOT"
msg "  Launcher:     $BIN_LINK"
msg ""
msg "Next steps:"
msg "  - Make sure ~/.local/bin is on your PATH (or run the path above directly)."
msg "  - Start Curator: modificus-curator"
msg "  - To enable Nexus \"Download with manager\" links, open Curator's"
msg "    Integrations dialog and register the nxm:// handler."
