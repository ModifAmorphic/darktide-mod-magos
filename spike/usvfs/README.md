# USVFS staging spike

Throwaway investigation for `docs/design/0002-usvfs-spike.md` (the integration
plan) and `docs/design/0003-usvfs-spike-results.md` (the findings). **Not part of
the solution; not built by `dotnet build src/modificus-curator.sln`.** On the
`spike/usvfs-staging` branch only.

## State

**Concluded: USVFS is blocked by Windows Smart App Control.** See
`docs/design/0003-usvfs-spike-results.md` for the full write-up. Short version:
this machine runs Smart App Control enforced (consumer Windows 11 feature);
`usvfs_x64.dll` is unsigned and an API-hooking library, so SAC refuses to load it
(`0x800711C7`, "Application Control policy has blocked this file"). This is
structural for consumer machines and not practicably remediable for a third-party
unsigned hooking DLL, so USVFS is not the staging path for the consumer product.
The junction fallback (`docs/design/0001-junction-staging.md`) is the release
path. The harness below reproduces the block on any SAC-enabled machine.

## Layout

```
spike/usvfs/
  fetch-usvfs.ps1       download + verify (SHA-256) + extract the USVFS release
  run.ps1               build the harness + run a scenario
  bin/                  vendored usvfs_x64.dll + usvfs_proxy_x64.exe (gitignored)
  .cache/               the .7z + full extraction (gitignored)
  src/UsvfsSpike/       the harness
    UsvfsInterop.cs     P/Invoke over usvfs_x64.dll (+ kernel32 process helpers)
    Program.cs          scenarios + modes
    UsvfsSpike.csproj   net10.0, x64; copies the vendored binaries next to the exe
```

## Run

```powershell
cd D:\Repos\ModifAmorphic\darktide-modificus-curator
.\spike\usvfs\fetch-usvfs.ps1        # one-time: vendor the USVFS binaries
.\spike\usvfs\run.ps1 version        # on an SAC-enforced machine: 0x800711C7 block
.\spike\usvfs\run.ps1 spawn -Exe "$env:WINDIR\System32\whoami.exe"   # pre-reboot wall reproducer
```

## Scenarios / modes

- `spawn --exe <p> [--args ...] [--timeout ms]` — minimal: can a hooked spawn
  launch a process that exits on its own? (This is the wall reproducer.)
- `enum [--work <p>]` — Q1: does a usvfs-spawned native process see a virtual dir?
- `propagate [--work <p>]` — Q2: does USVFS reach a grandchild a hooked process
  spawns via its own `CreateProcess`?
- `lifetime [--work <p>]` — Q3: does the virtual view survive controller disconnect?
- `version` — print the USVFS version (smoke test).
- Auxiliary (kept for inspection; the scenarios use `cmd.exe` instead because a
  managed target hangs under injection and the real targets are native):
  `target`, `relay-standin`.

Set `$env:USVFS_SPIKE_KEEP = '1'` to leave the scratch work dir in place after a
run (under `%TEMP%` unless `--work` is given) for inspection.
