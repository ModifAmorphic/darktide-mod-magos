# Phase 1 — Process Foothold via dbghelp Proxy DLL

**Status:** Tier A PASS (coder-verified). Tier B pending (live-game run by user, see `RUNBOOK.md`).

**Scope:** Prove that a proxy `dbghelp.dll`, dropped into the game's
`binaries/` directory, loads into the Darktide process at startup,
forwards the engine's dbghelp calls to the real (Wine builtin) dbghelp
without breaking anything, and writes a deterministic "my code ran"
side effect to disk. No hooks, no Lua, no function discovery — this is
foothold-only.

---

## Summary

- Built a 220 KB `pei-x86-64` `dbghelp.dll` that forwards all **200**
  exports of the Wine builtin dbghelp via dynamic `LoadLibraryW` +
  tail-jump thunks.
- Standalone Wine test passes: proxy loads, forwarded `SymGetOptions`
  and `SymInitialize` calls return sane values through the thunks, and
  `darktide-poc.log` is written next to the DLL with PID + ISO-8601
  timestamp + the literal `[darktide-poc]` marker on both attach and
  detach.
- `install.sh` / `uninstall.sh` round-trip verified in a sandbox:
  backup integrity confirmed by sha256, no-clobber rule enforced.

---

## Tier A result — PASS

### Build

```
$ ./build.sh
[build] generating stubs from /usr/lib/wine/x86_64-windows/dbghelp.dll ...
gen_stubs: wrote 200 exports -> src/
[build] compiling with x86_64-w64-mingw32-gcc ...
[build] OK: build/dbghelp.dll (pei-x86-64)
[build] exports: built=200 reference=200
```

Toolchain: `x86_64-w64-mingw32-gcc (GCC) 16.1.0`, `wine-11.11`,
reference DLL `/usr/lib/wine/x86_64-windows/dbghelp.dll` (Wine builtin,
200 named exports).

### Standalone Wine test

```
$ ./test/run_wine_test.sh
[test] building host.exe ...
[test] running host.exe under /usr/bin/wine ...
[host] loading dbghelp.dll ...
[host] OK: dbghelp.dll loaded at 00006FFFFABE0000
[host] OK: forwarded SymGetOptions() returned 0x2
[host] OK: forwarded SymInitialize(NULL,NULL,FALSE) returned 1
[host] looking for log at: Z:\home\...\test\build\darktide-poc.log
[host] OK: log contains attach marker.
[host] log content:
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=32 ts=2026-06-21T05:32:06Z
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 pid=32 ts=2026-06-21T05:32:06Z

[host] PASS: all checks succeeded
[test] host.exe exit code: 0
[test] log analysis: attach=1 detach=1 marker=3
[test] log content:
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=32 ts=2026-06-21T05:32:06Z
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 pid=32 ts=2026-06-21T05:32:06Z
[darktide-poc] DllMain DLL_PROCESS_DETACH pid=32 ts=2026-06-21T05:32:06Z

[test] PASS: all Tier A checks succeeded
```

**What this proves:**

1. The proxy loads as `dbghelp.dll` via the standard DLL search order
   (the test host's `LoadLibraryW(L"dbghelp.dll")` resolves to our
   proxy because it's in the current directory).
2. The proxy's `DllMain` runs, loads the real builtin from
   `C:\Windows\System32\dbghelp.dll` (absolute path → no recursion into
   ourselves), and resolves all 200 forwarded exports eagerly.
3. Forwarding works end-to-end: `SymGetOptions` (no args) returns
   `0x2`; `SymInitialize(NULL, NULL, FALSE)` (3 args, exercises
   RCX/RDX/R8) returns `TRUE` — both consistent with calling the Wine
   builtin directly, proving argument registers survive the tail jump.
4. The log side effect fires deterministically with the exact format
   mandated by the spec:
   `[darktide-poc] <event> pid=<PID> ts=<ISO8601>`.

### Stub fast-path disassembly (verified)

`x86_64-w64-mingw32-objdump -d --disassemble=SymInitialize build/dbghelp.dll`:

```asm
SymInitialize:
   sub    $0x28,%rsp                 ; allocate shadow space
   mov    g_fwd_SymInitialize(%rip),%rax   ; load cached ptr (RAX, not an arg reg)
   test   %rax,%rax
   je     .resolve                   ; cache miss — only if DllMain failed
   add    $0x28,%rsp                 ; restore stack
   jmp    *%rax                      ; TAIL JUMP to real function
.resolve:
   lea    "SymInitialize"(%rip),%rcx ; only the slow path clobbers RCX
   call   fwd_resolve
   mov    %rax,g_fwd_SymInitialize(%rip)
   test   %rax,%rax
   jne    .tail_jump
   add    $0x28,%rsp                 ; bail cleanly if unresolvable
   ret
```

The fast path is 6 instructions, preserves all argument registers
(RCX/RDX/R8/R9/XMM0-3) and the caller's shadow space — the forwarded
function sees exactly the frame it would have seen if called directly.

---

## Load-critical dbghelp imports

Identified via `x86_64-w64-mingw32-objdump -p` on the live game files
in `/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/`.

### `Darktide.exe` — 13 static imports (these MUST forward correctly)

| Symbol | Hint |
|---|---|
| `EnumerateLoadedModules64` | 0x0005 |
| `ImageDirectoryEntryToDataEx` | 0x0015 |
| `StackWalk64` | 0x002b |
| `SymCleanup` | 0x0034 |
| `SymFromAddrW` | 0x005a |
| `SymFunctionTableAccess64` | 0x0064 |
| `SymGetLineFromAddrW64` | 0x006f |
| `SymGetModuleBase64` | 0x007f |
| `SymGetModuleInfoW64` | 0x0083 |
| `SymInitializeW` | 0x00a9 |
| `SymLoadModuleEx` | 0x00ac |
| `SymSetOptions` | 0x00c7 |
| `UnDecorateSymbolNameW` | 0x00e3 |

All 13 are present in both the Wine builtin and the game's shipped
Microsoft `dbghelp.dll` (verified by name against both export tables).

### `GFSDK_Aftermath_Lib.x64.dll` — 0 static imports

Aftermath does **not** statically import dbghelp. The POC spec
hypothesized that Aftermath calls `SymFunctionTableAccess64` during
normal startup; the live binary shows no static dbghelp import and no
embedded `Sym*` / `StackWalk*` string references. Aftermath almost
certainly resolves dbghelp dynamically at runtime via
`LoadLibrary` + `GetProcAddress` (only when an actual crash occurs),
which our full-export forwarding covers transparently — any name it
asks for resolves to a working forwarder.

This is a **correction to the theory doc §4 / POC spec**: Aftermath is
not the primary load-critical dbghelp consumer. The engine itself is,
via the 13 static imports above. The forwarding still has to work, but
the failure mode the spec worried about (Aftermath crashing on first
callback) is not actually a concern at startup.

---

## Export count — built vs. reference

| | Count | Source |
|---|---|---|
| **Reference** | 200 | Wine builtin `/usr/lib/wine/x86_64-windows/dbghelp.dll` |
| **Built** | 200 | This proxy — every name forwarded |
| Game's shipped MS dbghelp | 242 named (257 entries) | Game's `binaries/dbghelp.dll` |

The game's shipped Microsoft dbghelp exports 42 additional names beyond
the wine builtin's 200. Spot-checking shows these are all WinDbg-style
debugger extension commands (`block`, `chksym`, `dbghelp`, `dh`,
`fptr`, `homedir`, `inlinedbg`, `itoldyouso`, `lmi`, `lminfo`, `omap`,
`optdbgdump`, `optdbgdumpaddr`, `srcfiles`, `stack_force_ebp`,
`stackdbg`, `sym`, `symsrv`, `vc7fpo`, …) used only by `windbg.exe` /
`cdb.exe` as interactive debugger extensions. The Darktide engine
imports none of them (verified above), and Aftermath doesn't reference
them either. They are omitted by design; this matches the spec's
"within a handful" tolerance and the spec's explicit choice of the Wine
builtin as the reference.

**Deliberate omission, single export:** `wine_get_module_information`
(ord 200) is forwarded like everything else, but on native Windows it
does not exist in the Microsoft dbghelp and `fwd_resolve` will return
NULL for it. The stub's slow path bails cleanly (`ret`) rather than
crash; no caller ever invokes it. Under Wine/Proton (our target) the
Wine builtin exports it, so resolution always succeeds.

---

## Design decisions worth flagging

1. **Eager resolution in DllMain + lazy safety net in each stub.**
   The spec suggested lazy resolution on first call. We resolve every
   export eagerly at attach (so the stub fast path is always a clean
   tail jump with no argument-register risk), and each stub still has
   a lazy `fwd_resolve` fallback for the rare case the cache is empty.
   The lazy path does clobber argument registers (it must call
   `GetProcAddress`), but it only ever runs if DllMain failed to
   populate the cache — and in that case we log `missing=N` so the
   problem is visible. This is more robust than the spec's pure-lazy
   approach while keeping the fast path identical.

2. **`LoadLibraryW` inside `DllMain`.** Documented as risky under the
   loader lock, but loading a system DLL that is already mapped (which
   is what `C:\Windows\System32\dbghelp.dll` resolves to under Wine)
   is the standard proxy-DLL pattern and does not deadlock in
   practice. The loader fast-paths already-loaded modules. We do not
   return `FALSE` on `LoadLibrary` failure — that would crash the
   process. We log and let forwarders return NULL on demand.

3. **Absolute path bypasses DLL search order.** `LoadLibraryW(L"C:\\Windows\\System32\\dbghelp.dll")`
   does not re-enter our proxy. Verified empirically in the Wine test:
   the load succeeds, all 200 exports resolve, and no recursion is
   observed.

4. **`WINEDLLOVERRIDES=dbghelp=native`** is what makes Wine load *us*
   (from the application directory) rather than its own builtin. Inside
   the proxy, the absolute-path load still gets the builtin. The two
   mechanisms coexist correctly.

5. **Log path derived from `GetModuleFileNameW(hinstDLL)`, never
   hardcoded.** Verified under Wine: the log lands at the Linux path
   `…/binaries/darktide-poc.log` because Wine exposes our DLL's path
   as `Z:\…\binaries\dbghelp.dll`, and we strip the filename and
   append `darktide-poc.log`. The user can `tail -f` the Linux path
   directly.

---

## Risks / things to watch in Tier B

- **Proton-vs-Wine version skew.** Tier A used system `wine-11.11`.
  The user's Proton may bundle a different Wine build. dbghelp
  forwarding semantics are stable across Wine versions, but if a
  crash appears the first thing to check is whether the user's builtin
  dbghelp has the same export set. RUNBOOK documents how to compare.
- **Aftermath crash-on-launch is unlikely per our static analysis**
  (Aftermath has no static dbghelp import), but if it happens,
  `…/binaries/GFSDK_Aftermath_Lib.x64.dll` can be temporarily renamed
  to `.bak` to isolate the cause. RUNBOOK documents this.
- **Steam file verification will undo install.sh.** If the user
  verifies files via Steam, the proxy is replaced with the original
  and the `.orig` backup is orphaned. The RUNBOOK tells the user to
  run `uninstall.sh` first to keep things clean.

---

## Deliverables

All under `poc/phase1-proxy-dll/`:

| Path | Purpose |
|---|---|
| `src/dllmain.c` | `DllMain` + real-dbghelp load + logging |
| `src/stubs.c` | **generated** — 200 forwarder functions + lookup table |
| `src/stubs.h` | **generated** — extern decls |
| `src/dbghelp.def` | **generated** — EXPORTS section |
| `tools/gen_stubs.py` | winedump output → stubs.c/.h/.def |
| `build.sh` | cross-compile via mingw → `build/dbghelp.dll` |
| `install.sh` | proxy → `binaries/`, backs up original |
| `uninstall.sh` | restores original from `.orig` |
| `test/host.c` | minimal Wine test exe |
| `test/run_wine_test.sh` | runs + verifies the test exe under wine |
| `build/dbghelp.dll` | **the built proxy (220 KB, pei-x86-64)** |
| `RUNBOOK.md` | Tier B instructions for the live game |
| `report.md` | this file |

---

## Phase 2 readiness

Phase 1 is complete and verified at Tier A. Phase 2 (function discovery)
can proceed on a separate branch with confidence that:

- The DLL gets into the process at startup (DllMain runs at
  DLL_PROCESS_ATTACH, before any engine code).
- The logging channel works — Phase 2 hooks can use the same
  `log_line` infrastructure (or extend it) to report discovered
  addresses.
- The forwarding layer is invisible to the engine, so adding hook
  code alongside DllMain does not destabilize the dbghelp surface.

**No Phase 2 work was done here.** This is foothold-only, as scoped.
