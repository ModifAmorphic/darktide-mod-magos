# Spike 001 — Component A Language & Structure (Hybrid Rust + C)

> **Status:** Approved, not started.
> **Branch:** `spike/001-component-a` — merges to `main` as production if successful.
> **Time box:** ~3–5 days (leans upper — full discovery port).
> **Informs:** `docs/decisions/0001-component-a-language-and-structure.md`
> **Posture:** de-risking spike, not a POC. **Built to production quality
> from the start.** If it validates the architecture, the discovery crate
> becomes the first real production component (reviewed and merged), not
> rewritten. The C shell is a minimal validation slice (small, expected
> rework when the full shell is built post-decision). If the spike fails
> the decision criteria, the branch is discarded.

## Purpose

Validate the single highest-leverage, hardest-to-reverse decision in the
project: the language and internal structure of **Component A** (the
injected payload). Specifically, whether the **Hybrid** option — a Rust
pure-library for discovery + a C shell for everything that touches the
live game — is viable, by testing its load-bearing claims empirically
rather than by argument.

## What's being tested — the Hybrid

A Rust discovery library (compiled to a C-ABI static archive) + a C
live-game shell, linked into one DLL, delivered by `CreateRemoteThread`.
The three claims under test:

1. The mixed Rust+C build produces a working Windows PE DLL, reproducibly.
2. The discovery engine is **safe, offline-testable Rust** (no live game
   needed) — Rust's benefit materializes here, where the logic bulk lives.
3. The two halves integrate at a clean C-ABI seam, in-process, and the
   live-game shell (C) handles hooking + the LuaJIT C API natively.

The Hybrid is favored on paper because it aligns each language with
where its benefit holds: Rust's safety/testability on stateless
computation, C's native ergonomics and domain track record on the
irreducibly-unsafe live-game bits.

## Scope — IS and IS NOT

**IS** — the vertical slice that proves the 7 steps below, with the
**discovery engine built out fully** (all 16 addresses, both discovery
methods, `.pdata` gap-handling) and to production quality.

**IS NOT** (explicitly out of scope): multi-shot injection; DMF
bootstrap; the stubbed DMF dependencies (`Mods.original_require`,
`Mods.lua.io`); the mod-manager UI; profiles / load-order; code signing.
The C shell covers only what's needed to validate the architecture
(hook `lua_newstate`, one VM call, invoke the seam); the full shell is
built post-decision. The spike answers the language/structure question
while delivering the production discovery crate.

## Acceptance criteria — the 7 steps

Ordered so each gates the next, with the cheapest test of the riskiest
assumption first.

| # | Step | Where | Pass | Fail means | Owner |
|---|------|-------|------|-----------|-------|
| 1 | **Build the mixed DLL.** Rust staticlib (C ABI) + C shell → one Windows PE. MinGW cross-compile from Linux; MSVC native on Windows. CI workflows committed. | offline | Valid PE DLL with a `DllMain`, reproducible on both toolchains. | toolchain won't link → Hybrid rejected. | coder |
| 2 | **Discovery as safe, unit-tested Rust — full engine.** Port the POC's complete lookup set (all 16 addresses; Method A string-anchor + Method B source-pattern; `.pdata` gap-handling for CFG thunks / leaf / import thunks) to safe Rust. Tested against the POC's `addresses.json` + the 16-address table in `docs/poc/production-spec.md` as the oracle. Measure safe-vs-unsafe line ratio. | offline (no game) | All 16 addresses match the oracle; core discovery logic is **>90% safe Rust** with passing tests. | Rust benefit doesn't materialize → consider All-C / All-C++. | coder |
| 3 | **CreateRemoteThread delivery.** Launcher: `CreateProcess(SUSPENDED)` → inject → `ResumeThread`; `DllMain` runs; zero game-dir files. Validated on Windows native **and** Proton. | live | DLL attaches; game reaches main menu; zero footprint; both platforms. | injection fails / crashes / Proton broken. | user (runbook) |
| 4 | **Hook `lua_newstate`, capture `lua_State*`.** C shell installs MinHook on the thunk; handler fires; `L` captured and valid. | live | Hook fires; `L` non-null/valid (log). | hooking in C fails (unexpected — proven path). | user (runbook) |
| 5 | **Exercise the LuaJIT C ABI.** One call, e.g. `lua_gettop(L)`, confirming the documented struct offsets. | live | Call returns expected value; offsets confirmed. | ABI/offset mismatch in C shell. | user (runbook) |
| 6 | **Seam integration, in-process.** C shell reads the live image from the module base, passes bytes to the Rust `discover()` C-API, gets the address table back. | live | All 16 addresses match the oracle against the **live** image. | Seam design is wrong — **not** that Rust is wrong. | user (runbook) |
| 7 | **Panic boundary.** `panic = "abort"` on the Rust side; an induced panic in the pure-lib is contained — no UB crossing into C. | offline (mechanism) + live (confirm) | Panic terminates cleanly; no host corruption. | Panic discipline insufficient. | coder (+ user confirm) |

**Sequencing logic:** steps 1–2 are offline and prove the novel/risky
half cheaply before any live-game work — so a failure there rejects the
Hybrid without wasting live effort. Steps 3–6 are the live track,
converging at 6. Step 7 is cross-cutting. Step 2 does not hard-depend on
3; it is ordered first deliberately for fail-fast. A fail at 1 or 2
rejects the Hybrid cheaply; a fail at 6 indicts the *seam design*, not
Rust.

## Execution model — who does what

The spike has a hard split between autonomous-offline and human-live
work (the live game can't be driven by a subagent):

- **Coder** (offline): steps 1, 2, 7-mechanism; produces the integrated
  build + a **runbook** for steps 3–6; reports the safe/unsafe ratio.
- **User** (live): executes steps 3–6 + 7-confirm via the runbook,
  reports results/logs.
- **QA**: reviews offline test coverage (step 2), structures the live
  runbook so steps 3–6 are reproducible, validates pass/fail evidence.

## Decisions (confirmed)

1. **Binary fixture.** Offline tests resolve the local install via a
   `DARKTIDE_GAME_DIR` env var (binary SHA `132eed5f…`); the binary is
   **never committed**. A small synthetic PE is used only to unit-test
   the PE-parsing machinery in isolation.
2. **Spike location / promotion.** Work happens on branch
   `spike/001-component-a`. **Assume it merges to `main` as production
   if successful** — so code is written to production quality (tested,
   reviewable) from the start. The discovery crate carries forward
   fully; the minimal C shell is expected to be expanded post-decision.
3. **Safe-surface threshold.** Step 2 passes when >90% of the core
   discovery logic is safe Rust with passing tests.
4. **Discovery scope.** The **full POC lookup set** (all 16 addresses,
   both methods, gap-handling), validated against the POC `addresses.json`
   oracle — not a minimal sample. Reason: we have working reference code
   and a known-good oracle, so the port is low-risk and the output is
   the production discovery engine, not throwaway.

## Deliverables (on `spike/001-component-a`)

- Discovery crate (Rust) — production quality, full 16-address engine,
  tested against the oracle.
- C shell (minimal validation slice) + launcher.
- Live runbook (steps 3–6) for the user.
- Offline test results + measured safe/unsafe ratio.
- User-filled live result log.
- Findings feed **ADR-0001** (decision + rationale filled in from outcomes).

## Risks

- Fail at 1 or 2 → Hybrid rejected cheaply; reconsider All-C / All-C++.
- Fail at 6 → seam design flaw; revise seam or reconsider All-Rust.
- CreateRemoteThread-on-Proton failure (step 3) is a **delivery**
  problem, independent of language; the proxy-DLL + `WINEDLLOVERRIDES`
  fallback is the documented mitigation and does **not** block this ADR.

## Reference

- `docs/poc/production-spec.md` — validated addresses, struct layouts, constraints.
- `docs/poc/lua-vm-injection-anchors.md` — PE layout, discovery methodology.
- `docs/decisions/0001-component-a-language-and-structure.md` — the ADR this informs.
