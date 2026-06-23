# Spike 001 — Live Runbook (steps 3–6 + 7-confirm)

> The offline steps (1 build, 2 discovery, 7-mechanism) are done by the coder.
> This runbook is what **you (the user)** run against the live game to validate
> steps 3–6. The coder cannot drive the live game. **Do not commit the game
> binary or anything under `_local/`.**

## What you're validating

The Hybrid architecture: a Rust discovery staticlib + a C shell, linked into
one DLL (`magos_shell.dll`), delivered by `CreateRemoteThread`. On attach the
shell calls the Rust `magos_discover` seam against the live `Darktide.exe`
image, logs all 16 discovered addresses, hooks `lua_newstate`, and calls
`lua_gettop` once.

## Prerequisites

- A Darktide install. Resolve its path via `_local/DARKTIDE.env`:
  ```sh
  source _local/DARKTIDE.env
  echo "$DARKTIDE_GAME_DIR"   # e.g. /games/steamapps/common/Warhammer 40,000 DARKTIDE/
  ```
- The coder already ran `cargo test -p magos-discovery` green against this
  install (Tier-2 matcher self-validation: all 16 found). The live run confirms
  the *same* engine produces the *same* 16 addresses **in-process** against the
  loader-mapped image (step 6), plus the hook + VM call (steps 4–5).

## Build (offline, reproducible)

From the repo root on a Linux box with `x86_64-w64-mingw32-gcc` + a Rust
`x86_64-pc-windows-gnu` target:

```sh
source _local/DARKTIDE.env   # not required for build, only for tests
make build                   # -> magos_shell.dll, magos_launcher.exe
make check                   # verifies a valid PE DLL with a DllMain
```

`make check` must print `CHECK PASS: valid PE DLL with DllMain`. The DLL's only
runtime deps are Windows system DLLs (KERNEL32/ntdll/USERENV/WS2_32/bcrypt +
the UCRT) — no `magos_discovery.dll`, no `libgcc`. Copy `magos_shell.dll` and
`magos_launcher.exe` to your Windows/Proton machine.

---

## Step 3 — CreateRemoteThread delivery (Windows native + Proton)

**Zero game-dir footprint**: the DLL loads from a staging path you choose, NOT
from inside the game directory. Nothing is written under `$DARKTIDE_GAME_DIR`.

### 3a. Windows native

```bat
:: Stage the DLL somewhere outside the game dir, e.g. C:\magos\
magos_launcher.exe  "C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40,000 DARKTIDE\binaries\Darktide.exe"  "C:\magos\magos_shell.dll"
```

The launcher: `CreateProcess(SUSPENDED)` → `VirtualAllocEx` + `WriteProcessMemory`
(the DLL path) → `CreateRemoteThread(LoadLibraryA, dllpath)` → wait → `ResumeThread`.
`DllMain` runs on the remote thread before the engine's `main()`, so the
`lua_newstate` hook is in place before the VM is created.

**Pass criteria:** the game launches and reaches the main menu (no crash); a
`magos_spike.log` appears beside `Darktide.exe` (or at `$MAGOS_LOG_FILE`).

### 3b. Linux / Proton

`CreateRemoteThread` works under Wine/Proton's process management, but Proton
prefixes differ per user. Run the launcher **inside the game's Proton prefix**:

```sh
# Find the prefix (usually ~/.steam/steam/steamapps/compatdata/1361210/pfx)
PFX="$HOME/.steam/steam/steamapps/compatdata/1361210/pfx"

# Run the Windows launcher under the prefix's wine64, with the DLL staged
# outside the game dir (e.g. ~/magos/magos_shell.dll).
WINEPREFIX="$PFX" WINEESYNC=1 WINEFSYNC=1 \
  "$PFX/drive_c/windows/system32/wine64" \
  ~/magos/magos_launcher.exe \
  "$DARKTIDE_GAME_DIR/binaries/Darktide.exe" \
  ~/magos/magos_shell.dll
```

(Steam must be told to use the same Proton prefix — launch via Steam with
`STEAM_COMPAT_DATA_PATH=$PFX` if running outside Steam's own launcher.)

**Proton fallback (documented, not a blocker):** if `CreateRemoteThread` misbehaves under
Proton, the proxy-DLL + `WINEDLLOVERRIDES="dbghelp=native,builtin"` approach (POC phase 1)
is the proven fallback. It is a *delivery* problem, independent of the Hybrid
language decision — see ADR-0001.

**Pass criteria (both platforms):** game reaches main menu; `magos_spike.log`
exists; **no files written under `$DARKTIDE_GAME_DIR`** (verify with
`find "$DARKTIDE_GAME_DIR" -newermt '-5 min' -type f` — should be empty/unchanged).

---

## Step 4 — Hook `lua_newstate`, capture `lua_State*`

Open `magos_spike.log`. The worker thread logs, in order:

```
[magos] step6: discovery OK. 16 addresses (RVAs): ...
[magos] step4: lua_newstate hook installed at 0x... (detour 0x...)
[magos] step4: lua_newstate hook fired; L = 0x...
```

**Pass criteria:** the `lua_newstate hook fired` line appears (the hook
intercepted the engine's VM creation) **and** `L` is a non-null pointer. The
hook is installed on the discovered `lua_newstate_thunk` RVA, so this also
proves the discovered thunk address is correct.

If you see `step4: lua_newstate returned NULL` or the line never appears, the
thunk RVA is wrong or the hook didn't take — capture the full log and report.

---

## Step 5 — Exercise the LuaJIT C ABI (`lua_gettop`)

Immediately after the `L` capture, the detour calls `lua_gettop(L)` via the
discovered `lua_gettop` RVA (cast to `int (*)(lua_State*)`) and logs:

```
[magos] step5: lua_gettop(L) = 0 (expect 0 for fresh state)
```

**Pass criteria:** `lua_gettop(L) == 0`. For a freshly-created state `top ==
base`, so `(top - base) >> 3 == 0`. A non-zero value means the documented
`lua_State` offsets (`base` @ `0x10`, `top` @ `0x18`, LJ_64 non-GC64) don't
match this build — an ABI/offset mismatch to investigate.

---

## Step 6 — Seam integration, in-process (all 16 vs the live image)

The `step6: discovery OK` block lists all 16 RVAs as discovered **in-process**
from the loader-mapped module base (`GetModuleHandle(NULL)`). This is the same
Rust `discover()` the offline oracle test runs — but here against the live
image, through the C-ABI seam, from the C shell.

**Pass criteria:** all 16 addresses are non-zero and match the offline oracle
output (run `cargo test -p magos-discovery --test oracle -- --nocapture` and
compare the `discovered 16` table to the log's `step6` block). They must be
identical: same engine, same binary, same RVAs.

A mismatch here is significant: it would mean the **seam design** (passing the
live module base as `&[u8]` to the Rust engine) is flawed — **not** that Rust is
wrong. Report it distinctly (per the spike's fail-fast note: a step-6 failure
indicts the seam, not the language).

---

## Step 7-confirm — panic boundary (live)

The offline mechanism is verified by the coder: `cargo test -p magos-discovery
--lib panic_` shows an induced panic is caught at the C-ABI boundary
(`magos_test_panic_boundary` returns the sentinel, no abort, no UB). The live
confirm is simply: **the game runs to the main menu without a host crash**
during steps 3–6. If the pure-library panicked across the boundary, the process
would either abort (panic=abort build) or receive `MAGOS_ERR_PANIC` and log it
— either way, no silent UB/corruption.

(You can also call `magos_test_panic_boundary(1)` from a tiny test harness if
you want a live demonstration of the catch; it returns `0x7FFFFFB7`.)

---

## Filling the live result log

Append your results to `docs/poc/spike-001-live-results.md` (create it):

```
## Live results — <date>, build SHA <sha>, platform <windows|proton>
- Step 3: PASS/FAIL — <notes>, game-dir footprint check: <clean/changed>
- Step 4: PASS/FAIL — L = 0x..., hook fired: yes/no
- Step 5: PASS/FAIL — lua_gettop(L) = <n>
- Step 6: PASS/FAIL — all 16 match offline oracle: yes/no (list any mismatch)
- Step 7-confirm: PASS/FAIL — host stable through the run
```

Attach `magos_spike.log`. These results feed ADR-0001.
