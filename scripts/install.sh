#!/bin/sh
# Modificus Curator Linux installer.
#
# Installs the latest GitHub release (prereleases included) into Curator's
# default app-data root, adds a launcher symlink in ~/.local/bin, and prints
# the next steps. Served from raw/main:
#
#   curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh
#
# The script always installs the latest release visible to an unauthenticated
# request (drafts are excluded by the API). Users wanting a specific release
# download and manage it themselves.
#
# Testing overrides (env vars, not needed for normal use):
#   INSTALL_ROOT=<dir>        install into this dir instead of the default root
#   BIN_LINK=<path>           create the launcher symlink here
#   CURATOR_REPO=<owner/repo> install from this repo
#   CURATOR_ARCHIVE=<file>    use a local tar.gz instead of downloading (used
#                             to exercise extraction against a fake archive)
set -eu

REPO="${CURATOR_REPO:-ModifAmorphic/darktide-modificus-curator}"
ASSET_PREFIX="curator-"
ASSET_SUFFIX="-linux-x64.tar.gz"
UA="modificus-curator-installer"

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

msg() { printf '%s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

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
# entirely and derives the tag from the filename; otherwise query the releases
# list endpoint and download. The list endpoint excludes drafts for
# unauthenticated requests and returns newest first, so the first tag_name is
# the latest release (prerelease or stable). /releases/latest is not used
# because it skips prereleases.
if [ -n "${CURATOR_ARCHIVE:-}" ]; then
    [ -f "$CURATOR_ARCHIVE" ] || die "CURATOR_ARCHIVE does not exist: $CURATOR_ARCHIVE"
    arcbasename=$(basename "$CURATOR_ARCHIVE")
    case "$arcbasename" in
        *"$ASSET_SUFFIX") tag=${arcbasename%"$ASSET_SUFFIX"} ;;
        *) tag="unknown" ;;
    esac
    tag="${tag#"$ASSET_PREFIX"}"
    asset_name="${ASSET_PREFIX}${tag}${ASSET_SUFFIX}"
    archive="$tmp_dir/$asset_name"
    cp "$CURATOR_ARCHIVE" "$archive"
    msg "Using local archive: $CURATOR_ARCHIVE (tag $tag, testing override)"
else
    msg "Finding the latest release of $REPO ..."
    releases_json=$(fetch "https://api.github.com/repos/$REPO/releases") \
        || die "Could not fetch the release list. If this is a rate limit, wait and retry."
    tag=$(printf '%s\n' "$releases_json" \
        | grep '"tag_name"' | head -n 1 \
        | sed 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/')
    [ -n "$tag" ] || die "Could not parse a release tag from the GitHub API response."
    asset_name="${ASSET_PREFIX}${tag}${ASSET_SUFFIX}"
    asset_url="https://github.com/$REPO/releases/download/${tag}/${asset_name}"
    archive="$tmp_dir/$asset_name"
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
[ -f "$extract_dir/relay/modificus_relay.exe" ] \
    || die "Archive is missing relay/modificus_relay.exe. Got an unexpected layout."

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
msg "  - On first run Curator registers the nxm:// handler itself, so Nexus"
msg "    \"Download with manager\" links work without any extra setup."
