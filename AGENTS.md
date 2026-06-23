# AGENTS.md — darktide-mod-magos Project Guide

> Orientation document for any agent or developer starting work on
> this project. Read this first.

## What This Project Is

**darktide-mod-magos** is a new modding application for Warhammer 40,000:
Darktide. It replaces the existing community modding toolchain
(dtkit-patch + Darktide-Mod-Loader + Darktide-Mod-Framework harness)
with a DLL-injection-based approach that eliminates game-directory
footprint and bundle-database fragility.

A POC has been completed and validated on both Linux/Proton and
Windows native. The project is transitioning from POC to production
development.

## Directory Structure

```
darktide-mod-magos/
├── AGENTS.md                              ← You are here
├── .gitignore
├── docs/
│   ├── reference/                         ← Background on the existing modding ecosystem
│   │   ├── darktide-framework-analysis.md     How current Darktide modding works (not our work)
│   │   └── analysis-verification.md           Verification audit of the above
│   ├── poc/                               ← POC documentation (approach, evidence, results)
│   │   ├── lua-vm-injection-theory.md         Architecture and approach (10 sections)
│   │   ├── lua-vm-injection-anchors.md        Technical evidence: 16 confirmed addresses, PE layout
│   │   ├── lua-vm-injection-poc.md            POC plan: 6 user stories (historical record)
│   │   ├── lua-vm-injection-poc-results.md    POC outcomes: all stories passed
│   │   └── poc-postmortem.md                  Goals vs outcomes, lessons learned, gaps
│   ├── DEPLOYMENT_OPTIONS_SURVEY.md       ← Why Lua VM Injection was chosen (decision rationale)
│   ├── production-summary.md              ← High-level next steps (orientation)
│   └── production-spec.md                 ← Detailed technical spec for production (grounded in POC)
├── poc/                                   ← POC code (disposable, but contains validated implementations)
│   ├── phase0-offline-discovery/             Python offline discovery script
│   ├── phase1-proxy-dll/                     Proxy DLL with dbghelp export forwarding
│   ├── phase2-runtime-discovery/             Portable C discovery engine + capstone
│   ├── phase2b-runtime-discovery/            DLL integration (discovery worker thread)
│   ├── phase3-state-capture/                 MinHook, lua_newstate hook, lua_State capture
│   ├── phase4-execute-lua/                   lua_pcall hook, retry-on-error, Lua execution
│   └── phase5-dmf-bootstrap/                 C-function bootstrap, Mods table, DMF loading
└── .agents/                               ← Empty (was used for inter-session communication during POC)
```

## Reading Order

For a new agent starting production work, read in this order:

1. **`docs/production-summary.md`** — High-level overview of what was
   proven and what needs to be built. Start here.
2. **`docs/production-spec.md`** — Detailed technical grounding for each
   work item, tied to POC findings and confirmed addresses.
3. **`docs/poc/poc-postmortem.md`** — Honest assessment of what went
   right, what went wrong, and what remains unproven. Read before
   making architectural assumptions.
4. **`docs/poc/lua-vm-injection-theory.md`** — Full architecture
   document. Read the sections relevant to your work item.
5. **`docs/poc/lua-vm-injection-anchors.md`** — Reference for function
   addresses, PE layout, and discovery methodology. Consult when
   implementing discovery or hooks.
6. **`docs/reference/darktide-framework-analysis.md`** — Background on
   the existing modding toolchain. Read to understand DMF compatibility
   requirements and what's being replaced.
7. **`docs/DEPLOYMENT_OPTIONS_SURVEY.md`** — Why this approach was
   chosen. Read if questioning architectural decisions.

## Key Technical Facts

- **Approach:** DLL injection into Darktide.exe → runtime function
  discovery → hook `lua_newstate` → capture `lua_State*` → C-function
  bootstrap → load DMF from staging directory
- **No anti-cheat:** Darktide has server-side EAC only (no client-side
  kernel-mode scanner). DLL injection is safe today.
- **Sandboxed `_G`:** The engine's default Lua environment does NOT
  expose standard library functions. Solved via C-function bootstrap
  (register C functions as Lua globals via `lua_pushcclosure` +
  `lua_setfield`).
- **16 function addresses confirmed** (all verified at runtime in the
  live game). See `docs/poc/lua-vm-injection-anchors.md` §9.
- **Binary SHA-256:**
  `132eed5fe58515774a41199269dd240ef6092f84b1efc8ad4a28e23ea6791661`
  (all addresses are for this binary version)
- **POC validated on both Linux/Proton and Windows native.**
- **Production uses `CreateRemoteThread` injection** (zero game-directory
  footprint). The POC used a proxy DLL shortcut. The DLL internals are
  identical either way.

## Architecture Decisions Locked

1. Lua VM Injection over Bundle Virtualization
2. C-function bootstrap (not `luaL_openlibs` — destructive)
3. Source-pattern matching for function discovery (not dynamic heuristics)
4. Retry-on-error timing (not precise hook-point timing)
5. DMF Lua files preserved as-is; harness fully replaced

## POC Phase Summary

| Phase | What Was Proven | Key Output |
|-------|----------------|------------|
| 0 | Offline function discovery methodology | 7 addresses, 6 methodology gaps |
| 1 | DLL injection into the game process | Proxy DLL with 200 export forwarders |
| 2 | Runtime function discovery in live process | `matched=7 mismatched=0` |
| 3 | lua_State pointer capture | `lua_gettop(L)=0`, state verified |
| 4 | Arbitrary Lua code execution | `return 42` — `load_rc=0 pcall_rc=0` |
| 5 | DMF bootstrap via C-function bootstrap | `dmf_loader.lua` loaded, game reached main menu |

All phases passed Tier A (offline/mock tests) and Tier B (live game)
on both Linux/Proton and Windows.
