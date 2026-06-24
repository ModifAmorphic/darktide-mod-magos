//! Integration test for the `magos_discover_detail` C-ABI seam — the variant
//! with a detail/error buffer that the live C shell (`shell/src/dllmain.c`)
//! calls. `magos_discover` is already unit-tested in `src/lib.rs`; this covers
//! the detail-buffer variant's two contracts:
//!
//!   - **Success path**: against the real `Darktide.exe` (resolved via
//!     `$DARKTIDE_GAME_DIR` / `_local/DARKTIDE.env`, the same pattern as
//!     `oracle.rs`), `magos_discover_detail` returns `MAGOS_OK`, all 16 RVAs
//!     are found, and the detail buffer is left untouched (success writes no
//!     error message). Skips cleanly when the binary is absent (portable in CI).
//!   - **Error path**: a non-PE byte buffer yields a non-OK status (the PE
//!     error code) AND populates the detail buffer with a non-empty message.

use magos_discovery::pe::map_from_file;
use magos_discovery::{magos_discover_detail, MagosAddressTable, MAGOS_ERR_PE, MAGOS_OK};
use std::path::PathBuf;

/// Resolve `DARKTIDE_GAME_DIR`: prefer the env var, else parse
/// `_local/DARKTIDE.env` (the documented local source — never committed).
/// Mirrors `tests/oracle.rs::resolve_game_dir`.
fn resolve_game_dir() -> Option<PathBuf> {
    if let Ok(d) = std::env::var("DARKTIDE_GAME_DIR") {
        if !d.is_empty() {
            return Some(PathBuf::from(d));
        }
    }
    // Walk up from CARGO_MANIFEST_DIR to find the repo root (_local/ lives at
    // the workspace root, next to discovery/).
    let mut root = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    while root.parent().is_some() {
        let env_file = root.join("_local").join("DARKTIDE.env");
        if env_file.exists() {
            if let Ok(txt) = std::fs::read_to_string(&env_file) {
                for line in txt.lines() {
                    if let Some(rest) = line.trim().strip_prefix("export DARKTIDE_GAME_DIR=") {
                        let v = rest.trim().trim_matches(|c: char| c == '\'' || c == '"');
                        if !v.is_empty() {
                            return Some(PathBuf::from(v));
                        }
                    }
                }
            }
        }
        if !root.pop() {
            break;
        }
    }
    None
}

fn darktide_exe() -> Option<PathBuf> {
    let dir = resolve_game_dir()?;
    let exe = dir.join("binaries").join("Darktide.exe");
    if exe.exists() {
        Some(exe)
    } else {
        None
    }
}

/// The 16 LuaJIT/C-API RVAs of a `MagosAddressTable` as `(name, rva)` pairs, in
/// spec-table order. Mirrors `engine::AddressTable::sixteen` for the C-ABI
/// struct (which exposes the fields but no such accessor).
fn sixteen_of(t: &MagosAddressTable) -> [(&'static str, u32); 16] {
    [
        ("lua_newstate_thunk", t.lua_newstate_thunk),
        ("lua_newstate_body", t.lua_newstate_body),
        ("lua_atpanic", t.lua_atpanic),
        ("lua_gettop", t.lua_gettop),
        ("luaL_loadbuffer", t.lual_loadbuffer),
        ("lua_pcall", t.lua_pcall),
        ("luaL_openlibs", t.lual_openlibs),
        ("lua_pushcclosure", t.lua_pushcclosure),
        ("lua_setfield", t.lua_setfield),
        ("lua_pushstring", t.lua_pushstring),
        ("lua_tolstring", t.lua_tolstring),
        ("lua_createtable", t.lua_createtable),
        ("lua_type", t.lua_type),
        ("lua_tonumber", t.lua_tonumber),
        ("lua_settop", t.lua_settop),
        ("lua_panic_body", t.lua_panic_body),
    ]
}

#[test]
fn magos_discover_detail_success_and_error_paths() {
    // ---- Error path (hermetic, always runs): a non-PE buffer must surface a
    // PE error AND populate the detail buffer with a non-empty message. ----
    // 256 bytes of zeros: large enough to pass the DOS-header size gate, but
    // with no MZ signature → PeError::BadDosSig → MAGOS_ERR_PE.
    let bad: [u8; 256] = [0; 256];
    let mut out = MagosAddressTable::default();
    let mut detail = [0u8; 256];
    let code = unsafe {
        magos_discover_detail(
            bad.as_ptr(),
            bad.len(),
            &mut out,
            detail.as_mut_ptr(),
            detail.len(),
        )
    };
    assert_ne!(code, MAGOS_OK, "non-PE input must not succeed");
    assert_eq!(code, MAGOS_ERR_PE, "non-PE input must surface a PE error");
    let nul = detail.iter().position(|&b| b == 0).unwrap_or(detail.len());
    let msg = std::str::from_utf8(&detail[..nul]).unwrap_or("<bad utf8>");
    assert!(!msg.is_empty(), "detail buffer must be populated on error");
    eprintln!("[detail] error path: rc={code}, detail=\"{msg}\"");

    // ---- Success path: against the real Darktide.exe (skip if absent, like
    // oracle.rs). Asserts MAGOS_OK, all 16 RVAs found, and the detail buffer
    // left untouched (success writes no error message). ----
    let exe = match darktide_exe() {
        Some(p) => p,
        None => {
            eprintln!(
                "[detail] SKIP success path: Darktide.exe not resolvable (set \
                 $DARKTIDE_GAME_DIR or create _local/DARKTIDE.env)."
            );
            return;
        }
    };
    let file = std::fs::read(&exe).expect("read Darktide.exe");
    let image = map_from_file(&file).expect("map Darktide.exe");
    let mut out = MagosAddressTable::default();
    let mut detail = [0u8; 256];
    let code = unsafe {
        magos_discover_detail(
            image.as_ptr(),
            image.len(),
            &mut out,
            detail.as_mut_ptr(),
            detail.len(),
        )
    };
    assert_eq!(code, MAGOS_OK, "discovery must succeed on the real binary");
    for (name, rva) in sixteen_of(&out) {
        assert!(rva != 0, "success path: {name} discovered as 0");
    }
    // Phase-1 probe additions must also be populated through the C-ABI seam.
    assert!(out.lua_getfield != 0, "success path: lua_getfield discovered as 0");
    assert!(
        out.lua_resource_bytecode != 0,
        "success path: lua_resource::bytecode loader discovered as 0"
    );
    // Phase-3 probe additions must also be populated through the C-ABI seam.
    assert!(out.lua_getfenv != 0, "success path: lua_getfenv discovered as 0");
    assert!(out.lua_setfenv != 0, "success path: lua_setfenv discovered as 0");
    // Success writes nothing to the detail buffer: it must remain empty
    // (all-NUL, i.e. untouched since zero-init — matching the C shell's
    // `uint8_t detail[256] = {0}` usage in shell/src/dllmain.c).
    assert!(
        detail.iter().all(|&b| b == 0),
        "detail buffer must be untouched on success"
    );
    eprintln!("[detail] success path: rc=MAGOS_OK, all 16 RVAs found, detail empty");
}
