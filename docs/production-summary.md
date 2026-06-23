# Production Summary and Next Steps

> What the POC proved, what the production app needs to do, and where
> to start. This is the orientation document for moving from POC to
> product.

---

## What We Proved

The POC validated the **Lua VM Injection** approach end to end:

- A DLL injected into the Darktide process can **discover LuaJIT
  functions at runtime** (no hardcoded addresses)
- It can **capture the engine's `lua_State*`** by hooking `lua_newstate`
- It can **execute arbitrary Lua code** in the game's VM
- It can **bootstrap DMF** via C-function registration, bypassing the
  engine's sandboxed `_G`
- The game runs **stable** with the DLL active — full startup to main
  menu, engine hooks working
- The **bundle system is completely bypassed** — no
  `bundle_database.data` patching, no format fragility

16 function addresses were discovered and confirmed. The sandboxed
environment was diagnosed and solved. The approach is viable.

---

## What the Production App Needs to Do

The production app is a **mod manager** that:

1. **Manages a staging directory** outside the game folder — mods, DMF,
   load order, configuration all live here
2. **Launches the game modded** — injects the DLL via `CreateRemoteThread`
   (zero game-directory footprint)
3. **Stays out of the way for vanilla play** — launching from Steam
   directly runs the unmodified game, no cleanup needed
4. **Provides mod management UX** — load order, enable/disable, profiles,
   dependency resolution, conflict detection

The DLL itself (the injection payload) does the technical work proven in
the POC: runtime discovery, hook installation, DMF bootstrap, mod
loading. The mod manager app wraps this in a user-friendly launcher with
management features.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│  MOD MANAGER APP (user-facing)                               │
│                                                               │
│  • Staging directory management                               │
│  • Mod browser (Nexus / Thunderstore integration?)            │
│  • Load order UI (drag-and-drop, not text file editing)       │
│  • Dependency resolution                                      │
│  • Profile management                                         │
│  • "Launch Modded" button                                     │
│                                                               │
│  On "Launch Modded":                                          │
│  1. Prepare staging dir (mods, DMF, load order)               │
│  2. Set DARKTIDE_MOD_STAGING env var                          │
│  3. CreateProcess(Darktide.exe, CREATE_SUSPENDED)             │
│  4. Inject DLL from staging via CreateRemoteThread             │
│  5. ResumeThread                                              │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  INJECTED DLL (inside Darktide.exe)                           │
│                                                               │
│  Everything proven in the POC:                                │
│  • Runtime function discovery (string anchors + source        │
│    pattern matching)                                          │
│  • Hook lua_newstate → capture lua_State*                     │
│  • Hook lua_pcall → retry-on-error injection                  │
│  • C-function bootstrap → register Mods table + __print       │
│  • Load dmf_loader.lua from staging                           │
│  • DMF initializes → hooks game functions → loads user mods   │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  STAGING DIRECTORY (outside game folder)                      │
│                                                               │
│  • dmf/ — DMF framework Lua files                             │
│  • mods/ — user mods                                          │
│  • mod_load_order.txt — load order                            │
│  • hook DLL — the injected payload                            │
└─────────────────────────────────────────────────────────────┘
```

---

## Key Decisions Already Made

These were locked during research/POC and shouldn't need revisiting:

1. **Lua VM Injection over Bundle Virtualization** — eliminates
   `bundle_database.data` fragility entirely
2. **C-function bootstrap** — DMF's 6 dependencies registered as C
   functions, bypassing the sandboxed `_G`
3. **Source-pattern matching** for function discovery — the reliable
   identification method (dynamic "first-to-fire" heuristics are not)
4. **DMF Lua files stay, harness replaced** — only the DMF framework
   Lua code is preserved; everything else (dtkit-patch, mod_loader,
   base loader, trampoline) is replaced by the DLL + mod manager
5. **Retry-on-error timing** — the injected chunk self-checks for
   readiness and retries on the engine's `lua_pcall` calls

---

## What Needs to Be Built

### Tier 1: Core (minimum viable mod manager)

- [ ] **CreateRemoteThread injection** — replace the proxy DLL with
      launcher-based injection. Zero game-directory footprint. Must
      work on Windows native and Linux/Proton. **Note: the proxy DLL
      approach is validated on BOTH platforms (Linux/Proton + Windows
      native). CreateRemoteThread replaces the delivery mechanism; the
      DLL internals are unchanged.**
- [ ] **Full DMF dependency implementation** — the POC stubbed
      `Mods.original_require` (returns nil) and `Mods.lua.io` (empty
      table). Production needs real implementations.
- [ ] **Load order management** — replace `mod_load_order.txt` editing
      with a UI. Read/write the file format DMF expects.
- [ ] **Staging directory management** — install/uninstall mods into
      staging, manage profiles.
- [ ] **Game path detection** — find the Darktide install automatically
      (Steam app ID 1361210, same logic as dtkit-patch).

### Tier 2: Mod Management UX

- [ ] **Mod enable/disable** — toggle mods in the load order without
      deleting files
- [ ] **Dependency resolution** — parse mod metadata, detect missing
      dependencies, auto-order
- [ ] **Conflict detection** — analyze which functions each mod hooks,
      flag overlaps
- [ ] **Mod profiles** — save/switch between mod configurations
- [ ] **Mod versioning** — track installed versions, detect updates

### Tier 3: Polish

- [ ] **Mod browser** — integrate with Nexus Mods API or Thunderstore
- [ ] **Auto-update** — check for mod updates, download, install
- [ ] **Code signing** — sign the DLL and launcher to reduce AV flags
- [ ] **Cross-platform support** — Windows native, Linux/Proton,
      potentially Steam Deck
- [ ] **Game-update detection** — detect when Darktide updates, warn
      if discovery might need adjustment

---

## Known Risks for Production

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Fatshark enables client-side EAC | Low | Fatal (kills injection) | Bundle Virtualization is the documented fallback |
| AV flags the injected DLL | High | Support burden | Code signing, AV vendor whitelisting, user education |
| Game update breaks discovery | Low | Mods don't load until DLL updated | String anchors are engine-framework code (`stingray::` namespace), not game content — they only change in a major engine overhaul, not content patches. LuaJIT 2.1 is statically linked and pinned to the engine build. Same engine base runs Vermintide 2; architecture has been stable for years. Recovery path: re-run discovery tool against new binary. |
| Proton CreateRemoteThread quirks | Medium | Linux launch fails | WINEDLLOVERRIDES (proxy DLL) as fallback delivery mechanism |
| DMF compatibility issues | Low-Medium | Some mods don't work | Needs testing with real user mods, not just DMF bootstrap |

---

## Suggested Starting Point

**Start with Tier 1: CreateRemoteThread injection.** This is the one
thing the POC didn't test that matters for the production goal (zero
footprint). Everything else in Tier 1 builds on the POC's proven
foundation. If CreateRemoteThread works on both platforms, the core
architecture is validated and the rest is product development.

The POC's DLL code (discovery engine, hooks, C-function bootstrap) is
reusable as-is. The proxy DLL delivery is replaced by CreateRemoteThread
delivery. The mod manager app is new development.

---

## Reference Documents

| Document | Purpose |
|----------|---------|
| `docs/poc/lua-vm-injection-theory.md` | Architecture and approach (10 sections) |
| `docs/poc/lua-vm-injection-anchors.md` | Technical evidence: PE layout, 16 confirmed addresses, discovery methodology |
| `docs/poc/lua-vm-injection-poc.md` | The POC plan (6 user stories) |
| `docs/poc/lua-vm-injection-poc-results.md` | POC outcomes (all stories PASS) |
| `docs/poc/poc-postmortem.md` | Honest assessment: goals vs outcomes, what went wrong, what remains unproven |
| `docs/DEPLOYMENT_OPTIONS_SURVEY.md` | Landscape survey (why we chose Lua VM Injection) |
| `docs/reference/analysis-verification.md` | Verification of the original framework analysis |
