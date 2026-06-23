# Phase 5 (DMF bootstrap) — Runbook (Tier B, user-executed)

This walks you through installing the dbghelp proxy DLL into your
Darktide install, launching the game, and verifying **the DMF bootstrap** —
the DLL registers the 6 DMF dependencies as C functions / Lua tables via
the LuaJIT C API, then loads `dmf_loader.lua` from the staging directory.

> **Phase 5 = Phase 4 (everything Phase 4 included) + the full DMF
> dependency surface + dmf_loader.lua loading.** Same hook installation,
> same retry-on-error mechanism. The runtime differences: `Mods` table +
> `__print` are registered before the injection, and the injected chunk
> calls `Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")` which loads
> the real DMF loader.

**You do not need to do anything in this document for Tier A** — that
was completed by the coder (see `report.md`). This runbook covers the
**Tier B live-game test**, which only you can run.

---

## What's new in Phase 5 (vs Phase 5 Step 3)

Three changes inside the `lua_pcall` detour:

1. **DMF bootstrap.** Before the injection attempt, the detour registers
   the **6 DMF dependencies** as C functions / Lua tables via the C API:

   ```c
   _G.__print               = c_print       (writes args to log file)
   _G.Mods.file.dofile      = c_dofile      (reads + execs a .lua file)
   _G.Mods.lua.loadstring   = c_loadstring  (compiles a Lua source)
   _G.Mods.lua.io           = {}             (empty table; minimal POC)
   _G.Mods.require_store    = {}             (empty table; DMF populates)
   _G.Mods.original_require = c_require_stub (logs + returns nil)
   ```

   Built via `lua_createtable` + `lua_pushcclosure` + `lua_setfield`
   (Phase 5 newly-identified RVAs: `lua_tolstring=0xc75190`,
   `lua_createtable=0xc73ad0`, plus `lua_type=0xc753b0`,
   `lua_tonumber=0xc730c0` for completeness).

2. **New bootstrap chunk.** The injected Lua chunk is now:

   ```lua
   return Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")
   ```

   This calls our `c_dofile` C function, which reads
   `dmf_loader.lua` from the staging directory and executes it via
   `luaL_loadbuffer` + `lua_pcall`. The loader itself uses
   `Mods.file.dofile` to load DMF's modules.

3. **Staging directory resolution.** The DLL reads the staging directory
   from the `DARKTIDE_MOD_STAGING` env var. If unset, it derives a default
   from the engine's module path (`<Darktide.exe>/../../mods` — the
   game's `mods/` directory, where the live install puts DMF source).
   `c_dofile` resolves relative paths against this staging dir and
   appends `.lua` (matching the original mod_loader's `get_file_path`
   behavior).

Everything else from Phase 4 rev3 + Phase 5 Step 3 is unchanged: the
retry-on-error mechanism, the reentrancy guard, the max-retry cap. The
`inject targets:` log line now also includes the new RVAs.

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
- Phases 1, 2b, 3, 4, and 5 Step 3 succeeded (game reached the main menu
  and was stable for 30s+; discovery reproduced all 7 addresses;
  `lua_State*` captured; `lua_pcall` re-identified at `0xc744c0`;
  `poc_print` C function registered and called from Lua).
- The Phase 5 DLL already built. If you haven't built it:
  ```sh
  cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase5-dmf-bootstrap
  ./build.sh
  ```
  Output: `build/dbghelp.dll` (~3.2 MB, PE-x86-64).
- The Tier A tests pass:
  ```sh
  make a1   # mock-VM DMF bootstrap + offline disasm checks (8 total)
  make a2   # DLL plumbing smoke under Wine
  ```
  The A1 test now includes **eight disassembly-shape checks** — the 4
  from Phase 5 Step 3 plus 4 new ones for Phase 5 (`lua_tolstring`,
  `lua_createtable`, `lua_type`, `lua_tonumber`). It also loads the
  REAL `dmf_loader.lua` from the game install via `c_dofile` and verifies
  Story 6 (loader begins executing without immediate error).
- The DMF source tree exists at
  `/games/steamapps/common/Warhammer 40,000 DARKTIDE/mods/dmf/`
  (this is the default path the DLL resolves when
  `DARKTIDE_MOD_STAGING` is unset).

---

## Step 1 — Install the proxy into the game directory

If a prior-phase proxy is currently installed, **uninstall it first**:
```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase5-dmf-bootstrap
./uninstall.sh
```

Then install Phase 5:
```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase5-dmf-bootstrap
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

## Step 3 — (Optional) Set DARKTIDE_MOD_STAGING

By default, the DLL looks for DMF source at the game's `mods/` directory
(derived from the engine's module path). If you want to use a different
staging directory, set this in the Steam launch option:

```
DARKTIDE_MOD_STAGING="/path/to/your/staging" WINEDLLOVERRIDES="dbghelp=native,builtin" %command%
```

The staging dir must contain `dmf/scripts/mods/dmf/dmf_loader.lua` and the
`dmf/scripts/mods/dmf/modules/` tree (same layout as the game install's
`mods/` directory).

---

## Step 4 — Launch the game, play ~30s, exit, say "done"

Launch Darktide through Steam. Reach the main menu (or at least wait long
enough to get past the launcher splash). Play for ~30 seconds total, then
exit the game cleanly.

### What the dev-lead expects to see in the log

The expected sequence:

```
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=<PID> ts=<ISO8601>
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 ...
[darktide-poc] hook lua_newstate installed at 0x...c7c000 (rva=0xc7c000) ...
[darktide-poc] inject targets: lua_pcall=0x...c744c0 ... lua_pushcclosure=0x...c74580 ... lua_setfield=0x...c74cb0 ... lua_tolstring=0x...c75190 ... lua_createtable=0x...c73ad0 ... lua_gettop=0x...c74050 ... staging=Z:/games/.../mods ...
[darktide-poc] inject lua_pcall hook installed at 0x...c744c0 ...
[darktide-poc] captured lua_State* = 0x<L> ...
[darktide-poc] lua_gettop(L) = 0 ...
[darktide-poc] discover summary matched=6 mismatched=1 ...
[darktide-poc] DMF bootstrap: registered Mods table + __print (staging=Z:/games/.../mods)    <-- NEW (Phase 5)
[darktide-poc] [c_dofile] reading <staging>/dmf/scripts/mods/dmf/dmf_loader.lua              <-- c_dofile fired
[darktide-poc] [FROM LUA __print] DMF:Initializing basic mod hook system...                  <-- dmf_loader executed!
[darktide-poc] injected attempt=1 load_rc=0 pcall_rc=0 (0=success) delay_ms=<...> ...        <-- Story 6 PASS
[darktide-poc] DllMain DLL_PROCESS_DETACH pid=<PID> ts=<ISO8601>
```

The four critical signals:

1. **`inject targets: ... lua_tolstring=0x...c75190 ... lua_createtable=0x...c73ad0 ... staging=...`**
   — confirms the new Phase 5 RVAs are baked in AND the staging dir was
   resolved correctly.
2. **`DMF bootstrap: registered Mods table + __print`** — confirms the
   C-function bootstrap ran and built the Mods table.
3. **`[c_dofile] reading .../dmf_loader.lua`** — confirms the bootstrap
   chunk called `Mods.file.dofile`, which entered our C function.
4. **`[FROM LUA __print] DMF:Initializing basic mod hook system...`** +
   **`injected attempt=N load_rc=0 pcall_rc=0`** — THE PROOF. dmf_loader.lua
   executed far enough to print its init banner (which happens inside
   `init_mod_framework`, called when the engine loads mods). Paired with
   `pcall_rc=0`, this is mission accomplished.

> **Note:** the dmf_loader.lua's top-level code (the part that runs during
> our pcall) only defines functions and returns the `dmf_mod_object`. The
> actual mod hook banner (`DMF:Initializing basic mod hook system...`)
> prints when `init_mod_framework()` runs — which is called by the engine
> LATER (when it loads the bundle system). For Story 6, we just need the
> LOADER itself to load + execute without immediate errors. The "banner
> log line" above is the BONUS case where everything chains through. The
> MINIMUM success signal is just `pcall_rc=0` paired with the `[c_dofile]`
> line.

---

## Success criteria (dev-lead checks these post-hoc)

1. **All prior-phase markers still appear** (no regression):
   - `DllMain forwarders resolved=200 missing=0 total=200`
   - `discover summary matched=6 mismatched=1`
   - `captured lua_State* = 0x<L>`
   - `lua_gettop(L) = 0`
   - `cfunctions registered: poc_print` (Phase 5 Step 3 — retained)
2. **Phase 5 markers appear:**
   - `inject targets: ... lua_tolstring=0x...c75190 lua_createtable=0x...c73ad0 ...`
   - `DMF bootstrap: registered Mods table + __print`
   - `[c_dofile]` log lines (c_dofile fired)
3. **The diagnostic result (ONE of these two):**
   - **Success (Story 6 PASS):** `injected attempt=N load_rc=0 pcall_rc=0`
     AND at least one `[c_dofile]` line. Phase 5 DMF bootstrap is proven
     end-to-end against the live game.
   - **Failure:** `injected attempt=N load_rc=0 pcall_rc=2` for ALL
     attempts (up to the 200-attempt cap, then `giving up after N
     attempts — dmf_loader never loaded`). The most likely causes:
     - `Mods` setup didn't run (check for `DMF bootstrap: registered`
       line — if missing, one of the C API pointers was wrong)
     - `c_dofile` couldn't read dmf_loader.lua (check the `[c_dofile]`
       log lines — if it says "failed to read", the staging dir is wrong)
     - dmf_loader.lua's body errored (check for `[c_dofile] pcall failed`
       log lines — the loader may depend on globals we don't yet provide)
4. **Game reached the main menu and was stable ~30s.** All prior stability
   must be preserved.

---

## Step 5 — Uninstall (restore the original dbghelp)

```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase5-dmf-bootstrap
./uninstall.sh
```

Then remove the Steam launch option.

---

## Troubleshooting

### `inject targets:` never appears in the log

Phase 3 or Phase 4's install failed. Check the log for:
- `DllMain phase3_install rc=<nonzero>` — Phase 3 failed; Phase 4/5 were
  correctly skipped. Fix Phase 3 first.
- No `DMF bootstrap` line but `inject targets:` appears — the detour
  never fired on the captured state. Wait longer.

### `DMF bootstrap` appears but no `[c_dofile]` line follows

The bootstrap registered `Mods` + `__print` but the chunk didn't call
`Mods.file.dofile`. Likely the chunk source was wrong (check the
`src_len` field in the `injected` line — should be ~60 bytes for the
default bootstrap chunk).

### `[c_dofile] failed to read .../dmf_loader.lua`

The staging directory doesn't contain `dmf/scripts/mods/dmf/dmf_loader.lua`.
Either:
- The default path resolution failed (check the `staging=` field in
  `inject targets:`). Set `DARKTIDE_MOD_STAGING` explicitly.
- The game install doesn't have DMF installed (verify by `ls
  <staging>/dmf/scripts/mods/dmf/dmf_loader.lua`).

### `[c_dofile] pcall failed for .../dmf_loader.lua`

The loader's body errored. This is expected if DMF depends on globals we
don't yet provide (engine globals like `Managers`, `CLASS`, etc. that are
only available later in the engine's init). Wait longer (the retry
mechanism will retry as the engine initializes more). If it persists for
200 attempts, we hit the cap — note this for Phase 6 (we may need to
provide more globals or change injection timing).

### Game crashes right after `DMF bootstrap`

The C-function bootstrap itself crashed the engine. This would be a
significant finding (means our setup corrupted the engine's state).
Capture the crash dump from:
`/games/steamapps/compatdata/1361210/pfx/drive_c/users/steamuser/AppData/Roaming/Fatshark/Darktide/crash_dumps/crash_dump-*.dmp`

### `pcall_rc` is a large negative number (not 0 or 2)

The hooked lua_pcall address is wrong (the binary moved on an update).
Re-run the offline disasm checks:
```sh
cd .../phase5-dmf-bootstrap
DARKTIDE_EXE="/games/.../Darktide.exe" make a1
```

---

## What success looks like

- All Phase 1+2b+3+4+5 Step 3 markers appear
- `inject targets: ... lua_tolstring=0x...c75190 lua_createtable=0x...c73ad0 ...`
- `DMF bootstrap: registered Mods table + __print`
- `[c_dofile]` log line(s)
- `injected attempt=1 load_rc=0 pcall_rc=0`
- Game stable for 30s+

This completes the POC: the Lua VM Injection approach can not only execute
arbitrary Lua in the engine's VM (Phase 4) but also bootstrap the full
DMF framework (Phase 5). Phase 6 (results doc) will document the outcome;
production work (replacing the proxy DLL with CreateRemoteThread,
cross-platform testing, AV compatibility, etc.) can proceed.
