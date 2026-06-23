//! Oracle integration test (Spike 001, step 2).
//!
//! Resolves the installed `Darktide.exe` via `$DARKTIDE_GAME_DIR` (read from
//! `_local/DARKTIDE.env` at the repo root — the env var is NOT set in the
//! shell by default), runs the full discovery engine, and validates the
//! result on two tiers:
//!
//!   - **Tier 1 — pinned exact-match.** Runs *only* if the resolved binary's
//!     SHA-256 equals the oracle's pinned SHA (`132eed5f…`). Asserts every one
//!     of the 16 discovered RVAs equals the POC's `addresses.json` value.
//!   - **Tier 2 — matcher self-validation (always).** [`discover`] returning
//!     `Ok` is itself the proof: each of the 16 finders requires a *unique*
//!     cluster candidate whose body satisfies that function's source-pattern
//!     signature, so success means all 16 were found unambiguously. We then
//!     also re-check each discovered address against its matcher and print the
//!     full table.
//!
//! The test **skips** (does not fail) when the binary is absent, so the suite
//! is portable; it runs fully wherever a Darktide install is reachable.

use magos_discovery::pe::{map_from_file, Pe};
use magos_discovery::patterns::{self, Cluster};
use magos_discovery::{discover, oracle::PINNED_SHA256, oracle::PINNED_SIXTEEN, DiscoverError};
use sha2::{Digest, Sha256};
use std::path::PathBuf;

/// Resolve `DARKTIDE_GAME_DIR`: prefer the env var, else parse
/// `_local/DARKTIDE.env` (the documented local source — never committed).
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

fn sha256_hex(bytes: &[u8]) -> String {
    let mut h = Sha256::new();
    h.update(bytes);
    let out = h.finalize();
    out.iter().map(|b| format!("{:02x}", b)).collect()
}

#[test]
fn oracle_all_sixteen_match() {
    let exe = match darktide_exe() {
        Some(p) => p,
        None => {
            eprintln!(
                "[oracle] SKIP: Darktide.exe not resolvable (set $DARKTIDE_GAME_DIR or \
                 create _local/DARKTIDE.env). Tier-1/Tier-2 cannot run without the binary."
            );
            return;
        }
    };
    let file = std::fs::read(&exe).expect("read Darktide.exe");
    let sha = sha256_hex(&file);
    let pinned = sha == PINNED_SHA256;
    eprintln!("[oracle] binary = {}", exe.display());
    eprintln!("[oracle] sha256 = {sha}");
    eprintln!(
        "[oracle] {} the pinned build ({})",
        if pinned { "MATCHES" } else { "DIFFERS FROM" },
        PINNED_SHA256
    );

    let image = map_from_file(&file).expect("map Darktide.exe");
    let table = discover(&image).expect("discovery must find all 16 uniquely (Tier 2)");

    // ---- Tier 1: pinned exact-match (only when the build matches the oracle) ----
    if pinned {
        for (name, pinned_rva) in PINNED_SIXTEEN {
            let got = table.sixteen().iter().find(|(n, _)| *n == name).unwrap().1;
            assert_eq!(
                got, pinned_rva,
                "Tier-1 mismatch on {name}: got 0x{got:x}, pinned 0x{pinned_rva:x}"
            );
        }
        eprintln!("[oracle] Tier-1 PASS: all 16 RVAs exactly match the pinned oracle");
    } else {
        eprintln!(
            "[oracle] Tier-1 SKIPPED: installed build differs from the pinned oracle \
             (this is a fixture-version note, not a discovery failure)."
        );
    }

    // ---- Tier 2: matcher self-validation (always) ----
    // discover() already required each signature to match uniquely; re-assert
    // each discovered address against its matcher for an explicit, reportable
    // pass/fail per function, and require all RVAs non-zero + distinct.
    let pe = Pe::from_mapped(&image).unwrap();
    let mut dis =
        magos_discovery::disasm::Disassembler::new_x86_64_intel().expect("capstone init");
    let cluster = test_cluster(&pe, &image);
    eprintln!(
        "[oracle] cluster window = [0x{:x}, 0x{:x}), _PRELOAD rva = 0x{:x}",
        cluster.lo, cluster.hi, cluster.preload_str_rva
    );

    let mut distinct = std::collections::HashSet::new();
    let sixteen = table.sixteen();
    for (name, rva) in sixteen {
        assert!(rva != 0, "Tier-2: {name} discovered as 0");
        assert!(
            distinct.insert(rva) || name == "lua_newstate_thunk" || name == "lua_newstate_body",
            "Tier-2: duplicate RVA 0x{rva:x} for {name}"
        );
    }
    // Re-validate each function body against its signature at the discovered RVA.
    assert_match("lua_gettop", table.lua_gettop, &pe, &image, &mut dis, patterns::match_lua_gettop);
    assert_match("lua_atpanic", table.lua_atpanic, &pe, &image, &mut dis, patterns::match_lua_atpanic);
    assert_match("lua_type", table.lua_type, &pe, &image, &mut dis, patterns::match_lua_type);
    assert_match("lua_tolstring", table.lua_tolstring, &pe, &image, &mut dis, patterns::match_lua_tolstring);
    assert_match("lua_createtable", table.lua_createtable, &pe, &image, &mut dis, patterns::match_lua_createtable);
    assert_match("lua_tonumber", table.lua_tonumber, &pe, &image, &mut dis, patterns::match_lua_tonumber);
    assert_match("lua_settop", table.lua_settop, &pe, &image, &mut dis, patterns::match_lua_settop);
    assert_match("lua_pcall", table.lua_pcall, &pe, &image, &mut dis, patterns::match_lua_pcall);
    assert_match_c("lua_pushcclosure", table.lua_pushcclosure, &pe, &image, &mut dis, cluster, |i, c| {
        patterns::match_lua_pushcclosure(i, c)
    });
    assert_match_c("lua_setfield", table.lua_setfield, &pe, &image, &mut dis, cluster, |i, c| {
        patterns::match_lua_setfield(i, c)
    });
    assert_match_c("lua_pushstring", table.lua_pushstring, &pe, &image, &mut dis, cluster, |i, c| {
        patterns::match_lua_pushstring(i, c)
    });
    assert_match_c("luaL_openlibs", table.lual_openlibs, &pe, &image, &mut dis, cluster, |i, c| {
        patterns::match_lual_openlibs(i, c)
    });
    assert_match_body("luaL_loadbuffer", table.lual_loadbuffer, &pe, &image, &mut dis, cluster, |i, p, im, c| {
        patterns::match_lual_loadbuffer(i, p, im, c)
    });
    // newstate body signature + thunk→body link.
    assert_match("lua_newstate_body", table.lua_newstate_body, &pe, &image, &mut dis, patterns::match_lua_newstate_body);
    assert!(
        table.lua_newstate_thunk == table.lua_newstate_body
            || thunk_targets(image.as_slice(), table.lua_newstate_thunk, table.lua_newstate_body),
        "Tier-2: lua_newstate_thunk 0x{:x} must jump to body 0x{:x}",
        table.lua_newstate_thunk,
        table.lua_newstate_body
    );
    eprintln!("[oracle] Tier-2 PASS: all 16 signatures self-validate at their discovered RVAs");

    // ---- Report: full table + shift vs the pinned build (informative) ----
    eprintln!("[oracle] ---- discovered 16 (RVA) ----");
    let mut deltas: Vec<i64> = Vec::new();
    for (name, pinned_rva) in PINNED_SIXTEEN {
        let got = sixteen.iter().find(|(n, _)| *n == name).unwrap().1;
        let delta = got as i64 - pinned_rva as i64;
        eprintln!(
            "[oracle]   {name:<22} 0x{got:08x}  (pinned 0x{pinned_rva:08x}, Δ {delta:+#x})"
        );
        if got != 0 {
            deltas.push(delta);
        }
    }
    if !deltas.is_empty() {
        let min = *deltas.iter().min().unwrap();
        let max = *deltas.iter().max().unwrap();
        eprintln!(
            "[oracle] shift vs pinned: min Δ = {min:+#x}, max Δ = {max:+#x} ({})",
            if min == max {
                "UNIFORM — code block relocated as a unit"
            } else {
                "NON-UNIFORM — investigate"
            }
        );
    }
}

// ---- helpers ----

fn test_cluster(pe: &Pe, image: &[u8]) -> Cluster {
    // Mirror engine::discover_with's cluster derivation.
    let rdata = pe.rdata();
    let rlen = std::cmp::min(rdata.raw_size, rdata.virtual_size) as usize;
    let preload = image
        .get(rdata.rva as usize..rdata.rva as usize + rlen)
        .and_then(|s| s.windows(b"_PRELOAD\x00".len()).position(|w| w == b"_PRELOAD\x00"))
        .map(|off| rdata.rva + off as u32)
        .unwrap_or(0);
    let center = pe.text().rva; // fallback only
    // Use the LuaJIT version string anchor like the engine.
    let mut center = center;
    if let Some(rva) = string_rva(pe, image, b"LuaJIT 2.1.") {
        let mut sites = Vec::new();
        magos_discovery::scan::find_lea_xrefs(pe, image, rva, &mut sites);
        if let Some(&site) = sites.first() {
            if let Some(rf) = pe.find_runtime_function(site) {
                center = rf.begin;
            }
        }
    }
    Cluster {
        lo: center.saturating_sub(0x10_0000),
        hi: center.saturating_add(0x10_0000),
        preload_str_rva: preload,
    }
}

fn string_rva(pe: &Pe, image: &[u8], needle: &[u8]) -> Option<u32> {
    let r = pe.rdata();
    let len = std::cmp::min(r.raw_size, r.virtual_size) as usize;
    let slice = image.get(r.rva as usize..r.rva as usize + len)?;
    let off = slice.windows(needle.len()).position(|w| w == needle)?;
    Some(r.rva + off as u32)
}

fn assert_match(
    name: &str,
    rva: u32,
    pe: &Pe,
    image: &[u8],
    dis: &mut magos_discovery::disasm::Disassembler,
    matcher: fn(&[magos_discovery::disasm::DecodedInsn]) -> bool,
) {
    let (begin, end) = magos_discovery::disasm::body_bounds(pe, rva);
    let insns = dis.disasm_range(image, begin, end);
    assert!(
        !insns.is_empty() && matcher(&insns),
        "Tier-2: signature mismatch for {name} @ 0x{rva:x}"
    );
}

fn assert_match_c(
    name: &str,
    rva: u32,
    pe: &Pe,
    image: &[u8],
    dis: &mut magos_discovery::disasm::Disassembler,
    cluster: Cluster,
    matcher: fn(&[magos_discovery::disasm::DecodedInsn], Cluster) -> bool,
) {
    let (begin, end) = magos_discovery::disasm::body_bounds(pe, rva);
    let insns = dis.disasm_range(image, begin, end);
    assert!(
        !insns.is_empty() && matcher(&insns, cluster),
        "Tier-2: signature mismatch for {name} @ 0x{rva:x}"
    );
}

fn assert_match_body(
    name: &str,
    rva: u32,
    pe: &Pe,
    image: &[u8],
    dis: &mut magos_discovery::disasm::Disassembler,
    cluster: Cluster,
    matcher: fn(
        &[magos_discovery::disasm::DecodedInsn],
        &Pe,
        &[u8],
        Cluster,
    ) -> bool,
) {
    let (begin, end) = magos_discovery::disasm::body_bounds(pe, rva);
    let insns = dis.disasm_range(image, begin, end);
    assert!(
        !insns.is_empty() && matcher(&insns, pe, image, cluster),
        "Tier-2: signature mismatch for {name} @ 0x{rva:x}"
    );
}

fn thunk_targets(image: &[u8], thunk_rva: u32, body_rva: u32) -> bool {
    let p = match image.get(thunk_rva as usize..thunk_rva as usize + 5) {
        Some(p) => p,
        None => return false,
    };
    if p[0] != 0xE9 {
        return false;
    }
    let rel = i32::from_le_bytes([p[1], p[2], p[3], p[4]]);
    (thunk_rva as i64 + 5 + rel as i64) as u32 == body_rva
}

// Smoke: the DiscoverError Display impl renders (keeps the public API honest).
#[test]
fn discover_error_displays() {
    let e = DiscoverError::Anchor("test");
    assert_eq!(format!("{e}"), "method-A anchor 'test' not found");
}

#[ignore] // run with: cargo test -p magos-discovery --test oracle dbg_ -- --nocapture --ignored
#[test]
fn dbg_disasm_predicted() {
    let exe = darktide_exe().expect("binary");
    let file = std::fs::read(&exe).unwrap();
    let image = map_from_file(&file).unwrap();
    let pe = Pe::from_mapped(&image).unwrap();
    let mut dis = magos_discovery::disasm::Disassembler::new_x86_64_intel().unwrap();
    // predicted = pinned + observed uniform shift 0xf0680
    for (name, pinned) in [
        ("lua_setfield", 0xc74cb0u32),
        ("lua_settop", 0xc74f30),
        ("luaL_loadbuffer", 0xc7ad80),
        ("luaL_openlibs", 0xc7f380),
        ("lua_createtable", 0xc73ad0),
        ("lua_tolstring", 0xc75190),
        ("lua_pushstring", 0xc747d0),
        ("lua_tonumber", 0xc730c0),
        ("lua_panic_body", 0x328220),
    ] {
        let rva = pinned + 0xf0680;
        let (begin, end) = magos_discovery::disasm::body_bounds(&pe, rva);
        let insns = dis.disasm_range(&image, begin, end);
        println!("\n=== {name} @ predicted 0x{rva:x} (pinned 0x{pinned:x}) === insns={}", insns.len());
        let lim = if name == "lua_tolstring" { 40 } else { 14 };
        for ins in insns.iter().take(lim) {
            println!("  0x{:x}: {} {}", ins.address, ins.mnemonic, ins.op_str);
        }
        // direct call targets
        let calls: Vec<u64> = insns.iter()
            .filter(|i| i.id == magos_discovery::disasm::X86_INS_CALL)
            .filter_map(|i| i.op_str.trim().strip_prefix("0x").and_then(|h| u64::from_str_radix(h,16).ok()))
            .collect();
        println!("  direct call targets: {:?}", calls.iter().map(|c| format!("0x{c:x}")).collect::<Vec<_>>());
    }
    // Disambiguation aid: print a specific address's body.
    for rva in [0xd90a50u32] {
        let (begin, end) = magos_discovery::disasm::body_bounds(&pe, rva);
        let insns = dis.disasm_range(&image, begin, end);
        println!("\n=== extra @ 0x{rva:x} (begin=0x{begin:x} end=0x{end:x}) insns={} ===", insns.len());
        for ins in insns.iter().take(36) {
            println!("  0x{:x}: {} {}", ins.address, ins.mnemonic, ins.op_str);
        }
    }
}
