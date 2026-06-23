# ADR 0001: Component A Language and Structure

- **Status:** Proposed — pending [Spike 001](../planning/spike-001-component-a-language.md)
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

**Pending Spike 001 results.** Pre-wired decision tree, so the outcome
fills in the choice without re-litigation:

- **Steps 1, 2, 6 pass + safe surface high (>90%)** → adopt the **Hybrid**.
- **1/2 pass but 6 fails** → the *seam design* is flawed, not Rust;
  revise the seam, or fall back to **All-Rust**.
- **Step 2 fails (thin safe surface)** → Rust's benefit doesn't
  materialize where claimed → adopt **All-C** or **All-C++**.
- **Step 3 fails on Proton** → a delivery problem, independent of
  language; proxy-DLL fallback applies and does **not** block this ADR.

## Consequences (anticipated — confirm post-spike)

- *If Hybrid adopted:* two toolchains and a C-ABI seam to maintain; the
  seam must be held at the safety boundary (Rust = stateless discovery,
  C = everything touching the live game) or it rots; Rust testability
  captured on the discovery engine; C track record captured on the shell.
- *If All-C/C++ adopted:* single language, no seam; testability of the
  discovery engine rests on that language's testing ergonomics.

## References

- [Spike 001](../planning/spike-001-component-a-language.md)
- `docs/poc/production-spec.md` — validated addresses, struct layouts, constraints.
