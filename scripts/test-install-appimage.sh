#!/bin/sh
# Deterministic tests for scripts/install-appimage.sh.
#
# Runs without network or FUSE: a fake AppImage fixture implements
# --appimage-extract and emits the Velopack 1.2.0 squashfs-root structure, and
# a PATH-injected fake curl serves the manifest + AppImage bytes. HOME and
# XDG_DATA_HOME are redirected to an isolated temp tree so no real user home or
# desktop integration is touched.
#
# Usage: sh scripts/test-install-appimage.sh
# Exits nonzero on any failed assertion.
set -u

# Capture the real HOME before any test overrides it, so we can assert the test
# tree never lands inside it.
REAL_HOME="${HOME:-}"

script_dir=$(cd "$(dirname "$0")" && pwd)
installer="$script_dir/install-appimage.sh"

passes=0
fails=0
work_root=""

cleanup() {
    if [ -n "$work_root" ] && [ -d "$work_root" ]; then rm -rf "$work_root" 2>/dev/null || :; fi
}
trap cleanup EXIT INT TERM

work_root=$(mktemp -d 2>/dev/null || mktemp -d -t curator-appimage-tests) \
    || { echo "ERROR: could not create temp work dir" >&2; exit 1; }

# --- assertion helpers (no set -e: count failures and keep going) ----------
ok() { passes=$((passes + 1)); }
fail() { printf 'FAIL: %s\n' "$*" >&2; fails=$((fails + 1)); }

assert_file_exists() { if [ -e "$1" ]; then ok; else fail "missing file: $1"; fi; }
assert_not_exists() { if [ -e "$1" ]; then fail "unexpected file exists: $1"; else ok; fi; }
assert_executable() { if [ -x "$1" ]; then ok; else fail "not executable: $1"; fi; }
assert_file_contains() {
    if grep -qF "$2" "$1" 2>/dev/null; then ok; else fail "$1 does not contain: $2"; fi
}
assert_file_not_contains() {
    if grep -qF "$2" "$1" 2>/dev/null; then
        fail "$1 unexpectedly contains: $2"
    else
        ok
    fi
}
assert_symlink_to() {
    _l="$1"; _t="$2"
    if [ -L "$_l" ] && [ "$(readlink "$_l")" = "$_t" ]; then
        ok
    else
        fail "$_l not a symlink to $_t (got $(readlink "$_l" 2>/dev/null))"
    fi
}
assert_same_file() {
    if cmp -s "$1" "$2"; then ok; else fail "$1 != $2 (bytes differ)"; fi
}
expect_fail() { # cmd...
    if "$@" >/dev/null 2>&1; then fail "expected nonzero exit: $*"; else ok; fi
}
expect_ok() { # cmd...
    if "$@" >/dev/null 2>&1; then ok; else fail "expected exit 0: $*"; fi
}

# --- isolated per-test environment -----------------------------------------
setup_env() {
    base="$work_root/$1"
    T_HOME="$base/home"
    T_XDG="$base/share"
    T_ROOT="$T_XDG/Modificus Curator"
    T_BIN="$base/bin/modificus-curator"
    T_APPS="$T_XDG/applications"
    T_ICONS="$T_XDG/icons/hicolor/256x256/apps"
    T_APPIMAGE="$T_ROOT/appimage/Modificus.Curator.AppImage"
    mkdir -p "$T_HOME" "$T_XDG" "$base/bin"
}

# --- fake AppImage fixture --------------------------------------------------
# $1 = output path, $2 = version marker (written to sq.version), $3 = optional
# file to omit from the extracted tree, $4 = "fail" to fail extraction.
make_fixture() {
    out="$1"; ver="$2"; omit="${3:-}"; fail_flag="${4:-}"
    cat > "$out" <<'FIX'
#!/bin/sh
# Fake Type 2 AppImage for install-appimage.sh tests.
VER='@@VER@@'
OMIT='@@OMIT@@'
FAIL='@@FAIL@@'
if [ "$1" = "--appimage-extract" ]; then
    if [ "$FAIL" = "fail" ]; then
        echo "fake appimage: extraction failed" >&2
        exit 1
    fi
    root=squashfs-root
    mkdir -p "$root/usr/bin/relay"
    [ "$OMIT" = "ModifAmorphic.ModificusCurator.desktop" ] || \
        printf '[Desktop Entry]\nType=Application\nName=Modificus Curator\nIcon=ModifAmorphic.ModificusCurator\nExec=Modificus.Curator\nCategories=Game;Utility;\nStartupWMClass=ModifAmorphic.ModificusCurator\n' > "$root/ModifAmorphic.ModificusCurator.desktop"
    [ "$OMIT" = "ModifAmorphic.ModificusCurator.png" ] || \
        printf 'FAKE_PNG_%s\n' "$VER" > "$root/ModifAmorphic.ModificusCurator.png"
    [ "$OMIT" = ".DirIcon" ] || \
        printf 'FAKE_DIRICON_%s\n' "$VER" > "$root/.DirIcon"
    [ "$OMIT" = "usr/bin/Modificus.Curator" ] || \
        { printf '#!/bin/sh\n' > "$root/usr/bin/Modificus.Curator"; chmod +x "$root/usr/bin/Modificus.Curator"; }
    [ "$OMIT" = "usr/bin/Modificus.Curator.NxmHandler" ] || \
        { printf '#!/bin/sh\n' > "$root/usr/bin/Modificus.Curator.NxmHandler"; chmod +x "$root/usr/bin/Modificus.Curator.NxmHandler"; }
    [ "$OMIT" = "usr/bin/relay/modificus_relay.exe" ] || \
        printf 'relay binary\n' > "$root/usr/bin/relay/modificus_relay.exe"
    [ "$OMIT" = "usr/bin/UpdateNix" ] || \
        { printf 'update nix\n' > "$root/usr/bin/UpdateNix"; chmod +x "$root/usr/bin/UpdateNix"; }
    [ "$OMIT" = "usr/bin/sq.version" ] || \
        printf '%s\n' "$VER" > "$root/usr/bin/sq.version"
    exit 0
fi
exit 0
FIX
    sed -e "s|@@VER@@|$ver|g" -e "s|@@OMIT@@|$omit|g" -e "s|@@FAIL@@|$fail_flag|g" \
        "$out" > "$out.tmp" && mv -f "$out.tmp" "$out"
    chmod +x "$out"
}

# --- fake curl (manifest + AppImage download interception) -----------------
# Reads FAKE_MANIFEST, FAKE_STABLE, FAKE_PRERELEASE, FAKE_CURL_LOG from env.
make_fake_curl() {
    bin_dir="$1"
    cat > "$bin_dir/curl" <<'CURL'
#!/bin/sh
# Fake curl for install-appimage.sh tests.
out=""
url=""
while [ $# -gt 0 ]; do
    case $1 in
        -o) out=$2; shift 2 ;;
        -A|-U) shift 2 ;;
        -*) shift ;;
        *) url=$1; shift ;;
    esac
done
case "$url" in
    *release.env)
        cat "$FAKE_MANIFEST"
        ;;
    *prerelease*.AppImage)
        cp "$FAKE_PRERELEASE" "$out"
        printf '%s\n' "$url" >> "$FAKE_CURL_LOG"
        ;;
    *stable*.AppImage)
        cp "$FAKE_STABLE" "$out"
        printf '%s\n' "$url" >> "$FAKE_CURL_LOG"
        ;;
    *)
        echo "fake curl: unexpected url: $url" >&2
        exit 1
        ;;
esac
CURL
    chmod +x "$bin_dir/curl"
}

# Build a fake manifest + fake bin and export the FAKE_* env consumed by the
# fake curl. $1 = stable fixture path, $2 = prerelease fixture path.
setup_fake_net() {
    fake_bin="$work_root/fakebin-$$"
    mkdir -p "$fake_bin"
    make_fake_curl "$fake_bin"
    fake_manifest="$work_root/release.env"
    cat > "$fake_manifest" <<EOF
RELEASE_URL=https://example.test/curator-stable.tar.gz
PRE_RELEASE_URL=https://example.test/curator-prerelease.tar.gz
APPIMAGE_RELEASE_URL=https://example.test/curator-stable.AppImage
APPIMAGE_PRE_RELEASE_URL=https://example.test/curator-prerelease.AppImage
EOF
    curl_log="$work_root/curl-$$-log"
    : > "$curl_log"
    export FAKE_MANIFEST="$fake_manifest" \
           FAKE_STABLE="$1" FAKE_PRERELEASE="$2" \
           FAKE_CURL_LOG="$curl_log" FAKE_BIN="$fake_bin"
}

# ===========================================================================
# Test 1: unknown arguments fail without modifying anything.
# ===========================================================================
setup_env unknown-arg
mkdir -p "$T_ROOT"
: > "$T_ROOT/marker"
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" sh "$installer" --bogus-arg
assert_file_exists "$T_ROOT/marker"
assert_not_exists "$T_APPIMAGE"
assert_not_exists "$T_ROOT/appimage"

# ===========================================================================
# Test 2: missing local override fails clearly with no install mutation.
# ===========================================================================
setup_env missing-override
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$T_ROOT/nope.AppImage" sh "$installer"
assert_not_exists "$T_APPIMAGE"
assert_not_exists "$T_ROOT/appimage"

# ===========================================================================
# Test 3: initial install via CURATOR_APPIMAGE creates AppImage, exec mode,
# symlink, desktop entry, and icon.
# ===========================================================================
setup_env initial
make_fixture "$work_root/initial.AppImage" "v1"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/initial.AppImage" sh "$installer"
assert_file_exists "$T_APPIMAGE"
assert_executable "$T_APPIMAGE"
assert_symlink_to "$T_BIN" "$T_APPIMAGE"
assert_file_exists "$T_APPS/modificus-curator.desktop"
assert_file_contains "$T_APPS/modificus-curator.desktop" "Name=Modificus Curator"
assert_file_contains "$T_APPS/modificus-curator.desktop" "Icon=modificus-curator"
assert_file_contains "$T_APPS/modificus-curator.desktop" "Categories=Game;Utility;"
assert_file_contains "$T_APPS/modificus-curator.desktop" "StartupWMClass=ModifAmorphic.ModificusCurator"
assert_file_contains "$T_APPS/modificus-curator.desktop" "$T_APPIMAGE"
assert_file_not_contains "$T_APPS/modificus-curator.desktop" "APPIMAGE"
assert_file_exists "$T_ICONS/modificus-curator.png"

# ===========================================================================
# Test 4: stable selection via fake curl (network interception).
# ===========================================================================
setup_env stable-select
make_fixture "$work_root/stable.AppImage" "stable-v1"
make_fixture "$work_root/prerelease.AppImage" "prerelease-v1"
setup_fake_net "$work_root/stable.AppImage" "$work_root/prerelease.AppImage"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" PATH="$FAKE_BIN:$PATH" sh "$installer"
assert_file_exists "$T_APPIMAGE"
assert_file_contains "$FAKE_CURL_LOG" "https://example.test/curator-stable.AppImage"
assert_file_not_contains "$FAKE_CURL_LOG" "prerelease.AppImage"
assert_same_file "$T_APPIMAGE" "$work_root/stable.AppImage"

# ===========================================================================
# Test 5: prerelease selection via fake curl + --prerelease.
# ===========================================================================
setup_env prerelease-select
make_fixture "$work_root/stable.AppImage" "stable-v1"
make_fixture "$work_root/prerelease.AppImage" "prerelease-v1"
setup_fake_net "$work_root/stable.AppImage" "$work_root/prerelease.AppImage"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" PATH="$FAKE_BIN:$PATH" CURATOR_PRERELEASE=1 sh "$installer"
assert_file_contains "$FAKE_CURL_LOG" "https://example.test/curator-prerelease.AppImage"
assert_same_file "$T_APPIMAGE" "$work_root/prerelease.AppImage"

# --prerelease flag selects prerelease too.
setup_env prerelease-flag
make_fixture "$work_root/stable.AppImage" "stable-v1"
make_fixture "$work_root/prerelease.AppImage" "prerelease-v1"
setup_fake_net "$work_root/stable.AppImage" "$work_root/prerelease.AppImage"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" PATH="$FAKE_BIN:$PATH" sh "$installer" --prerelease
assert_file_contains "$FAKE_CURL_LOG" "https://example.test/curator-prerelease.AppImage"
assert_same_file "$T_APPIMAGE" "$work_root/prerelease.AppImage"

# ===========================================================================
# Test 6: reinstall replaces only the AppImage + this installer's integration;
# shared user data survives.
# ===========================================================================
setup_env reinstall
make_fixture "$work_root/v1.AppImage" "v1"
make_fixture "$work_root/v2.AppImage" "v2"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/v1.AppImage" sh "$installer"
# Seed shared user data that must survive any reinstall.
mkdir -p "$T_ROOT/profiles" "$T_ROOT/mods" "$T_ROOT/logs" "$T_ROOT/app" "$T_ROOT/relay"
: > "$T_ROOT/profiles/p1.json"
: > "$T_ROOT/mods/m1"
: > "$T_ROOT/logs/curator.log"
: > "$T_ROOT/config.json"
: > "$T_ROOT/app-state.json"
: > "$T_ROOT/app/Modificus.Curator"
: > "$T_ROOT/relay/modificus_relay.exe"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/v2.AppImage" sh "$installer"
assert_same_file "$T_APPIMAGE" "$work_root/v2.AppImage"
assert_file_exists "$T_ROOT/profiles/p1.json"
assert_file_exists "$T_ROOT/mods/m1"
assert_file_exists "$T_ROOT/logs/curator.log"
assert_file_exists "$T_ROOT/config.json"
assert_file_exists "$T_ROOT/app-state.json"
assert_file_exists "$T_APPS/modificus-curator.desktop"
assert_file_exists "$T_ICONS/modificus-curator.png"
assert_symlink_to "$T_BIN" "$T_APPIMAGE"

# ===========================================================================
# Test 7: forced validation failure (missing required structure) preserves the
# prior AppImage.
# ===========================================================================
setup_env validation-fail
make_fixture "$work_root/good.AppImage" "v1"
make_fixture "$work_root/bad.AppImage" "v1" "usr/bin/UpdateNix"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/good.AppImage" sh "$installer"
assert_file_exists "$T_APPIMAGE"
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/bad.AppImage" sh "$installer"
assert_same_file "$T_APPIMAGE" "$work_root/good.AppImage"
# no stray temp candidate left behind in the install dir
_leftover=0
for _f in "$T_ROOT/appimage"/.curator-appimage.tmp.*; do
    if [ -e "$_f" ]; then
        _leftover=1
        break
    fi
done
if [ "$_leftover" = "0" ]; then ok; else fail "stray temp candidate left behind in $T_ROOT/appimage"; fi

# Missing the root desktop file is also rejected.
setup_env validation-fail-desktop
make_fixture "$work_root/good.AppImage" "v1"
make_fixture "$work_root/nodesktop.AppImage" "v1" "ModifAmorphic.ModificusCurator.desktop"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/good.AppImage" sh "$installer"
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/nodesktop.AppImage" sh "$installer"
assert_same_file "$T_APPIMAGE" "$work_root/good.AppImage"

# ===========================================================================
# Test 8: extraction failure preserves the prior AppImage.
# ===========================================================================
setup_env extraction-fail
make_fixture "$work_root/good.AppImage" "v1"
make_fixture "$work_root/noextract.AppImage" "v1" "" "fail"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/good.AppImage" sh "$installer"
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/noextract.AppImage" sh "$installer"
assert_same_file "$T_APPIMAGE" "$work_root/good.AppImage"

# ===========================================================================
# Test 9: standalone distribution survives; shared symlink is repointed to the
# AppImage while standalone files stay intact.
# ===========================================================================
setup_env coexist
make_fixture "$work_root/ai.AppImage" "v1"
mkdir -p "$T_ROOT/app" "$T_ROOT/relay" "$T_ROOT/profiles"
: > "$T_ROOT/app/Modificus.Curator"
: > "$T_ROOT/app/Modificus.Curator.NxmHandler"
: > "$T_ROOT/relay/modificus_relay.exe"
: > "$T_ROOT/profiles/p1.json"
: > "$T_ROOT/config.json"
# Standalone installer had pointed the symlink at its own executable.
ln -s "$T_ROOT/app/Modificus.Curator" "$T_BIN"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/ai.AppImage" sh "$installer"
# Standalone files intact.
assert_file_exists "$T_ROOT/app/Modificus.Curator"
assert_file_exists "$T_ROOT/app/Modificus.Curator.NxmHandler"
assert_file_exists "$T_ROOT/relay/modificus_relay.exe"
# Shared user data intact.
assert_file_exists "$T_ROOT/profiles/p1.json"
assert_file_exists "$T_ROOT/config.json"
# AppImage installed and symlink repointed to it.
assert_file_exists "$T_APPIMAGE"
assert_symlink_to "$T_BIN" "$T_APPIMAGE"

# ===========================================================================
# Test 10: paths containing spaces work; the desktop Exec quotes the path.
# ===========================================================================
setup_env spaces
spaced_root="$work_root/spaces/My Curator Dir"
spaced_bin="$work_root/spaces/bin/modificus-curator"
spaced_appimage="$spaced_root/appimage/Modificus.Curator.AppImage"
mkdir -p "$work_root/spaces/bin"
make_fixture "$work_root/spaced.AppImage" "v1"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$spaced_root" \
    BIN_LINK="$spaced_bin" CURATOR_APPIMAGE="$work_root/spaced.AppImage" sh "$installer"
assert_file_exists "$spaced_appimage"
assert_executable "$spaced_appimage"
assert_symlink_to "$spaced_bin" "$spaced_appimage"
assert_file_contains "$T_APPS/modificus-curator.desktop" "Exec=\"$spaced_appimage\""

# ===========================================================================
# Test 11: forced copy failure (read-only install dir, non-root only) preserves
# the prior AppImage. Under root, file-mode bits do not block writes, so the
# scenario is non-deterministic and skipped.
# ===========================================================================
setup_env copy-fail
make_fixture "$work_root/good.AppImage" "v1"
make_fixture "$work_root/v2.AppImage" "v2"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/good.AppImage" sh "$installer"
if [ "$(id -u)" = "0" ]; then
    printf 'SKIP copy-fail (running as root; perms do not block writes)\n' >&2
    ok # non-fatal: scenario cannot be exercised deterministically as root
else
    chmod 555 "$T_ROOT/appimage"
    expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
        BIN_LINK="$T_BIN" CURATOR_APPIMAGE="$work_root/v2.AppImage" sh "$installer"
    chmod 755 "$T_ROOT/appimage" 2>/dev/null || :
    assert_same_file "$T_APPIMAGE" "$work_root/good.AppImage"
fi

# ===========================================================================
# Test 12: the test tree is not inside the real HOME (no real home touched).
# Every installer invocation passes HOME="$T_HOME", so the installer never sees
# the real HOME; this asserts the temp tree itself is outside it.
# ===========================================================================
if [ -n "$REAL_HOME" ]; then
    case "$work_root" in
        "$REAL_HOME"|"$REAL_HOME"/*)
            fail "test work tree is inside the real HOME ($REAL_HOME)"
            ;;
        *)
            ok
            ;;
    esac
else
    ok
fi

# ===========================================================================
printf '\n%d passed, %d failed\n' "$passes" "$fails"
[ "$fails" -eq 0 ]
