# Phase 2b Runtime Discovery — Runbook (Tier B, user-executed)

This walks you through installing the Phase 2b dbghelp proxy DLL into your
Darktide install, launching the game, and verifying the runtime discovery
worked against the live Darktide.exe module.

**You do not need to do anything in this document for Tier A** — that was
completed by the coder (see `report.md`). This runbook covers the **Tier B
live-game test**, which only you can run.

Phase 2b = Phase 1 (foothold + dbghelp forwarding) + Phase 2a's discovery
engine linked in, run from a worker thread against the live game module.

---

## Prerequisites

- Darktide installed via Steam on Linux, running through Proton.
- Phase 1 succeeded (the game reached the main menu with the proxy DLL in
  place, 60s stable). Phase 2b inherits Phase 1's stability and adds a
  discovery worker thread; if Phase 1 doesn't work, fix that first.
- The Phase 2b DLL already built. If you haven't built it:
  ```sh
  cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase2b-runtime-discovery
  ./build.sh
  ```
  Output: `build/dbghelp.dll` (~3 MB, PE-x86-64 — larger than Phase 1's
  ~220 KB because the engine + capstone are statically linked).
- The Tier A tests pass:
  ```sh
  make a1          # in-memory engine correctness against real Darktide.exe
  make a2          # DLL plumbing smoke test under Wine
  make iat-test    # IAT-corruption regression (should-fix #5)
  make p2a-verify  # Phase 2a still 28/28 after the should-fixes
  ```
  If any of these fail, do **not** proceed — fix the issue first.

---

## Step 1 — Install the proxy into the game directory

```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase2b-runtime-discovery
./install.sh
```

What this does:
1. Backs up the game's shipped `dbghelp.dll` to `…/binaries/dbghelp.dll.orig`
   (refuses to run if a `.orig` already exists — run `uninstall.sh` first).
2. Copies `build/dbghelp.dll` over `…/binaries/dbghelp.dll`.
3. Removes any stale `darktide-poc.log` / `darktide-poc-discovery.json`.

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

**This must be `native,builtin`, NOT plain `native`.** The override applies
to every load of `dbghelp` by name. We have a two-sided load:

- **The engine** loads `dbghelp.dll` by bare name → Wine searches native
  first, finds our proxy in `binaries/`, loads it. (`native` ✓)
- **Our proxy** then `LoadLibraryW`s `C:\Windows\System32\dbghelp.dll` for
  forwarding → that file IS Wine's builtin → `builtin` must be allowed,
  otherwise the proxy logs `failed to load real dbghelp.dll` and forwarding
  is broken.

Plain `native` (without `builtin`) breaks the second load. This is the bug
the Phase 1 runbook documents; carry the fix forward.

---

## Step 3 — Launch the game and watch the log

Open a terminal and tail the log:

```sh
tail -f "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/darktide-poc.log"
```

Launch Darktide through Steam. Within a few seconds of clicking **Play**
(the worker thread runs after `DllMain` returns, and discovery takes a
moment for the ~15 MB `.text` capstone disasm), you should see:

```
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=<PID> ts=<ISO8601>
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 pid=<PID> ts=<ISO8601>
[darktide-poc] DllMain spawned discover_worker thread pid=<PID> ts=<ISO8601>
[darktide-poc] discover start base=0x140000000 size_of_image=0x... image_base=0x140000000 pid=<PID> ts=<ISO8601>
[darktide-poc] discover lua_panic_body rva=0x328220 expected=0x328220 MATCH pid=<PID> ts=<ISO8601>
[darktide-poc] discover init_begin rva=0x32a660 expected=0x32a660 MATCH pid=<PID> ts=<ISO8601>
[darktide-poc] discover lua_newstate_thunk rva=0xc7c000 expected=0xc7c000 MATCH pid=<PID> ts=<ISO8601>
[darktide-poc] discover lua_newstate_body rva=0xc7eea0 expected=0xc7eea0 MATCH pid=<PID> ts=<ISO8601>
[darktide-poc] discover lua_atpanic rva=0xc77f40 expected=0xc77f40 MATCH pid=<PID> ts=<ISO8601>
[darktide-poc] discover lua_gettop rva=0xc74050 expected=0xc74050 MATCH pid=<PID> ts=<ISO8601>
[darktide-poc] discover luaL_loadbuffer rva=0xc7ad80 expected=0xc7ad80 MATCH pid=<PID> ts=<ISO8601>
[darktide-poc] discover lua_pcall outcome=deferred summary=deferred (surveyed 64 cands, top score 90) pid=<PID> ts=<ISO8601>
[darktide-poc] discover summary matched=7 mismatched=0 pcall=deferred pid=<PID> ts=<ISO8601>
[darktide-poc] discover json written pid=<PID> ts=<ISO8601>
[darktide-poc] discover done pid=<PID> ts=<ISO8601>
```

### Success criteria

1. **DllMain attach** + `forwarders resolved=200 missing=0` appear (Phase 1
   forwarding still works inside the integrated DLL).
2. **`discover summary matched=7 mismatched=0 pcall=<outcome>`** appears.
3. **Each of the 7 confirmed addresses logs MATCH.** Any MISMATCH means
   the build moved (the baked-in constants are pinned to the analyzed
   Darktide.exe SHA-256 `132eed5f…791661`).
4. **`darktide-poc-discovery.json` is written** next to the DLL. Quick
   sanity check:
   ```sh
   ls -la "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/darktide-poc-discovery.json"
   # Should be ~43 KB
   python3 -c "import json; d=json.load(open('/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/darktide-poc-discovery.json')); print('cat_b:', [(c['name'], c['candidate_rvas']) for c in d['category_b_candidates']])"
   ```
5. **Game reaches main menu and is stable ~30s.** Phase 1's stability must
   be preserved (the worker runs once, out of band, and exits; it does not
   hook anything or run again). If the game crashes during the discovery
   window (first few seconds), see Troubleshooting.

On clean exit you should also see:
```
[darktide-poc] DllMain DLL_PROCESS_DETACH pid=<PID> ts=<ISO8601>
```

If all five hold, **Phase 2b passes.** The discovered addresses (in the log
and JSON) are what Phase 3's hooks will target.

---

## Step 4 — Uninstall (restore the original dbghelp)

```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase2b-runtime-discovery
./uninstall.sh
```

Then remove the Steam launch option. To verify the original was restored:
```sh
ls -la "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/dbghelp.dll"
# Expected: ~1.8 MB (the original Microsoft dbghelp), NOT ~3 MB (proxy)
```

If you ever want a clean slate and have lost the `.orig` backup, **verify
local files via Steam** (Darktide → Properties → Installed Files → Verify
integrity of game files).

---

## Troubleshooting

### `discover start` never appears

The worker thread didn't spawn or crashed before its first log line.
- Confirm `DllMain spawned discover_worker thread` is in the log. If not,
  `CreateThread` failed — check `DllMain ERROR CreateThread failed err=...`.
- If it spawned but `discover start` is missing, the worker crashed
  immediately. Capture the full log; the most likely cause is an
  implausible `base` / `e_lfanew` (shouldn't happen —
  `GetModuleHandleW(NULL)` is reliable).

### All 7 addresses log UNRESOLVED (not MATCH)

Discovery ran but found nothing. This means the worker scanned a module
that ISN'T Darktide.exe. Check the `discover start base=...` line:
- If `base=0x140000000 size_of_image=0x...` is implausibly small (a few
  hundred KB), the proxy loaded into something other than the game (e.g.
  a launcher). Confirm the host process is `Darktide.exe`
  (`pgrep -af Darktide.exe`).

### Some addresses log MISMATCH

The baked-in expected addresses are pinned to the analyzed Darktide.exe
build. A MISMATCH means **the game updated** and addresses shifted. This
is not a Phase 2b bug — it's the signal that the build moved. Re-derive
the addresses against the new binary (re-run Phase 0 / Phase 2a offline
against the new `Darktide.exe`), update `expected_addrs.h`, rebuild, and
re-test. Runtime discovery itself is what makes the approach survive
updates: even with MISMATCH against the old constants, the discovered
`rva=...` values in the log and JSON are the CURRENT addresses Phase 3
should hook.

### Game crashes during the discovery window

The worker thread must not touch the loader lock. If you suspect it did
(unexpected `LoadLibrary` / `GetModuleHandle` on an unrelated module),
check that the built DLL only imports from kernel32/user32/msvcrt —
nothing else:
```sh
x86_64-w64-mingw32-objdump -p build/dbghelp.dll | grep 'DLL Name'
```
Expected: `dbghelp.dll` (we export it), `KERNEL32.dll`, `USER32.dll`,
`msvcrt.dll`. Any other DLL is a regression.

### Log shows `failed to load real dbghelp.dll`

Same cause as Phase 1: the launch option is `dbghelp=native` (plain) and
must be `dbghelp=native,builtin`. See Step 2.

---

## What success looks like

- All 7 confirmed addresses log MATCH.
- `discover summary matched=7 mismatched=0 pcall=deferred`.
- `darktide-poc-discovery.json` written (~43 KB) with the 7 addresses in
  `category_b_candidates` / `init_candidate`.
- Game reaches the main menu and is stable ~30s.
- Detach line on clean exit.

If all five hold, **Phase 2b is PASS.** Proceed to Phase 3 (hooks).
