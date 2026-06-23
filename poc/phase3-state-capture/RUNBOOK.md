# Phase 3 State Capture — Runbook (Tier B, user-executed)

This walks you through installing the Phase 3 dbghelp proxy DLL into your
Darktide install, launching the game, and verifying the **lua_State*
capture** + **lua_pcall resolution** worked against the live Darktide.exe
process.

**You do not need to do anything in this document for Tier A** — that was
completed by the coder (see `report.md`). This runbook covers the **Tier B
live-game test**, which only you can run.

Phase 3 = Phase 1 (foothold + dbghelp forwarding) + Phase 2a's discovery
engine (cross-check) + Phase 3's MinHook-based hooks (lua_newstate capture
+ lua_pcall resolution).

---

## Prerequisites

- Darktide installed via Steam on Linux, running through Proton.
- Phase 1 and Phase 2b succeeded (game reached the main menu and was
  stable for 30s+, discovery reproduced all 7 addresses). Phase 3 inherits
  both and adds the hook layer; if Phase 2b doesn't work, fix that first.
- The Phase 3 DLL already built. If you haven't built it:
  ```sh
  cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase3-state-capture
  ./build.sh
  ```
  Output: `build/dbghelp.dll` (~3.2 MB, PE-x86-64 — slightly larger than
  Phase 2b's ~3 MB because MinHook is statically linked in addition to
  the engine + capstone).
- The Tier A tests pass:
  ```sh
  make a1       # MinHook hook mechanism (plain + CFG-thunk shapes)
  make a2       # DLL plumbing smoke under Wine
  make disasm   # candidate arg-count analysis
  ```
  If any of these fail, do **not** proceed — fix the issue first.

---

## What's new in Phase 3 (vs Phase 2b)

Phase 2b was a **read-only** discovery: the worker thread scanned the
in-memory module and logged the addresses. No hooks, no function calls
into the engine.

Phase 3 **adds hooks** (installed from DllMain before DllMain returns):

1. `lua_newstate` (thunk @ `0xc7c000`) — detour captures the returned
   `lua_State*` and verifies it via a single `lua_gettop(L)` call
   (expected return 0). Transparent passthrough: the engine sees its own
   `lua_State*` as if nothing happened.
2. Three `lua_pcall` candidates (`0xc748d0`, `0xc74f30`, `0xc754d0`) —
   each detour observes whether its first arg (`L`) matches the captured
   state. The first candidate to fire with a matching `L` is identified
   as `lua_pcall`; the other two are pruned.

The Phase 2b discovery worker runs unchanged as cross-check.

---

## Step 1 — Install the proxy into the game directory

```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase3-state-capture
./install.sh
```

What this does:
1. Backs up the game's shipped `dbghelp.dll` to `…/binaries/dbghelp.dll.orig`
   (refuses to run if a `.orig` already exists — run `uninstall.sh` first).
2. Copies `build/dbghelp.dll` over `…/binaries/dbghelp.dll`.
3. Removes any stale `darktide-poc.log` / `darktide-poc-discovery.json`.

If Phase 2b's proxy is currently installed, **uninstall it first**:
```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase2b-runtime-discovery
./uninstall.sh
```

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

**This must be `native,builtin`, NOT plain `native`.** Same reason as
Phase 1/2b: Wine overrides are keyed on module name, and our proxy needs
to `LoadLibraryW` the real builtin `C:\Windows\System32\dbghelp.dll` for
forwarding. Plain `native` blocks that second load.

---

## Step 3 — Launch the game and watch the log

Open a terminal and tail the log:

```sh
tail -f "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/darktide-poc.log"
```

Launch Darktide through Steam. Within the first few seconds you should
see the **Phase 1 + Phase 2b markers** from prior phases:

```
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=<PID> ts=<ISO8601>
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 pid=<PID> ts=<ISO8601>
```

Then the **Phase 3 hook-install** markers (these run synchronously inside
DllMain, before the worker thread spawns):

```
[darktide-poc] hook install base=0x140000000 size_of_image=0x... pid=<PID> ts=<ISO8601>
[darktide-poc] hook targets: lua_newstate=0x... lua_gettop=0x... cands=[...] pid=<PID> ts=<ISO8601>
[darktide-poc] hook MH_Initialize ok pid=<PID> ts=<ISO8601>
[darktide-poc] hook lua_newstate installed at 0x140c7c000 (rva=0xc7c000) pid=<PID> ts=<ISO8601>
[darktide-poc] hook lua_pcall candidates installed: 0xc748d0 0xc74f30 0xc754d0 (observing) pid=<PID> ts=<ISO8601>
[darktide-poc] DllMain spawned discover_worker thread pid=<PID> ts=<ISO8601>
```

Then the **discovery worker** lines (Phase 2b, cross-check), same as before:

```
[darktide-poc] discover start base=0x140000000 size_of_image=0x... image_base=0x140000000 pid=<PID> ts=<ISO8601>
[darktide-poc] discover lua_newstate_thunk rva=0xc7c000 expected=0xc7c000 MATCH pid=<PID> ts=<ISO8601>
... (all 7 confirmed addresses MATCH)
[darktide-poc] discover summary matched=7 mismatched=0 unresolved=0 pcall=deferred pid=<PID> ts=<ISO8601>
[darktide-poc] discover json written pid=<PID> ts=<ISO8601>
```

Then the **headline Phase 3 capture lines** — these appear when the engine
calls `lua_newstate` (early in main(), typically 1–5 seconds after the
attach line). **This is the headline success signal.** When you see:

```
[darktide-poc] captured lua_State* = 0x<L> pid=<PID> ts=<ISO8601>
[darktide-poc] lua_gettop(L) = 0 (expected 0 on fresh state) pid=<PID> ts=<ISO8601>
```

**Story 3 is delivered.** The state is captured and verified.

Finally, the **lua_pcall resolution** — these appear when the engine first
calls `lua_pcall` (typically within a few more seconds, as the engine
begins running scripts during boot). When you see:

```
[darktide-poc] lua_pcall candidate 0x<candidate> fired L=0x<L> (matches captured state) args=(...)
[darktide-poc] lua_pcall identified at 0x<addr> (winner) pid=<PID> ts=<ISO8601>
[darktide-poc] lua_pcall candidate 0x<other> pruned (other winner) DisableHook=0 pid=<PID> ts=<ISO8601>
```

The lua_pcall address is **resolved dynamically** (Phase 2b could only
defer this — Phase 3 closes the loop).

---

## Success criteria

1. **Phase 1 + Phase 2b markers still appear** (no regression):
   - `DllMain forwarders resolved=200 missing=0 total=200`
   - `discover summary matched=7 mismatched=0`
2. **Phase 3 hook markers appear** (hooks installed before the worker):
   - `hook lua_newstate installed at 0x<abs-addr> (rva=0xc7c000)`
   - `hook lua_pcall candidates installed: 0xc748d0 0xc74f30 0xc754d0 (observing)`
3. **The headline capture lines appear** (Story 3 delivered):
   - `captured lua_State* = 0x<L>`
   - `lua_gettop(L) = 0 (expected 0 on fresh state)`
4. **lua_pcall resolution** (strong bonus; acceptable if it defers — the
   engine might not call pcall during the brief window you watch):
   - `lua_pcall identified at 0x<addr> (winner)`
5. **Game reaches the main menu and is stable ~30s.** Phase 1/2b stability
   must be preserved. If the game crashes during boot, see Troubleshooting.
6. **`darktide-poc-discovery.json` is still written** (discovery worker
   still runs as cross-check). Quick sanity check:
   ```sh
   ls -la "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/darktide-poc-discovery.json"
   # Should be ~43 KB
   ```

On clean exit you should also see:
```
[darktide-poc] DllMain DLL_PROCESS_DETACH pid=<PID> ts=<ISO8601>
```

---

## Headline success signal

The **single most important line** to look for is:

```
[darktide-poc] captured lua_State* = 0x<L> pid=<PID> ts=<ISO8601>
[darktide-poc] lua_gettop(L) = 0 (expected 0 on fresh state) pid=<PID> ts=<ISO8601>
```

If those two lines appear and the game is stable, **Phase 3 passes**.
lua_pcall identification is a strong bonus but not strictly required for
this phase (Phase 4 will exercise it more).

---

## Step 4 — Uninstall (restore the original dbghelp)

```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase3-state-capture
./uninstall.sh
```

Then remove the Steam launch option. To verify the original was restored:
```sh
ls -la "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/dbghelp.dll"
# Expected: ~1.8 MB (the original Microsoft dbghelp), NOT ~3.2 MB (proxy)
```

If you ever want a clean slate and have lost the `.orig` backup, **verify
local files via Steam** (Darktide → Properties → Installed Files → Verify
integrity of game files).

---

## Troubleshooting

### `hook install base=` appears but no `captured lua_State*`

The hooks installed but the engine didn't call `lua_newstate`. This is
almost certainly because **the engine creates its VM before the proxy
loads** — but Phase 3 installs hooks in DllMain before it returns, so
this shouldn't happen unless:

- The proxy loaded into a process other than Darktide.exe (check
  `discover start base=` — is the size_of_image ~630 MB / `0x27db000`?).
- The game binary moved and the thunk at `base + 0xc7c000` is no longer
  `lua_newstate` (you'd see `discover summary matched<7` and a MISMATCH
  on `lua_newstate_thunk`). Re-derive against the new binary.

If `captured lua_State*` appears but the value is NULL or implausible
(not a kernel-space-distance pointer), the engine may have called a
non-LuaJIT lua_newstate shim. Check the disassembly.

### `lua_pcall identified` never appears

The candidates didn't fire with a matching `L` during your observation
window. Possible causes:

- The engine creates multiple Lua VMs and the captured `L` is a different
  one from where pcall runs. Phase 4 will exercise this more.
- The engine boot path didn't reach a pcall before you checked the log.
  Watch for 30+ seconds; the engine calls pcall repeatedly during the
  script-loading phase.

This is **acceptable for Phase 3**. Story 3 is the capture, not pcall
identification. The candidate RVAs are recorded in the log either way.

### `hook lua_newstate CreateHook at thunk 0xc7c000 failed: ...`

MinHook couldn't patch the thunk. The log will show a fallback attempt
on the body at `0xc7eea0`. If that also fails, the binary's CFG layout
has changed. Capture the full log and the new binary's SHA-256.

### Game crashes during boot

Most likely cause: a detour crashed. Look at the last log line before
the crash — was it during `lua_gettop(L) = ...` or a candidate firing?

- If `lua_gettop(L) = ...` was the last line, the captured `L` is bad
  (wrong address). Check `discover lua_newstate_thunk MATCH` — if it's a
  MISMATCH, the binary moved.
- If `lua_pcall candidate ... fired ...` was the last line, the candidate
  is not actually `lua_pcall` and the 4-arg detour shape mismatched.
  (Disassembly says all 3 are SAFE for 4-arg detours, but disassembly
  only looks at the first 24 instructions — a 5+ arg signature could
  hide further in.) If this happens, fall back to the observe-only
  assembly stub (see `phase3_hooks.c` comment).

Capture the Darktide crash dump from:
`/games/steamapps/compatdata/1361210/pfx/drive_c/users/steamuser/AppData/Roaming/Fatshark/Darktide/crash_dumps/crash_dump-*.dmp`

Parse with `python3 -c "from minidump import minidump; m=minidump.Minidump('path.dmp'); print(m.exception)"`.

### `discover summary matched<7` or any `discover ... MISMATCH`

The binary moved. Phase 3's baked-in RVAs (`0xc7c000`, etc.) are pinned
to the analyzed build (SHA-256 `132eed5f…791661`). Re-derive against
the new `Darktide.exe` (re-run Phase 0 / Phase 2a offline), update
`src/phase3_hooks.c` constants + Phase 2b's `expected_addrs.h`,
rebuild, re-test.

### Log shows `failed to load real dbghelp.dll`

Same as Phase 1/2b: the launch option is `dbghelp=native` (plain) and
must be `dbghelp=native,builtin`. See Step 2.

---

## What success looks like

- All 7 confirmed addresses log MATCH (Phase 2b cross-check intact).
- `hook lua_newstate installed at 0x<abs> (rva=0xc7c000)`.
- `captured lua_State* = 0x<L>` (the headline).
- `lua_gettop(L) = 0` (verification).
- `lua_pcall identified at 0x<addr>` (bonus).
- Game reaches the main menu and is stable ~30s.
- Detach line on clean exit.

If those hold, **Phase 3 is PASS.** Proceed to Phase 4 (Lua execution via
`luaL_loadstring` + the resolved `lua_pcall`, using the captured `L`).
