#!/bin/sh
# Deterministic tests for scripts/uninstall-appimage.sh.
#
# Runs without any real system integration: HOME and XDG_DATA_HOME are
# redirected to an isolated temp tree, and INSTALL_ROOT / BIN_LINK /
# VELOPACK_STATE_DIR are all overridden into that tree, so no real user home
# or /var/tmp/velopack is ever touched. Curator's installed state is faked with
# plain files, symlinks, and the retained Velopack package layout.
#
# Usage: sh scripts/test-uninstall-appimage.sh
# Exits nonzero on any failed assertion.
set -u

# Capture the real HOME before any test overrides it, so we can assert the test
# tree never lands inside it (or under /var/tmp).
REAL_HOME="${HOME:-}"

script_dir=$(cd "$(dirname "$0")" && pwd)
uninstaller="$script_dir/uninstall-appimage.sh"

passes=0
fails=0
work_root=""

cleanup() {
    if [ -n "$work_root" ] && [ -d "$work_root" ]; then rm -rf "$work_root" 2>/dev/null || :; fi
}
trap cleanup EXIT INT TERM

work_root=$(mktemp -d 2>/dev/null || mktemp -d -t curator-uninstall-tests) \
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
# on success, like the install harness).
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
    desktop_path="$T_APPS/modificus-curator.desktop"
    nxm_desktop_path="$T_APPS/modificus-curator-nxm-handler.desktop"
    icon_path="$T_ICONS/modificus-curator.png"
    managed_dir="$T_ROOT/nxm-handler"
    managed_handler_path="$managed_dir/Modificus.Curator.NxmHandler"
    managed_symlink_path="$managed_dir/Modificus.Curator"
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
make_bin_appimage() { ln -s "$appimage_path" "$T_BIN"; }
make_bin_standalone() {
    mkdir -p "$T_ROOT/app"
    : > "$T_ROOT/app/Modificus.Curator"
    ln -s "$T_ROOT/app/Modificus.Curator" "$T_BIN"
}
make_bin_regular() {
    : > "$T_BIN"
}
make_managed_appimage() {
    mkdir -p "$managed_dir"
    printf 'managed handler bytes\n' > "$managed_handler_path"
    ln -s "$appimage_path" "$managed_symlink_path"
}
# AppImage-managed NXM desktop (Exec points at the managed handler copy).
make_nxm_desktop_appimage() {
    mkdir -p "$T_APPS"
    printf '[Desktop Entry]\nType=Application\nName=Modificus Curator NXM Handler\nExec="%s" %%u\nNoDisplay=true\nMimeType=x-scheme-handler/nxm;\n' "$managed_handler_path" > "$nxm_desktop_path"
}
# Standalone NXM desktop (Exec points into app/).
make_nxm_desktop_standalone() {
    mkdir -p "$T_APPS" "$T_ROOT/app"
    : > "$T_ROOT/app/Modificus.Curator.NxmHandler"
    printf '[Desktop Entry]\nType=Application\nName=Modificus Curator NXM Handler\nExec="%s" %%u\nNoDisplay=true\nMimeType=x-scheme-handler/nxm;\n' "$T_ROOT/app/Modificus.Curator.NxmHandler" > "$nxm_desktop_path"
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
make_standalone_payload() {
    mkdir -p "$T_ROOT/app" "$T_ROOT/relay"
    printf 'standalone ui\n' > "$T_ROOT/app/Modificus.Curator"
    printf 'standalone handler\n' > "$T_ROOT/app/Modificus.Curator.NxmHandler"
    printf 'relay\n' > "$T_ROOT/relay/modificus_relay.exe"
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


# Convenience: full AppImage-managed install + shared data + standalone.
seed_full_install() {
    make_appimage
    make_main_desktop
    make_icon
    make_bin_appimage
    make_managed_appimage
    make_nxm_desktop_appimage
    make_user_data
    make_config_source_override
    make_standalone_payload
    make_velopack_state
}

# ===========================================================================
# Test 1: --help prints usage and mutates nothing.
# ===========================================================================
setup_env help-no-mutation
seed_full_install
expect_ok run_uninstall --help
# Every seeded artifact must survive verbatim.
assert_file_exists "$appimage_path"
assert_file_exists "$desktop_path"
assert_file_exists "$icon_path"
assert_symlink_to "$T_BIN" "$appimage_path"
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
assert_file_exists "$appimage_path"
assert_file_exists "$desktop_path"

setup_env repeat-purge
seed_full_install
expect_fail run_uninstall --purge-data --purge-data
assert_dir_exists "$T_ROOT"

setup_env repeat-help
seed_full_install
expect_fail run_uninstall --help --help
assert_file_exists "$appimage_path"

setup_env conflict-help-purge
seed_full_install
expect_fail run_uninstall --help --purge-data
assert_file_exists "$appimage_path"

# ===========================================================================
# Test 3: unsafe install/bin/state paths rejected before mutation.
# ===========================================================================
setup_env unsafe-root
seed_full_install
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="/" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller"
assert_file_exists "$appimage_path"

setup_env unsafe-bin
seed_full_install
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="/" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller"
assert_file_exists "$appimage_path"

setup_env unsafe-velopack-slash
seed_full_install
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="/" sh "$uninstaller"
assert_file_exists "$appimage_path"
assert_dir_exists "$T_VVELO"

setup_env unsafe-velopack-basename
seed_full_install
bad_velo="$work_root/unsafe-velopack-basename/wrongname"
mkdir -p "$(dirname "$bad_velo")"
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$bad_velo" sh "$uninstaller"
assert_file_exists "$appimage_path"

# ===========================================================================
# Test 4: unsafe purge root basename rejected before mutation.
# ===========================================================================
setup_env unsafe-purge-root
make_appimage
wrong_root="$work_root/unsafe-purge-root/WrongName"
mkdir -p "$wrong_root"
: > "$wrong_root/marker"
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$wrong_root" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller" --purge-data
assert_file_exists "$wrong_root/marker"

# ===========================================================================
# Test 5: default full uninstall removes owned payload + integration +
# AppImage NXM managed files + Velopack state.
# ===========================================================================
setup_env default-full
seed_full_install
expect_ok run_uninstall
# Removed (owned).
assert_not_exists "$appimage_path"
assert_not_exists "$appimage_dir"
assert_not_exists "$desktop_path"
assert_not_exists "$icon_path"
assert_not_exists "$T_BIN"
assert_not_exists "$nxm_desktop_path"
assert_not_exists "$managed_handler_path"
assert_not_exists "$managed_symlink_path"
assert_not_exists "$managed_dir"
assert_not_exists "$T_VVELO"
# Preserved (shared user data + standalone).
assert_file_exists "$T_ROOT/profiles/p1.json"
assert_file_exists "$T_ROOT/mods/m1"
assert_file_exists "$T_ROOT/app-state.json"
assert_file_exists "$T_ROOT/logs/curator.log"
assert_file_exists "$T_ROOT/app/Modificus.Curator"
assert_file_exists "$T_ROOT/app/Modificus.Curator.NxmHandler"
assert_file_exists "$T_ROOT/relay/modificus_relay.exe"
assert_dir_exists "$T_ROOT"

# ===========================================================================
# Test 6: default preserves config byte-for-byte, including SourceOverride.
# ===========================================================================
setup_env default-preserve-config
make_appimage
make_config_source_override
config_snapshot="$work_root/default-preserve-config/config.snapshot"
cp "$T_ROOT/config.json" "$config_snapshot"
expect_ok run_uninstall
assert_file_exists "$T_ROOT/config.json"
assert_same_file "$T_ROOT/config.json" "$config_snapshot"

# ===========================================================================
# Test 7: default preserves standalone-target, unrelated, and regular BIN_LINK.
# ===========================================================================
# Standalone-target symlink (-> app/) is preserved.
setup_env default-bin-standalone
make_appimage
make_bin_standalone
expect_ok run_uninstall
assert_symlink_to "$T_BIN" "$T_ROOT/app/Modificus.Curator"
assert_file_exists "$T_ROOT/app/Modificus.Curator"

# Unrelated symlink target is preserved.
setup_env default-bin-unrelated
make_appimage
mkdir -p "$work_root/default-bin-unrelated/other"
: > "$work_root/default-bin-unrelated/other/program"
ln -s "$work_root/default-bin-unrelated/other/program" "$T_BIN"
expect_ok run_uninstall
assert_symlink_to "$T_BIN" "$work_root/default-bin-unrelated/other/program"

# Regular file BIN_LINK is preserved.
setup_env default-bin-regular
make_appimage
make_bin_regular
expect_ok run_uninstall
assert_file_exists "$T_BIN"

# ===========================================================================
# Test 8: default preserves a standalone NXM desktop (Exec into app/).
# ===========================================================================
setup_env default-nxm-standalone
make_appimage
make_managed_appimage
make_nxm_desktop_standalone
expect_ok run_uninstall
assert_file_exists "$nxm_desktop_path"
assert_file_exists "$T_ROOT/app/Modificus.Curator.NxmHandler"

# ===========================================================================
# Test 9: unexpected files inside appimage/ and nxm-handler/ survive and keep
# their directories in place.
# ===========================================================================
setup_env default-unexpected-files
make_appimage
make_managed_appimage
make_nxm_desktop_appimage
mkdir -p "$appimage_dir" "$managed_dir"
printf 'keep me\n' > "$appimage_dir/extra.txt"
printf 'keep me too\n' > "$managed_dir/notes.txt"
expect_ok run_uninstall
# Owned files removed, unexpected files + dirs survive.
assert_not_exists "$appimage_path"
assert_not_exists "$managed_handler_path"
assert_not_exists "$managed_symlink_path"
assert_file_exists "$appimage_dir/extra.txt"
assert_file_exists "$managed_dir/notes.txt"
assert_dir_exists "$appimage_dir"
assert_dir_exists "$managed_dir"

# ===========================================================================
# Test 10: managed NXM symlink target survives; dangling symlink is removed.
# ===========================================================================
# Symlink target (a sentinel file) is NOT followed/deleted when the link is.
setup_env default-symlink-target-survives
make_appimage
make_managed_appimage
sentinel="$work_root/default-symlink-target-survives/sentinel.txt"
printf 'sentinel\n' > "$sentinel"
# Repoint the managed sibling symlink at the sentinel (not the AppImage).
rm -f "$managed_symlink_path"
ln -s "$sentinel" "$managed_symlink_path"
expect_ok run_uninstall
assert_file_exists "$sentinel"
assert_not_exists "$managed_symlink_path"
assert_not_exists "$managed_handler_path"
assert_not_exists "$managed_dir"

# Dangling managed symlink is removed cleanly (rm -f on a broken link).
setup_env default-dangling-symlink
make_appimage
make_managed_appimage
rm -f "$managed_symlink_path"
ln -s "$T_ROOT/does/not/exist" "$managed_symlink_path"
expect_ok run_uninstall
assert_not_exists "$managed_symlink_path"
assert_not_exists "$managed_handler_path"

# ===========================================================================
# Test 11: purge removes the entire correctly-named install root, including
# profiles/mods/config/logs/app-state and standalone app/relay.
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

# ===========================================================================
# Test 12: purge removes AppImage-target and standalone-under-root command
# symlinks, but preserves unrelated and regular BIN_LINK.
# ===========================================================================
# AppImage-target symlink removed.
setup_env purge-bin-appimage
seed_full_install
expect_ok run_uninstall --purge-data
assert_not_exists "$T_BIN"

# Standalone-under-root symlink removed.
setup_env purge-bin-standalone-under-root
seed_full_install
rm -f "$T_BIN"
mkdir -p "$T_ROOT/app"
: > "$T_ROOT/app/Modificus.Curator"
ln -s "$T_ROOT/app/Modificus.Curator" "$T_BIN"
expect_ok run_uninstall --purge-data
assert_not_exists "$T_BIN"

# Unrelated symlink preserved.
setup_env purge-bin-unrelated
seed_full_install
other="$work_root/purge-bin-unrelated/other/program"
mkdir -p "$(dirname "$other")"
: > "$other"
rm -f "$T_BIN"
ln -s "$other" "$T_BIN"
expect_ok run_uninstall --purge-data
assert_symlink_to "$T_BIN" "$other"
assert_file_exists "$other"

# Regular file BIN_LINK preserved.
setup_env purge-bin-regular
seed_full_install
rm -f "$T_BIN"
: > "$T_BIN"
expect_ok run_uninstall --purge-data
assert_file_exists "$T_BIN"

# ===========================================================================
# Test 13: purge removes the exact Curator NXM desktop even when its Exec
# targets the standalone handler.
# ===========================================================================
setup_env purge-nxm-standalone
seed_full_install
# Overwrite the NXM desktop with a standalone-targeted Exec.
make_nxm_desktop_standalone
expect_ok run_uninstall --purge-data
assert_not_exists "$nxm_desktop_path"
assert_not_exists "$T_ROOT"

# ===========================================================================
# Test 14: both modes remove isolated Velopack retained 0.10.3 package state.
# ===========================================================================
setup_env velopack-default
make_velopack_state
expect_ok run_uninstall
assert_not_exists "$T_VVELO"

setup_env velopack-purge
make_velopack_state
expect_ok run_uninstall --purge-data
assert_not_exists "$T_VVELO"

# ===========================================================================
# Test 15: absent / idempotent for both modes.
# ===========================================================================
# Empty tree: default mode succeeds with everything absent.
setup_env absent-default
expect_ok run_uninstall
# Idempotent: a second run also succeeds.
expect_ok run_uninstall

# Empty tree: purge mode succeeds (root absent, basename still validated).
setup_env absent-purge
expect_ok run_uninstall --purge-data
expect_ok run_uninstall --purge-data

# Idempotent after a real default uninstall: re-run is all-absent.
setup_env idempotent-after-default
seed_full_install
expect_ok run_uninstall
expect_ok run_uninstall
assert_not_exists "$appimage_path"
assert_dir_exists "$T_ROOT"

# ===========================================================================
# Test 16: paths containing spaces work end to end.
# ===========================================================================
setup_env spaces
spaced_root="$work_root/spaces/My Curator Dir"
spaced_bin="$work_root/spaces/bin with spaces/modificus-curator"
spaced_velo="$work_root/spaces/velopack/ModifAmorphic.ModificusCurator"
mkdir -p "$(dirname "$spaced_bin")" "$(dirname "$spaced_velo")"
spaced_appimage="$spaced_root/appimage/Modificus.Curator.AppImage"
spaced_managed="$spaced_root/nxm-handler/Modificus.Curator.NxmHandler"
spaced_managed_link="$spaced_root/nxm-handler/Modificus.Curator"
spaced_nxm="$T_APPS/modificus-curator-nxm-handler.desktop"
mkdir -p "$spaced_root/appimage" "$spaced_root/nxm-handler" "$spaced_root/profiles" "$T_APPS"
printf 'appimage\n' > "$spaced_appimage"
ln -s "$spaced_appimage" "$spaced_bin"
printf 'handler\n' > "$spaced_managed"
ln -s "$spaced_appimage" "$spaced_managed_link"
printf '[Desktop Entry]\nExec="%s" %%u\n' "$spaced_managed" > "$spaced_nxm"
printf 'profile\n' > "$spaced_root/profiles/p1.json"
# Confirm the seeded spaced-path NXM desktop exists before uninstall, so the
# post-uninstall removal assertion below is meaningful.
assert_file_exists "$spaced_nxm"
expect_ok env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$spaced_root" \
    BIN_LINK="$spaced_bin" VELOPACK_STATE_DIR="$spaced_velo" sh "$uninstaller"
assert_not_exists "$spaced_appimage"
assert_not_exists "$spaced_bin"
assert_not_exists "$spaced_managed"
assert_not_exists "$spaced_managed_link"
assert_not_exists "$spaced_nxm"
assert_file_exists "$spaced_root/profiles/p1.json"

# ===========================================================================
# Test 17: relative override values rejected before mutation (must be absolute).
# ===========================================================================
setup_env relative-root
make_appimage
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="relative/root" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller"
assert_file_exists "$appimage_path"

setup_env relative-bin
make_appimage
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="relative/bin/link" VELOPACK_STATE_DIR="$T_VVELO" sh "$uninstaller"
assert_file_exists "$appimage_path"

# Correct basename but relative path: the absolute check fires first.
setup_env relative-velopack
make_appimage
expect_fail env HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" INSTALL_ROOT="$T_ROOT" \
    BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="relative/velopack/ModifAmorphic.ModificusCurator" \
    sh "$uninstaller"
assert_file_exists "$appimage_path"

# ===========================================================================
# Test 18: shared XDG directories survive --purge-data (only exact files go).
# ===========================================================================
setup_env purge-xdg-survives
seed_full_install
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

# ===========================================================================
# Test 19: root execution is rejected; --help still works as root. Uses a
# PATH-injected fake `id` (reports uid 0) so the guard fires deterministically
# without actually being root.
# ===========================================================================
setup_env root-rejected
make_appimage
expect_fail env PATH="$fakeid_bin:$PATH" HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" \
    INSTALL_ROOT="$T_ROOT" BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" \
    sh "$uninstaller"
assert_file_exists "$appimage_path"

setup_env root-help-ok
make_appimage
# --help exits before the root guard, so it succeeds even with the fake root id.
expect_ok env PATH="$fakeid_bin:$PATH" HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" \
    INSTALL_ROOT="$T_ROOT" BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" \
    sh "$uninstaller" --help
assert_file_exists "$appimage_path"

# ===========================================================================
# Test 20: a removal failure yields a nonzero exit and suppresses the
# clean-success banner. Simulated with a PATH-injected fake `rm` that always
# fails (portable; no real permission changes).
# ===========================================================================
# Default mode: owned files survive, exit nonzero, warnings emitted.
setup_env rm-failure-default
seed_full_install
rm_out=$(env PATH="$fakerm_bin:$PATH" HOME="$T_HOME" XDG_DATA_HOME="$T_XDG" \
    INSTALL_ROOT="$T_ROOT" BIN_LINK="$T_BIN" VELOPACK_STATE_DIR="$T_VVELO" \
    sh "$uninstaller" 2>&1); rm_rc=$?
if [ "$rm_rc" -ne 0 ]; then ok; else fail "expected nonzero exit on rm failure (default), got $rm_rc"; printf '%s\n' "$rm_out" >&2; fi
assert_file_exists "$appimage_path"
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
# Test 21: no real HOME or /var/tmp/velopack is touched. Every invocation
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
