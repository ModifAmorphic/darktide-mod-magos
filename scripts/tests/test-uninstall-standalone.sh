#!/bin/sh
# Deterministic tests for scripts/uninstall-standalone.sh.
#
# Runs without any real system integration: HOME and XDG_DATA_HOME are
# redirected to an isolated temp tree, and INSTALL_ROOT / BIN_LINK /
# VELOPACK_STATE_DIR are all overridden into that tree, so no real user home
# or /var/tmp/velopack is ever touched. Curator's installed state is faked with
# plain files, symlinks, and the retained Velopack package layout.
#
# Usage: sh scripts/tests/test-uninstall-standalone.sh
# Exits nonzero on any failed assertion.
set -u

# Capture the real HOME before any test overrides it, so we can assert the test
# tree never lands inside it (or under /var/tmp).
REAL_HOME="${HOME:-}"

script_dir=$(cd "$(dirname "$0")" && pwd)
scripts_dir=$(dirname "$script_dir")
uninstaller="$scripts_dir/uninstall-standalone.sh"

passes=0
fails=0
work_root=""

cleanup() {
    if [ -n "$work_root" ] && [ -d "$work_root" ]; then rm -rf "$work_root" 2>/dev/null || :; fi
}
trap cleanup EXIT INT TERM

work_root=$(mktemp -d 2>/dev/null || mktemp -d -t curator-uninstall-standalone-tests) \
    || { echo "ERROR: could not create temp work dir" >&2; exit 1; }

# --- assertion helpers (no set -e: count failures and keep going) ----------
ok() { passes=$((passes + 1)); }
fail() { printf 'FAIL: %s\n' "$*" >&2; fails=$((fails + 1)); }

assert_file_exists() { if [ -e "$1" ]; then ok; else fail "missing file: $1"; fi; }
assert_dir_exists() { if [ -d "$1" ]; then ok; else fail "missing dir: $1"; fi; }
assert_not_exists() { if [ -e "$1" ] || [ -L "$1" ]; then fail "unexpected path exists: $1"; else ok; fi; }
assert_same_file() {
    if cmp -s "$1" "$2"; then ok; else fail "$1 != $2 (bytes differ)"; fi
}
assert_symlink_to() {
    _l="$1"; _t="$2"
    if [ -L "$_l" ] && [ "$(readlink "$_l")" = "$_t" ]; then
        ok
    else
        fail "$_l not a symlink to $_t (got $(readlink "$_l" 2>/dev/null))"
    fi
}
assert_is_symlink() {
    if [ -L "$1" ]; then ok; else fail "not a symlink: $1"; fi
}
# Captures output; prints it only on failure (aids diagnosis while staying quiet
# on success, like the AppImage harness).
expect_ok() { # cmd...
    _out=$("$@" 2>&1); _rc=$?
    if [ "$_rc" -eq 0 ]; then ok; else fail "expected exit 0 (got $_rc): $*"; printf '%s\n' "$_out" >&2; fi
}
expect_fail() { # cmd...
    _out=$("$@" 2>&1); _rc=$?
    if [ "$_rc" -ne 0 ]; then ok; else fail "expected nonzero exit: $*"; printf '%s\n' "$_out" >&2; fi
}

# --- isolated per-test environment -----------------------------------------
# Sets every path var the uninstaller resolves, all under the temp tree.
setup_env() {
    base="$work_root/$1"
    T_HOME="$base/home"
    T_XDG="$base/share"
    T_ROOT="$T_XDG/Modificus Curator"
    T_BIN="$base/bin/modificus-curator"
    T_APPS="$T_XDG/applications"
    T_ICONS="$T_XDG/icons/hicolor/256x256/apps"
    T_VVELO="$base/velopack/ModifAmorphic.ModificusCurator"
    appimage_dir="$T_ROOT/appimage"
    appimage_path="$appimage_dir/Modificus.Curator.AppImage"
    standalone_ui="$T_ROOT/app/Modificus.Curator"
    standalone_handler="$T_ROOT/app/Modificus.Curator.NxmHandler"
    managed_dir="$T_ROOT/nxm-handler"
    managed_handler_path="$managed_dir/Modificus.Curator.NxmHandler"
    managed_symlink_path="$managed_dir/Modificus.Curator"
    desktop_path="$T_APPS/modificus-curator.desktop"
    nxm_desktop_path="$T_APPS/modificus-curator-nxm-handler.desktop"
    icon_path="$T_ICONS/modificus-curator.png"
    mkdir -p "$T_HOME" "$T_XDG" "$(dirname "$T_BIN")"
}

# Invoke the uninstaller with every override pointed into the temp tree, so the
# real HOME and /var/tmp/velopack are never resolved.
run_uninstall() {
    env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
        BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" \
        sh "$uninstaller" "$@"
}

# --- fixtures --------------------------------------------------------------
make_standalone_payload() {
    mkdir -p "$T_ROOT/app" "$T_ROOT/relay"
    printf 'standalone ui\n' > "$standalone_ui"
    printf 'standalone handler\n' > "$standalone_handler"
    printf 'relay\n' > "$T_ROOT/relay/modificus_relay.exe"
}
make_appimage() {
    mkdir -p "$appimage_dir"
    printf 'fake appimage bytes\n' > "$appimage_path"
}
make_main_desktop() {
    mkdir -p "$T_APPS"
    printf '[Desktop Entry]\nType=Application\nName=Modificus Curator\nExec="%s" %%U\nIcon=modificus-curator\n' "$appimage_path" > "$desktop_path"
}
make_icon() {
    mkdir -p "$T_ICONS"
    printf 'fake png\n' > "$icon_path"
}
# AppImage-managed integration: the durable per-user handler copy + the sibling
# symlink to the AppImage.
make_managed_appimage() {
    mkdir -p "$managed_dir"
    printf 'managed handler bytes\n' > "$managed_handler_path"
    ln -s "$appimage_path" "$managed_symlink_path"
}
# BIN_LINK variants.
make_bin_standalone() { ln -s "$standalone_ui" "$T_BIN"; }
make_bin_appimage() { ln -s "$appimage_path" "$T_BIN"; }
make_bin_regular() { : > "$T_BIN"; }
make_bin_unrelated() {
    mkdir -p "$work_root/unrelated"
    : > "$work_root/unrelated/program"
    ln -s "$work_root/unrelated/program" "$T_BIN"
}
make_bin_dangling_unrelated() { ln -s "$T_ROOT/does/not/exist" "$T_BIN"; }
# Standalone NXM desktop (Exec points at the standalone handler in app/).
# Mirrors LinuxNxmHandlerRegistrar.FormatExec: Exec="<path>" %u
make_nxm_desktop_standalone() {
    mkdir -p "$T_APPS"
    printf '[Desktop Entry]\nType=Application\nName=Modificus Curator NXM Handler\nExec="%s" %%u\nNoDisplay=true\nMimeType=x-scheme-handler/nxm;\n' "$standalone_handler" > "$nxm_desktop_path"
}
# AppImage-managed NXM desktop (Exec points at the managed handler copy).
make_nxm_desktop_appimage() {
    mkdir -p "$T_APPS"
    printf '[Desktop Entry]\nType=Application\nName=Modificus Curator NXM Handler\nExec="%s" %%u\nNoDisplay=true\nMimeType=x-scheme-handler/nxm;\n' "$managed_handler_path" > "$nxm_desktop_path"
}
# Near-match standalone NXM desktop: same handler path but %U (capital) field
# code. Exact-line matching must NOT treat it as standalone-owned.
make_nxm_desktop_near_match() {
    mkdir -p "$T_APPS"
    printf '[Desktop Entry]\nType=Application\nName=Modificus Curator NXM Handler\nExec="%s" %%U\nNoDisplay=true\nMimeType=x-scheme-handler/nxm;\n' "$standalone_handler" > "$nxm_desktop_path"
}
# Unrelated NXM desktop: Exec points at a completely different handler.
make_nxm_desktop_unrelated() {
    mkdir -p "$T_APPS"
    printf '[Desktop Entry]\nType=Application\nName=Other Handler\nExec="/opt/other/handler" %%u\nNoDisplay=true\nMimeType=x-scheme-handler/nxm;\n' > "$nxm_desktop_path"
}
make_user_data() {
    mkdir -p "$T_ROOT/profiles" "$T_ROOT/mods" "$T_ROOT/logs"
    printf '{}\n' > "$T_ROOT/profiles/p1.json"
    printf 'mod1\n' > "$T_ROOT/mods/m1"
    printf 'log line\n' > "$T_ROOT/logs/curator.log"
    printf '{ "state": 1 }\n' > "$T_ROOT/app-state.json"
}
# config.json with a SourceOverride that must survive byte-for-byte.
make_config_source_override() {
    printf '{ "AppUpdates": { "SourceOverride": "/home/me/curator-feed" } }\n' > "$T_ROOT/config.json"
}
# Velopack retained-package state (cached/pending update bytes).
make_velopack_state() {
    mkdir -p "$T_VVELO/packages/ModifAmorphic.ModificusCurator/0.10.3"
    printf 'retained full nupkg\n' > "$T_VVELO/packages/ModifAmorphic.ModificusCurator/0.10.3/ModifAmorphic.ModificusCurator-0.10.3-linux-x64.full.nupkg"
    printf 'beta\n' > "$T_VVELO/.beta-id"
    printf 'packages lock\n' > "$T_VVELO/packages.lock"
    printf 'temp patch state\n' > "$T_VVELO/packages/ModifAmorphic.ModificusCurator/0.10.3/.patch.tmp"
}

# PATH-injected seams for deterministic guards/failure simulation. The
# uninstaller resolves `id` and `rm` through PATH, so a fake on PATH triggers
# the production code paths without any production-specific env hook. Each fake
# bin holds ONLY the faked command so everything else still resolves to real
# binaries in the rest of PATH.
fakeid_bin="$work_root/fakeid-bin"
fakerm_bin="$work_root/fakerm-bin"
mkdir -p "$fakeid_bin" "$fakerm_bin"
# Fake `id` always reports uid 0 -> exercises the root guard.
cat > "$fakeid_bin/id" <<'ID'
#!/bin/sh
echo 0
ID
chmod +x "$fakeid_bin/id"
# Fake `rm` always fails -> simulates an unwritable path without touching perms.
cat > "$fakerm_bin/rm" <<'RM'
#!/bin/sh
echo "fake rm: denied" >&2
exit 1
RM
chmod +x "$fakerm_bin/rm"

# Convenience: full standalone + AppImage install + shared data.
seed_full_install() {
    make_standalone_payload
    make_appimage
    make_main_desktop
    make_icon
    make_bin_standalone
    make_managed_appimage
    make_nxm_desktop_standalone
    make_user_data
    make_config_source_override
    make_velopack_state
}

# ===========================================================================
# Test 1: --help prints usage and mutates nothing.
# ===========================================================================
setup_env help-no-mutation
seed_full_install
expect_ok run_uninstall --help
# Every seeded artifact must survive verbatim.
assert_file_exists "$standalone_ui"
assert_file_exists "$standalone_handler"
assert_file_exists "$T_ROOT/relay/modificus_relay.exe"
assert_file_exists "$appimage_path"
assert_file_exists "$desktop_path"
assert_file_exists "$icon_path"
assert_symlink_to "$T_BIN" "$standalone_ui"
assert_file_exists "$managed_handler_path"
assert_is_symlink "$managed_symlink_path"
assert_file_exists "$nxm_desktop_path"
assert_dir_exists "$T_VVELO"

# ===========================================================================
# Test 2: unknown / repeated / conflicting arguments fail before mutation.
# ===========================================================================
setup_env unknown-arg
seed_full_install
expect_fail run_uninstall --bogus-arg
assert_file_exists "$standalone_ui"
assert_file_exists "$appimage_path"

setup_env repeat-purge
seed_full_install
expect_fail run_uninstall --purge-data --purge-data
assert_dir_exists "$T_ROOT"

setup_env repeat-help
seed_full_install
expect_fail run_uninstall --help --help
assert_file_exists "$standalone_ui"

setup_env conflict-help-purge
seed_full_install
expect_fail run_uninstall --help --purge-data
assert_file_exists "$standalone_ui"

# ===========================================================================
# Test 3: unsafe install/bin/state paths rejected before mutation.
# ===========================================================================
setup_env unsafe-root
seed_full_install
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="/" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller"
assert_file_exists "$standalone_ui"

setup_env unsafe-bin
seed_full_install
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="/" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller"
assert_file_exists "$standalone_ui"

setup_env unsafe-velopack-slash
seed_full_install
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="/" sh "$uninstaller"
assert_file_exists "$standalone_ui"
assert_dir_exists "$T_VVELO"

setup_env unsafe-velopack-basename
seed_full_install
bad_velo="$work_root/unsafe-velopack-basename/wrongname"
mkdir -p "$(dirname "$bad_velo")"
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$bad_velo" sh "$uninstaller"
assert_file_exists "$standalone_ui"

# ===========================================================================
# Test 4: unsafe purge root basename rejected before mutation.
# ===========================================================================
setup_env unsafe-purge-root
make_standalone_payload
wrong_root="$work_root/unsafe-purge-root/WrongName"
mkdir -p "$wrong_root"
: > "$wrong_root/marker"
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$wrong_root" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller" --purge-data
assert_file_exists "$wrong_root/marker"

# ===========================================================================
# Test 5: default full uninstall removes the standalone payload + standalone
# BIN_LINK + standalone NXM desktop, and preserves the AppImage distribution
# + shared user data + Velopack state.
# ===========================================================================
setup_env default-full
seed_full_install
expect_ok run_uninstall
# Removed (owned standalone payload + integration).
assert_not_exists "$standalone_ui"
assert_not_exists "$standalone_handler"
assert_not_exists "$T_ROOT/app"
assert_not_exists "$T_ROOT/relay"
assert_not_exists "$T_BIN"
assert_not_exists "$nxm_desktop_path"
# Preserved (AppImage distribution).
assert_file_exists "$appimage_path"
assert_dir_exists "$appimage_dir"
assert_file_exists "$desktop_path"
assert_file_exists "$icon_path"
assert_file_exists "$managed_handler_path"
assert_is_symlink "$managed_symlink_path"
assert_dir_exists "$managed_dir"
assert_dir_exists "$T_VVELO"
# Preserved (shared user data).
assert_file_exists "$T_ROOT/profiles/p1.json"
assert_file_exists "$T_ROOT/mods/m1"
assert_file_exists "$T_ROOT/app-state.json"
assert_file_exists "$T_ROOT/logs/curator.log"
assert_file_exists "$T_ROOT/config.json"
assert_dir_exists "$T_ROOT"

# ===========================================================================
# Test 6: default preserves config byte-for-byte, including SourceOverride.
# ===========================================================================
setup_env default-preserve-config
make_standalone_payload
make_config_source_override
config_snapshot="$work_root/default-preserve-config/config.snapshot"
cp "$T_ROOT/config.json" "$config_snapshot"
expect_ok run_uninstall
assert_file_exists "$T_ROOT/config.json"
assert_same_file "$T_ROOT/config.json" "$config_snapshot"

# ===========================================================================
# Test 7: default preserves AppImage-target, unrelated, dangling-unrelated,
# and regular BIN_LINK (only the exact standalone-target symlink is removed).
# ===========================================================================
# AppImage-target symlink preserved.
setup_env default-bin-appimage
make_standalone_payload
make_appimage
make_bin_appimage
expect_ok run_uninstall
assert_symlink_to "$T_BIN" "$appimage_path"
assert_file_exists "$appimage_path"
# Standalone payload still removed even though the link was preserved.
assert_not_exists "$standalone_ui"

# Unrelated symlink target preserved.
setup_env default-bin-unrelated
make_standalone_payload
make_bin_unrelated
expect_ok run_uninstall
assert_symlink_to "$T_BIN" "$work_root/unrelated/program"
assert_file_exists "$work_root/unrelated/program"

# Dangling unrelated symlink preserved.
setup_env default-bin-dangling-unrelated
make_standalone_payload
make_bin_dangling_unrelated
expect_ok run_uninstall
assert_is_symlink "$T_BIN"

# Regular file BIN_LINK preserved.
setup_env default-bin-regular
make_standalone_payload
make_bin_regular
expect_ok run_uninstall
assert_file_exists "$T_BIN"

# ===========================================================================
# Test 8: default preserves an AppImage-managed NXM desktop (Exec points at
# the managed handler copy), a near-match, and an unrelated entry.
# ===========================================================================
# AppImage-managed NXM desktop preserved.
setup_env default-nxm-appimage
make_standalone_payload
make_managed_appimage
make_nxm_desktop_appimage
expect_ok run_uninstall
assert_file_exists "$nxm_desktop_path"
assert_file_exists "$managed_handler_path"

# Near-match (handler path but %U) preserved: exact-line match rejects it.
setup_env default-nxm-near-match
make_standalone_payload
make_nxm_desktop_near_match
expect_ok run_uninstall
assert_file_exists "$nxm_desktop_path"

# Unrelated NXM desktop preserved.
setup_env default-nxm-unrelated
make_standalone_payload
make_nxm_desktop_unrelated
expect_ok run_uninstall
assert_file_exists "$nxm_desktop_path"

# ===========================================================================
# Test 9: default removes the exact standalone NXM desktop while leaving the
# AppImage main desktop + icon untouched.
# ===========================================================================
setup_env default-nxm-standalone
make_standalone_payload
make_appimage
make_main_desktop
make_icon
make_nxm_desktop_standalone
expect_ok run_uninstall
assert_not_exists "$nxm_desktop_path"
# AppImage-owned desktop/icon survive.
assert_file_exists "$desktop_path"
assert_file_exists "$icon_path"

# ===========================================================================
# Test 10: default is idempotent (re-run succeeds with everything already
# removed).
# ===========================================================================
setup_env idempotent-default
seed_full_install
expect_ok run_uninstall
expect_ok run_uninstall
assert_not_exists "$standalone_ui"
assert_not_exists "$T_ROOT/app"
assert_not_exists "$T_ROOT/relay"
assert_dir_exists "$T_ROOT"

# ===========================================================================
# Test 11: absent tree is success for both modes (idempotent from empty).
# ===========================================================================
setup_env absent-default
expect_ok run_uninstall
expect_ok run_uninstall

setup_env absent-purge
expect_ok run_uninstall --purge-data
expect_ok run_uninstall --purge-data

# ===========================================================================
# Test 12: purge removes the entire correctly-named install root, including
# profiles/mods/config/logs/app-state, both distributions (standalone app/relay
# + appimage/ + nxm-handler/), NXM desktop, main desktop/icon, and Velopack
# state.
# ===========================================================================
setup_env purge-full
seed_full_install
expect_ok run_uninstall --purge-data
assert_not_exists "$T_ROOT"
assert_not_exists "$T_ROOT/profiles"
assert_not_exists "$T_ROOT/mods"
assert_not_exists "$T_ROOT/config.json"
assert_not_exists "$T_ROOT/app-state.json"
assert_not_exists "$T_ROOT/logs"
assert_not_exists "$T_ROOT/app"
assert_not_exists "$T_ROOT/relay"
assert_not_exists "$appimage_path"
assert_not_exists "$managed_dir"
assert_not_exists "$T_VVELO"
assert_not_exists "$desktop_path"
assert_not_exists "$icon_path"
assert_not_exists "$nxm_desktop_path"

# ===========================================================================
# Test 13: purge removes the standalone-target AND install-root-under command
# symlinks, but preserves unrelated and regular BIN_LINK.
# ===========================================================================
# Standalone-target symlink removed.
setup_env purge-bin-standalone
seed_full_install
expect_ok run_uninstall --purge-data
assert_not_exists "$T_BIN"

# AppImage-target symlink removed (under install root).
setup_env purge-bin-appimage
seed_full_install
rm -f "$T_BIN"
make_bin_appimage
expect_ok run_uninstall --purge-data
assert_not_exists "$T_BIN"

# Unrelated symlink preserved.
setup_env purge-bin-unrelated
seed_full_install
rm -f "$T_BIN"
make_bin_unrelated
expect_ok run_uninstall --purge-data
assert_symlink_to "$T_BIN" "$work_root/unrelated/program"
assert_file_exists "$work_root/unrelated/program"

# Regular file BIN_LINK preserved.
setup_env purge-bin-regular
seed_full_install
rm -f "$T_BIN"
make_bin_regular
expect_ok run_uninstall --purge-data
assert_file_exists "$T_BIN"

# ===========================================================================
# Test 14: purge removes the exact Curator NXM desktop even when its Exec
# targets the standalone handler (already covered) and when it targets the
# AppImage-managed handler. Confirms purge is target-agnostic.
# ===========================================================================
setup_env purge-nxm-appimage
seed_full_install
make_nxm_desktop_appimage
expect_ok run_uninstall --purge-data
assert_not_exists "$nxm_desktop_path"
assert_not_exists "$T_ROOT"

# ===========================================================================
# Test 15: purge preserves unrelated external files under the XDG tree and
# shared XDG parent directories (only exact files + the install root go).
# ===========================================================================
setup_env purge-xdg-survives
seed_full_install
# Unrelated app + icon entries must survive.
printf '[Desktop Entry]\nName=Other\n' > "$T_APPS/other.desktop"
printf 'other png\n' > "$T_ICONS/other.png"
expect_ok run_uninstall --purge-data
assert_not_exists "$T_ROOT"
assert_not_exists "$desktop_path"
assert_not_exists "$icon_path"
assert_not_exists "$nxm_desktop_path"
# The user-level applications/ + icon hierarchy stay, even when now empty.
assert_dir_exists "$T_APPS"
assert_dir_exists "$T_ICONS"
assert_dir_exists "$T_XDG/icons/hicolor/256x256"
assert_dir_exists "$T_XDG/icons/hicolor"
assert_dir_exists "$T_XDG/icons"
# Unrelated entries untouched.
assert_file_exists "$T_APPS/other.desktop"
assert_file_exists "$T_ICONS/other.png"

# ===========================================================================
# Test 16: paths containing spaces work end to end.
# ===========================================================================
setup_env spaces
spaced_root="$work_root/spaces/My Curator Dir"
spaced_bin="$work_root/spaces/bin with spaces/modificus-curator"
spaced_velo="$work_root/spaces/velopack/ModifAmorphic.ModificusCurator"
mkdir -p "$(dirname "$spaced_bin")" "$(dirname "$spaced_velo")"
spaced_appimage="$spaced_root/appimage/Modificus.Curator.AppImage"
spaced_ui="$spaced_root/app/Modificus.Curator"
spaced_handler="$spaced_root/app/Modificus.Curator.NxmHandler"
spaced_nxm="$T_APPS/modificus-curator-nxm-handler.desktop"
mkdir -p "$spaced_root/app" "$spaced_root/appimage" "$spaced_root/relay" "$spaced_root/profiles" "$T_APPS"
printf 'appimage\n' > "$spaced_appimage"
printf 'ui\n' > "$spaced_ui"
printf 'handler\n' > "$spaced_handler"
printf 'relay\n' > "$spaced_root/relay/modificus_relay.exe"
printf 'profile\n' > "$spaced_root/profiles/p1.json"
ln -s "$spaced_ui" "$spaced_bin"
printf '[Desktop Entry]\nExec="%s" %%u\n' "$spaced_handler" > "$spaced_nxm"
# Confirm the seeded spaced-path fixtures exist before uninstall.
assert_file_exists "$spaced_nxm"
assert_symlink_to "$spaced_bin" "$spaced_ui"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$spaced_root" \
    BIN_LINK="$spaced_bin" VELOPACK_STATE_DIR="$spaced_velo" sh "$uninstaller"
assert_not_exists "$spaced_ui"
assert_not_exists "$spaced_handler"
assert_not_exists "$spaced_root/app"
assert_not_exists "$spaced_root/relay"
assert_not_exists "$spaced_bin"
assert_not_exists "$spaced_nxm"
# AppImage + shared data preserved.
assert_file_exists "$spaced_appimage"
assert_file_exists "$spaced_root/profiles/p1.json"

# ===========================================================================
# Test 17: relative override values rejected before mutation (must be absolute).
# ===========================================================================
setup_env relative-root
make_standalone_payload
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="relative/root" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller"
assert_file_exists "$standalone_ui"

setup_env relative-bin
make_standalone_payload
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="relative/bin/link" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller"
assert_file_exists "$standalone_ui"

# Correct basename but relative path: the absolute check fires first.
setup_env relative-velopack
make_standalone_payload
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="relative/velopack/ModifAmorphic.ModificusCurator" \
    sh "$uninstaller"
assert_file_exists "$standalone_ui"

# ===========================================================================
# Test 18: root execution is rejected; --help still works as root. Uses a
# PATH-injected fake `id` (reports uid 0) so the guard fires deterministically
# without actually being root.
# ===========================================================================
setup_env root-rejected
make_standalone_payload
expect_fail env PATH="$fakeid_bin:$PATH" HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" \
    INSTALL_ROOT="$T_ROOT" BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" \
    sh "$uninstaller"
assert_file_exists "$standalone_ui"

setup_env root-help-ok
make_standalone_payload
# --help exits before the root guard, so it succeeds even with the fake root id.
expect_ok env PATH="$fakeid_bin:$PATH" HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" \
    INSTALL_ROOT="$T_ROOT" BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" \
    sh "$uninstaller" --help
assert_file_exists "$standalone_ui"

# ===========================================================================
# Test 19: a removal failure yields a nonzero exit and suppresses the
# clean-success banner. Simulated with a PATH-injected fake `rm` that always
# fails (portable; no real permission changes).
# ===========================================================================
# Default mode: standalone payload survives a failed rm -rf, exit nonzero.
setup_env rm-failure-default
seed_full_install
rm_out=$(env PATH="$fakerm_bin:$PATH" HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" \
    INSTALL_ROOT="$T_ROOT" BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" \
    sh "$uninstaller" 2>&1); rm_rc=$?
if [ "$rm_rc" -ne 0 ]; then ok; else fail "expected nonzero exit on rm failure (default), got $rm_rc"; printf '%s\n' "$rm_out" >&2; fi
assert_file_exists "$standalone_ui"
assert_dir_exists "$T_VVELO"
case "$rm_out" in *"Could not remove"*) ok ;; *) fail "default rm-failure output lacks a removal warning"; printf '%s\n' "$rm_out" >&2 ;; esac

# Purge mode: install root survives a failed rm -rf, exit nonzero, and the
# "fully uninstalled" banner must NOT print.
setup_env rm-failure-purge
seed_full_install
rm_out=$(env PATH="$fakerm_bin:$PATH" HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" \
    INSTALL_ROOT="$T_ROOT" BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" \
    sh "$uninstaller" --purge-data 2>&1); rm_rc=$?
if [ "$rm_rc" -ne 0 ]; then ok; else fail "expected nonzero exit on rm failure (purge), got $rm_rc"; printf '%s\n' "$rm_out" >&2; fi
assert_dir_exists "$T_ROOT"
case "$rm_out" in
    *"fully uninstalled"*) fail "purge printed clean-success banner despite rm failure"; printf '%s\n' "$rm_out" >&2 ;;
    *) ok ;;
esac
case "$rm_out" in *"completed with"*) ok ;; *) fail "purge rm-failure output lacks error summary"; printf '%s\n' "$rm_out" >&2 ;; esac

# ===========================================================================
# Test 20: no real HOME or /var/tmp/velopack is touched. Every invocation
# redirects HOME/XDG + every override into the temp tree; this asserts the
# temp tree itself is outside both real HOME and /var/tmp.
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
case "$work_root" in
    /var/tmp|/var/tmp/*)
        fail "test work tree is under /var/tmp (would collide with real Velopack state)"
        ;;
    *)
        ok
        ;;
esac

# ===========================================================================
printf '\n%d passed, %d failed\n' "$passes" "$fails"
[ "$fails" -eq 0 ]
