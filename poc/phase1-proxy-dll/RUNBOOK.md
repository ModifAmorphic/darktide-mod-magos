# Phase 1 Proxy DLL — Runbook (Tier B, user-executed)

This walks you through installing the Phase 1 dbghelp proxy DLL into your
Darktide install, launching the game, and verifying the foothold worked.

**You do not need to do anything in this document for Tier A** — that
was completed by the coder (see `report.md`). This runbook covers the
**Tier B live-game test**, which only you can run.

---

## Prerequisites

- Darktide installed via Steam on Linux, running through Proton.
- The proxy DLL already built. If you haven't built it:
  ```sh
  cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase1-proxy-dll
  ./build.sh
  ```
  The output is `build/dbghelp.dll` (~220 KB, PE-x86-64). The Tier A
  Wine test should also have been run:
  ```sh
  ./test/run_wine_test.sh
  ```
  If that fails, do **not** proceed — fix the proxy first.

---

## Step 1 — Install the proxy into the game directory

```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase1-proxy-dll
./install.sh
```

What this does:
1. Backs up the game's shipped `dbghelp.dll` to
   `…/binaries/dbghelp.dll.orig` (refuses to run if a `.orig` already
   exists — run `uninstall.sh` first to clear it).
2. Copies `build/dbghelp.dll` over `…/binaries/dbghelp.dll`.
3. Removes any stale `darktide-poc.log` from a prior run.

If your game is installed somewhere other than the default
(`/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/`), set
`GAME_DIR` first:
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

**Why this override, and why `native,builtin` (not plain `native`).**
Wine's DLL override controls how it resolves modules by *name*. The
override is keyed on the module name (`dbghelp`) and applies to
**every** load of that name — it does **not** matter whether the load
uses a bare name or an absolute path. (An earlier version of this
runbook claimed the absolute-path load "bypasses the override"; that is
incorrect, and plain `native` fails precisely because of this.)

We have a two-sided load requirement:

- **The engine** loads `dbghelp.dll` by bare name. With `native,builtin`,
  Wine searches native first, finds our proxy in the application
  directory (`binaries/`), and loads it. ✓
- **Our proxy** then calls `LoadLibraryW(L"C:\\Windows\\System32\\dbghelp.dll")`
  to get the real implementation for forwarding. That file **is Wine's
  builtin**. Plain `native` forbids loading it (builtin disallowed), so
  the proxy's DllMain logs `failed to load real dbghelp.dll` and all
  forwarding is broken. With `native,builtin`, the builtin is allowed
  and the load succeeds. ✓

The override must list both: `native` so the engine gets our proxy,
`builtin` so our proxy can reach the real Wine dbghelp. If you
previously tried plain `native` and saw the `failed to load real
dbghelp.dll` error, switching to `native,builtin` is the fix.

---

## Step 3 — Launch the game and watch the log

Open a terminal and start tailing the log:

```sh
tail -f "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/darktide-poc.log"
```

Now launch Darktide through Steam as usual.

### Success criteria

You should see, within a second or two of clicking **Play**:

```
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=<PID> ts=<ISO8601>
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 pid=<PID> ts=<ISO8601>
```

- `pid=<PID>` should match the Darktide.exe process ID
  (check with `pgrep -af Darktide.exe` from another terminal).
- `ts=…` should be the current UTC time.
- `resolved=200 missing=0` confirms every forwarded export resolved
  against the real (Wine builtin) dbghelp.

If you see those two lines and the game reaches the **main menu without
crashing**, **Phase 1 passes.** Play for ~30 seconds (start mission
loading, etc.) to exercise Aftermath callbacks and confirm stability.

When you exit the game, you should also see:
```
[darktide-poc] DllMain DLL_PROCESS_DETACH pid=<PID> ts=<ISO8601>
```

---

## Step 4 — Uninstall (restore the original dbghelp)

When you're done testing, restore the original game file:

```sh
cd $HOME/repos/ModifAmorphic/darktide-mod-magos/poc/phase1-proxy-dll
./uninstall.sh
```

Then remove (or revert) the Steam launch option. The launch option is
harmless without the proxy installed (Wine just falls back to builtin),
but you should remove it to keep Steam launch options clean.

To verify the original was restored:
```sh
ls -la "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/dbghelp.dll"
# Expected: ~1.8 MB (the original Microsoft dbghelp), NOT ~220 KB (proxy)
```

If you ever want a clean slate and have lost the `.orig` backup,
**verify local files via Steam** (Darktide → Properties → Installed
Files → Verify integrity of game files) to restore the original.

---

## Troubleshooting

### Game crashes immediately on launch

Most likely cause: **a forwarded export is wrong or missing**, and the
engine/Aftermath crashed on the first dbghelp call. Things to check:

1. **Did the attach log line appear at all?**
   - **No.** Our proxy never loaded. Check:
     - Is `dbghelp.dll` in `binaries/` actually our proxy? (`ls -la`
       should show ~220 KB, not 1.8 MB.)
     - Is the Steam launch option set correctly? Try
       `WINEDLLOVERRIDES="dbghelp=native,builtin" %command%` as a
       fallback.
   - **Yes, but `missing=N` (N > 0).** Some exports didn't resolve
     against the Wine builtin. This is unexpected — capture the log
     and the output of
     `winedump -j export /usr/lib/wine/x86_64-windows/dbghelp.dll`
     and report it.

2. **Attach line appears, game crashes after the splash/menu.**
   This points at a forwarded call misbehaving (most likely Aftermath
   hitting one of `SymFunctionTableAccess64`, `StackWalk64`,
   `SymGetLineFromAddrW64`, or `SymGetModuleBase64`). To narrow it
   down:
   - Temporarily comment out Aftermath by renaming
     `…/binaries/GFSDK_Aftermath_Lib.x64.dll` to `.bak`. If the crash
     disappears, Aftermath is the trigger — capture which export it
     hit (see wine crash log below).
   - The crash is almost certainly not in our forwarder code (the
     fast path is a 6-instruction tail jump), but in the Wine builtin
     dbghelp being asked for a feature it doesn't fully implement.
     That's a Wine-version quirk, not a proxy bug.

3. **Wine crash log.** Proton writes crash logs to:
   ```
   ~/.steam/steam/steamapps/compatdata/<APPID>/pfx/drive_c/users/steamuser/AppData/Local/Temp/
   ```
   Darktide's Steam AppID is `13612`. Look for files matching
   `*.crash` or `*.log` modified around the time of the crash.
   `winedbg --gdb` attached to the live process is the more thorough
   option if you can reproduce.

### Log shows `failed to load real dbghelp.dll`

This means the proxy's `LoadLibraryW(L"C:\\Windows\\System32\\dbghelp.dll")`
was rejected. The confirmed cause is the **Wine override**:

- If your launch option is `dbghelp=native` (plain native, no builtin),
  Wine forbids loading the builtin dbghelp from System32 — and that file
  *is* the builtin. The fix is to use `dbghelp=native,builtin` (see
  Step 2). This is the expected fix; plain `native` cannot work for our
  two-sided load.

If you are already on `native,builtin` and still see this error, then:

- Check the Proton prefix's
  `drive_c/windows/system32/dbghelp.dll` exists (it should be a Wine
  marker file or symlink to the builtin).
- Confirm the override is actually reaching the process — run
  `winecfg` in the same Proton prefix and verify the per-application
  override for `dbghelp` lists both `native` and `builtin` (the env var
  and winecfg settings can interact).

### Log shows DllMain lines but `resolved=200 missing=0` is wrong

If `missing` is anything other than 0, some exports couldn't be
resolved against the real dbghelp. This could mean:
- You're running this proxy on **native Windows** (not via Proton),
  where the Microsoft dbghelp doesn't export `wine_get_module_info`
  (the 200th Wine-specific export). That single `missing=1` is
  harmless — no caller uses it.
- A different Wine version with a different builtin dbghelp export
  set. Compare the export lists (see `report.md` for the method).

---

## What success looks like

- Log file appears at game launch with the attach line.
- Game reaches the main menu without crashing.
- 30+ seconds of gameplay (or sitting at the menu) without a crash.
- Detach line appears on clean game exit.

If all four hold, **Phase 1 is PASS.** Proceed to Phase 2 (function
discovery) on a separate branch.
