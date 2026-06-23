# ADR 0001: Component A Language and Structure

- **Status:** Accepted — Hybrid validated by [Spike 001](../planning/spike-001-component-a-language.md). Step 4/5 hook-capture confirmation pending the timing-fix re-run (expected to pass; see outcome).
- **Date:** 2026-06-23
- **Decides:** The implementation language and internal structure of
  Component A (the injected payload that runs inside `Darktide.exe`).

## Context

Component A is the lowest-level, highest-risk, hardest-to-test
component: it runs inside the game process, hooks the engine's Lua VM,
and bootstraps the modding framework. Its language is the project's
highest-leverage decision — reversing it later means a full rewrite.

Two architectural facts shape the choice (established in planning):

1. **A splits into a pure-library and a thin shell.** The bulk by line
   count is pure computation — PE parsing, disassembly, source-pattern
   matching, address reporting — that is testable offline against a
   binary fixture, with no live game. The thin shell — hook
   installation, VM calls, raw pointer manipulation — can only be
   exercised in the live game.
2. **The safe/unsafe asymmetry.** Rust's safety benefit applies cleanly
   to the pure-library and not at all to the shell (which is irreducibly
   `unsafe` in any language — C++ included — because it manipulates raw
   pointers into a C VM we don't control).

## Decision drivers

- **Testability** is a baseline requirement (every component testable to
  the extent reasonably possible) — not a tradeable attribute.
- **Live-game track record** — game hooking/injection is a mature C/C++
  discipline (MinHook, Detours, PolyHook; 15+ yrs of examples).
- **FFI tax** — calling C from Rust is zero-cost performance-wise; the
  real cost is the `unsafe` surface, ABI struct-matching boilerplate,
  and panic-over-FFI discipline.
- **Build/toolchain** — mixed Rust+C is routine to orchestrate (not an
  architectural concern); MinGW-version alignment on Linux→PE
  cross-compile is the one operational wrinkle.

## Considered options

| Option | Pros | Cons |
|---|---|---|
| **Hybrid: Rust pure-lib + C shell** *(leaning)* | Rust's safety/testability where the logic bulk is; C's native ergonomics + track record exactly where the live-game risk is; smallest `unsafe`-in-Rust surface; shell needs only C (no C++) | Two toolchains; a C-ABI seam to design and hold at the safety boundary; two-language maintenance/review surface |
| **All-Rust** | Single language; safety/testability on the pure-library | Shell is `unsafe`-anyway (benefit doesn't apply where risk is); FFI tax + panic discipline on the live-game bits; thinner niche track record |
| **All-C++** | Matches the validated POC; richest reference; no FFI tax; mature build path (MinGW) | Weaker testability/safety ergonomics on the pure-library; C++ build friction; larger UB surface |
| **All-C** | Lowest FFI friction (entire stack is C); smallest binary; simplest | Weakest abstraction + weakest safety/testing ergonomics; only if minimizing complexity dominates |

## Decision

**Adopt the Hybrid** (Rust pure-library for discovery + C live-game
shell). Spike 001 met the adoption gate — steps 1, 2, 6 pass + safe
surface exceeded — so per the pre-wired tree the Hybrid is chosen.

## Spike 001 outcome

**Adoption gate (met):**
- **Step 1** (mixed Rust+C build) — PASS. MinGW cross-compile + MSVC (CI); valid PE DLL, statically linked, system-DLL-only runtime deps.
- **Step 2** (discovery, full 16-address engine) — PASS. All 16 found; **100% safe Rust** in core logic (1476 lines, 0 `unsafe`); only 5 `unsafe` lines at the C-ABI seam. Exceeded the >90% bar.
- **Step 6** (seam, in-process) — PASS. The C shell drove the Rust `magos_discover` against the live image; all 16 matched the offline oracle exactly. The hybrid-specific integration risk is validated.
- Safe surface — exceeded (>90% → 100%).

**Other steps:**
- **Step 3** (CreateRemoteThread on Proton) — PASS. The spike's #1 unvalidated risk is de-risked: create-suspend-inject-resume worked under Proton; game reached the menu.
- **Step 7** (panic boundary) — PASS (offline); host stable live.
- **Step 4/5** (hook fire + `lua_gettop`) — hook installed at the correct address but didn't fire due to a **timing bug** (launcher resumed the main thread before the worker installed the hook). Fixed via a hook-ready event handshake (commit `3b9692f`); re-run pending. Not an architecture failure — the POC proved the hook fires on the thunk; the spike found the correct shifted address; the only issue was timing.

**Bonus — game-update resilience validated:** the installed binary (SHA `5abdecb9…`) is newer than the pinned oracle (`132eed5f…`); the engine is build-agnostic and found all 16 at uniformly-shifted addresses (+0xf0680 cluster, +0xae50 engine region). This de-risks the "game update breaks discovery" concern from the production risk table.

**Two tempering nuances (inform future maintenance, not the decision):**
1. The source-pattern matchers needed real tuning to be *build-agnostic* (the POC's keyed on specific RVAs/call counts that don't survive an update). `lua_newstate`/`luaL_loadbuffer` couldn't be body-matched at all — they needed the POC's dataflow/anchor traces. The maintenance surface is the signatures; a LuaJIT-version change (rare — static Stingray code) would require re-tuning. Property of the *method*, not the language.
2. `panic=abort` vs `catch_unwind` are mutually exclusive per-build — a real Rust-over-FFI discipline item the seam must enforce. Resolution: unwind build + `catch_unwind` as primary containment, abort as a separate fail-safe profile.

**Production launcher insights (from live testing — feed Component B):**
1. **Proton launch model:** Steam non-Steam-game (the launcher) + forced Proton + `STEAM_COMPAT_DATA_PATH` → our launcher creates+suspends+injects. Steam UX + zero game-dir footprint + correct hook timing in one design (resolves the Steam-UX-vs-zero-footprint tension).
2. **Steam appID:** the launcher must set `SteamAppId=1361210` + `SteamGameId=1361210` in Darktide's env — otherwise the non-Steam-shortcut's hashed appID denies `SteamAPI_Init`.
3. **Hook-ready handshake:** the production "Launch Modded" flow is `CreateProcess(SUSPENDED) → inject → **wait for hook-ready** → ResumeThread` — the production-summary's `CreateProcess→inject→ResumeThread` is incomplete without the hook-ready wait.

## Consequences

- Two toolchains (Rust + C) and a C-ABI seam to maintain; the seam must
  be held at the safety boundary (Rust = stateless discovery, C =
  everything touching the live game) or it rots.
- Rust testability captured on the discovery engine (100% safe,
  offline-tested); C track record captured on the shell (MinHook, LuaJIT C ABI).
- The discovery crate carries forward as production; the minimal C shell
  is expanded into the full production shell (DMF bootstrap, multi-shot
  injection, DMF-dep impls) as the next Component A workstream.
- Signature maintenance: the build-agnostic matchers are the ongoing
  maintenance surface (re-tune on a LuaJIT version change — rare).
- The spike branch (`spike/001-component-a`) is the seed of production
  Component A; merge to `main` once step 4/5 confirm and code review passes.

## References

- [Spike 001](../planning/spike-001-component-a-language.md)
- `docs/poc/production-spec.md` — validated addresses, struct layouts, constraints.
