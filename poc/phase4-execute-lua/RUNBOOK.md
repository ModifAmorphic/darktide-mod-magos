# Phase 5 Step 3 (C-function bootstrap) — Runbook (Tier B, user-executed)

This walks you through installing the dbghelp proxy DLL into your
Darktide install, launching the game, and verifying **the C-function
bootstrap** — registering our `poc_print` C function as a Lua global
via `lua_pushcclosure` + `lua_setfield`, bypassing the sandboxed `_G`.

> **Phase 5 Step 3 is the production-ready fix** for the sandbox finding
> (Phase 4 + Phase 5 Step 2 confirmed `luaL_openlibs` is destructive — it
> crashes the game within 1 second). Instead of restoring the standard
> library globals (which conflicts with the engine's replacements), we
> provide our OWN globals with our OWN names (`poc_print`, not `print`).
> The injected chunk only references our globals, so it doesn't care what
> the engine did to `_G`.

**You do not need to do anything in this document for Tier A** — that
was completed by the coder (see `report.md`). This runbook covers the
**Tier B live-game test**, which only you can run.

Phase 5 Step 3 = Phase 4 (everything Phase 4 included) + the C-function
bootstrap registration + the new `poc_print()` chunk. Same hook
installation, same retry-on-error mechanism. The runtime differences:
`poc_print` is registered as a global before the injection, and the
chunk now calls `poc_print()` instead of using `print`/`io.open`.

---

## What's new in Phase 5 Step 3 (vs Phase 5 Step 2)

Two changes inside the `lua_pcall` detour:

1. **`luaL_openlibs(L)` is now DISABLED** in production (pointer wired
   to NULL in `inject_install`). The openlibs call only fires if the
   pointer is non-NULL, which is now only the case in the A1 test.
   Live testing proved calling openlibs on the engine's state is
   destructive (overwrites the engine's custom globals → crash within
   1 second).
2. **NEW: C-function bootstrap.** Before the injection attempt, the
   detour registers our `poc_print` C function as a Lua global via:
   ```c
   lua_pushcclosure(L, &poc_print, 0);                    // 0xc74580
   lua_setfield(L, LUA_GLOBALSINDEX, "poc_print");        // 0xc74cb0
   ```
   `LUA_GLOBALSINDEX = -10002` in LuaJIT 2.1. This is the same
   mechanism the engine itself uses to set `_G.<name>` globals (its
   init function uses the identical pattern 13 times).
3. **The chunk changed** to:
   ```lua
   poc_print()
   return 42
   ```
   `poc_print()` calls our C function, which writes a fixed string to
   the log file. The chunk only references globals we provide — no
   dependency on `_G.print`, `_G.io`, or anything else the engine
   may have stripped.

Everything else from Phase 4 rev 3 is unchanged: the retry-on-error
mechanism, the reentrancy guard, the max-retry cap. The `inject
targets:` log line now also includes `lua_pushcclosure=0x...c74580
(rva=0xc74580) lua_setfield=0x...c74cb0 (rva=0xc74cb0)`.

---

## Testing workflow (read this first)

You don't tail a log, and you don't paste log lines into chat in real time.
The workflow is:

1. **Build** the DLL (or have the dev-lead's delivered build ready).
2. **Install** it into the game directory (Step 1 below).
3. **Launch** Darktide via Steam.
4. **Play ~30 seconds.** Reach the main menu if you can; that's plenty of
   time for the engine to call `lua_pcall` on the captured state.
5. **Exit** the game cleanly (Alt+F4 / menu → quit).
6. **Tell the dev-lead "done."**

That's it on your side. The dev-lead reads the log file afterward:

```sh
"/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/darktide-poc.log"
```

No `tail -f`. No live coordination. One log file per launch.

---

## Prerequisites

- Darktide installed via Steam on Linux, running through Proton.
- Phases 1, 2b, 3, and 4 succeeded (game reached the main menu and was
  stable for 30s+; discovery reproduced all 7 addresses; `lua_State*`
  captured; `lua_pcall` re-identified at `0xc744c0`).
- The Phase 5 Step 3 DLL already built. If you haven't built it:
  ```sh
  cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase4-execute-lua
  ./build.sh
  ```
  Output: `build/dbghelp.dll` (~3.2 MB, PE-x86-64).
- The Tier A tests pass:
  ```sh
  make a1   # mock-VM injection + C-function bootstrap + offline disasm checks
  make a2   # DLL plumbing smoke under Wine
  ```
  The A1 test now includes **four disassembly-shape checks** — for
  `lua_pcall` (`0xc744c0`), `luaL_openlibs` (`0xc7f380`),
  `lua_pushcclosure` (`0xc74580`), and `lua_setfield` (`0xc74cb0`).
  If A1 fails on any, do NOT proceed — fix the issue first.

---

## Step 1 — Install the proxy into the game directory

If a prior-phase proxy is currently installed, **uninstall it first**:
```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase4-execute-lua
./uninstall.sh
```

Then install Phase 5 Step 3:
```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase4-execute-lua
./install.sh
```

What this does:
1. Backs up the game's shipped `dbghelp.dll` to `…/binaries/dbghelp.dll.orig`
   (refuses to run if a `.orig` already exists — run `uninstall.sh` first).
2. Copies `build/dbghelp.dll` over `…/binaries/dbghelp.dll`.
3. Removes stale `darktide-poc.log` and `darktide-poc-discovery.json`.

If your game is installed elsewhere, set `GAME_DIR`:
```sh
GAME_DIR="/path/to/your/Darktide/binaries" ./install.sh
```

---

## Step 2 — Set the Steam launch option

In Steam, right-click **Warhammer 40,000: DARKTIDE** → **Properties** →
**General** → **Launch Options**, and paste:

```
WINEDLLOVERRIDES="dbghelp=native,builtin" %command%
```

**This must be `native,builtin`, NOT plain `native`.**

---

## Step 3 — Launch the game, play ~30s, exit, say "done"

Launch Darktide through Steam. Reach the main menu (or at least wait long
enough to get past the launcher splash). Play for ~30 seconds total, then
exit the game cleanly.

### What the dev-lead expects to see in the log

The expected sequence:

```
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=<PID> ts=<ISO8601>
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 ...
[darktide-poc] hook lua_newstate installed at 0x...c7c000 (rva=0xc7c000) ...
[darktide-poc] inject targets: lua_pcall=0x...c744c0 (rva=0xc744c0) luaL_loadbuffer=0x...c7ad80 (rva=0xc7ad80) luaL_openlibs=0x...c7f380 (rva=0xc7f380) [DISABLED] lua_pushcclosure=0x...c74580 (rva=0xc74580) lua_setfield=0x...c74cb0 (rva=0xc74cb0) min_interval_ms=500 max_attempts=200 ...
[darktide-poc] inject lua_pcall hook installed at 0x...c744c0 (rva=0xc744c0) ...
[darktide-poc] captured lua_State* = 0x<L> ...
[darktide-poc] lua_gettop(L) = 0 (expected 0 on fresh state) ...
[darktide-poc] discover summary matched=6 mismatched=1 ...
[darktide-poc] cfunctions registered: poc_print                       <-- NEW (Phase 5 Step 3)
[darktide-poc] [FROM LUA] poc_print called — Lua executed a C function!  <-- THE PROOF
[darktide-poc] injected attempt=1 load_rc=0 pcall_rc=0 (0=success) delay_ms=<...> src_len=22 ...
[darktide-poc] DllMain DLL_PROCESS_DETACH pid=<PID> ts=<ISO8601>
```

The three critical signals:

1. **`inject targets: ... lua_pushcclosure=0x...c74580 ... lua_setfield=0x...c74cb0 ...`**
   — confirms the new RVAs are baked in (if missing, you installed the
   old DLL).
2. **`cfunctions registered: poc_print`** — confirms the bootstrap ran.
3. **`[FROM LUA] poc_print called — Lua executed a C function!`** — THE
   PROOF. Paired with `injected attempt=1 ... pcall_rc=0`, this is
   mission accomplished: Lua executed a C function we provided, bypassing
   the sandboxed `_G`.

---

## Success criteria (dev-lead checks these post-hoc)

1. **All prior-phase markers still appear** (no regression):
   - `DllMain forwarders resolved=200 missing=0 total=200`
   - `discover summary matched=6 mismatched=1`
   - `captured lua_State* = 0x<L>`
   - `lua_gettop(L) = 0`
2. **Phase 5 Step 3 markers appear:**
   - `inject targets: ... lua_pushcclosure=0x...c74580 ... lua_setfield=0x...c74cb0 ...`
   - `cfunctions registered: poc_print`
   - `[FROM LUA] poc_print called — Lua executed a C function!`
3. **The diagnostic result (ONE of these two):**
   - **Success:** `injected attempt=N load_rc=0 pcall_rc=0 (0=success) ...`
     AND the `[FROM LUA]` line appears. Phase 5 can proceed to the full
     DMF bootstrap (implement the 6 `Mods.*` globals as C functions, load
     `dmf_loader.lua`).
   - **Failure:** `injected attempt=N load_rc=0 pcall_rc=2 (0=success) ...`
     for ALL attempts (up to the 200-attempt cap, then `giving up after N
     attempts — poc_print never became available`). No `[FROM LUA]` line.
     This means either our pushcclosure or setfield address is wrong (the
     binary moved on a game update) — re-run the offline disasm checks:
     ```sh
     DARKTIDE_EXE="/games/.../Darktide.exe" make a1
     ```
4. **Game reached the main menu and was stable ~30s.** All prior stability
   must be preserved.

---

## Step 4 — Uninstall (restore the original dbghelp)

```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase4-execute-lua
./uninstall.sh
```

Then remove the Steam launch option.

---

## Troubleshooting

### `inject targets:` never appears in the log

Phase 3 or Phase 4's install failed. Check the log for:
- `DllMain phase3_install rc=<nonzero>` — Phase 3 failed; Phase 4/5 were
  correctly skipped. Fix Phase 3 first.
- No `cfunctions registered` line but `inject targets:` appears — the
  detour never fired on the captured state. Wait longer or check if the
  engine calls `lua_pcall` on a different `lua_State*`.

### `cfunctions registered` appears but no `[FROM LUA]` line follows

The registration ran but the chunk didn't execute (latch already set,
rate-limiter, max-retry cap, or pcall errored). Wait longer. If the log
shows `giving up after N attempts — poc_print never became available`,
the chunk failed every time → either our pushcclosure/setfield RVAs are
wrong, or `lua_setfield(L, -10002, ...)` doesn't actually set a global
on this build.

### Game crashes right after `cfunctions registered`

The C-function bootstrap itself crashed. This would be a significant
finding (it means our pushcclosure or setfield call is corrupting the
engine's state). Capture the crash dump from:
`/games/steamapps/compatdata/1361210/pfx/drive_c/users/steamuser/AppData/Roaming/Fatshark/Darktide/crash_dumps/crash_dump-*.dmp`

### `pcall_rc` is a large negative number (not 0 or 2)

The hooked lua_pcall address is wrong (the binary moved on an update).
Re-run the offline disasm checks:
```sh
cd .../phase4-execute-lua
DARKTIDE_EXE="/games/.../Darktide.exe" make a1
```

### Log shows the OLD DLL (no `lua_pushcclosure` in `inject targets:`)

Rebuild and reinstall:
```sh
cd .../phase4-execute-lua && ./build.sh && ./uninstall.sh && ./install.sh
```

---

## What success looks like

- All Phase 1+2b+3+4 markers appear
- `inject targets: ... lua_pushcclosure=0x...c74580 ... lua_setfield=0x...c74cb0 ...`
- `cfunctions registered: poc_print`
- `[FROM LUA] poc_print called — Lua executed a C function!`
- `injected attempt=1 load_rc=0 pcall_rc=0 (0=success) ...`
- Game stable for 30s+

This unblocks Phase 5's full DMF bootstrap: implement the 6 `Mods.*`
dependencies (`Mods.file.dofile`, `Mods.lua.loadstring`, `Mods.lua.io`,
`Mods.require_store`, `Mods.original_require`, `__print`) as C functions
using the same `lua_pushcclosure` + `lua_setfield` pattern, then load
`dmf_loader.lua`.
