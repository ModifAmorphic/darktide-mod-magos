# 0003 — USVFS staging spike: results

> Status: **spike concluded with a decisive, product-level finding.** This is the
> result of the investigation planned in `0002-usvfs-spike.md`. The spike code
> lives under `spike/usvfs/` on the `spike/usvfs-staging` branch (throwaway recon,
> not production code).

## Headline

**USVFS is blocked by Windows Smart App Control on this machine, and Smart App
Control is a consumer Windows 11 feature.** That makes USVFS a poor fit for a
public, consumer Darktide mod manager: any user with Smart App Control enabled
(common on Windows 11, offered during setup) would have Curator's USVFS layer
fail outright. This is a concrete, evidence-backed reason to take the junction
fallback (`0001-junction-staging.md`) as the release path and not pursue USVFS
for the consumer product.

## The evidence chain

1. **The block.** After a reboot mid-spike, loading `usvfs_x64.dll` from the .NET
   host fails hard:
   ```
   Unable to load DLL 'usvfs_x64.dll' or one of its dependencies:
   An Application Control policy has blocked this file. (0x800711C7)
   ```
   `0x800711C7` is the Windows Application Control "file blocked" status.

2. **The policy is enforced.** On this machine (Windows 11 Pro N, build 26200):
   - Smart App Control: `VerifiedAndReputablePolicyState = 1` (enforced).
   - WDAC: `SecurityServicesRunning = {2}` (Code Integrity running),
     `UsermodeCodeIntegrityPolicyEnforcementStatus = 2` (enforced).
   - AppLocker also present.

3. **Why SAC blocks it.** Both USVFS binaries are **unsigned**
   (`Get-AuthenticodeSignature` -> `NotSigned`), and `usvfs_x64.dll` is an
   **API-hooking** library (it detours Win32/NT file APIs). Smart App Control's
   "verified and reputable" model allows binaries that are either signed by a
   trusted issuer **or** have positive cloud reputation. An unsigned,
   reputation-less, foreign-maintained hooking DLL meets neither bar, so SAC
   refuses to load it.

## Why this is decisive for the product

Smart App Control is not an enterprise-only control; it is a **consumer feature**
of Windows 11 that users enable during setup or in Settings. It is increasingly
common on new Windows 11 installs. Shipping a USVFS-based staging layer means:

- It fails immediately for every SAC-enabled user (the DLL will not load).
- There is no clean remediation Curator can ship:
  - **Signing alone is insufficient.** SAC for non-Microsoft signers also requires
    cloud reputation; a freshly signed, unfamiliar DLL still gets blocked.
  - **The user cannot meaningfully opt out.** Disabling SAC is permanent (it
    cannot be re-enabled without resetting Windows), so "ask the user to turn off
    SAC" is a hostile non-starter for a consumer app.
  - **WDAC ISV allow-listing** requires deploying a custom code-integrity policy
    with admin, which itself needs SAC off. Not viable for a consumer product.

The fundamental mismatch is USVFS's design (in-process API hooking via an
unsigned DLL) versus the direction Windows consumer security is moving
(reputation-based blocking of exactly that pattern). This is the
"AV/security-software interference with API hooking" risk flagged abstractly in
`0002`, now concretely realized and verified.

## The earlier "hang," explained

Before the reboot, the controller could load `usvfs_x64.dll` and
`usvfsCreateProcessHooked` reported "injection successful," yet every injected
child (verified with `whoami.exe`, `cmd.exe`, and a .NET target) hung at startup
and never executed its own code or exited. The most consistent explanation, given
the SAC finding, is that SAC was blocking the injected `usvfs_x64.dll` from
loading inside the child processes (user-mode code integrity refusing the
unsigned hooking DLL on the remote `LoadLibrary` thread), leaving the child in a
broken/hung state. The reboot tightened enforcement to a hard, reported block on
the controller's own load, which made the root cause directly observable.

What was independently ruled out as the cause of the pre-reboot hang: shared
console, the controller's wait mechanism, a suspended child thread, a
"controller must be hooked" requirement (the API explicitly supports a non-hooked
controller), target-specific behavior (`whoami.exe` hung too), and shared-memory
permissions on `%ProgramData%\USVFS` (the ACL grants the user full control).

## Related risk worth flagging (not chased here)

Smart App Control governs **any** unsigned DLL load, including
**relay's own `relay_shell.dll`**, which is injected into Darktide via
`CreateRemoteThread`. If relay_shell.dll is unsigned, the same SAC enforcement
could block the existing modded-launch path on SAC-enabled consumer machines.
This is outside the USVFS spike's scope (and the operator's reported symptom, the
symlink error, occurs during staging, before relay launches), but it is a real
question for the product's consumer-readiness and should be verified: does a
modded launch (Curator -> relay -> Darktide injection) succeed on an SAC-enabled
machine? If not, signing relay_shell.dll (and Curator's exe) is on the
consumer-release critical path regardless of the staging decision.

## Recommendation

1. **Take the junction fallback (`0001-junction-staging.md`) as the Windows
   release path for staging.** It is privilege-free, small, dependency-free, and
   SAC-clean: junctions are created by a running process via a normal Win32
   reparse-point operation (`FSCTL_SET_REPARSE_POINT`); SAC governs whether
   Curator's own signed/reputable exe can run, not whether a running process may
   create a junction. Junctions do not load any unsigned DLL into the game.
2. **Do not pursue USVFS for the consumer product.** The SAC incompatibility is
   structural and not practicably remediable for a third-party unsigned hooking
   library on consumer Windows 11. (USVFS remains a technically interesting
   option in controlled/unsigned environments, which is why MO2, whose users
   accept the tradeoff, can use it. Curator's consumer audience will not.)
3. **Verify relay under SAC** as a separate, high-priority item (see the related
   risk above), since it affects the whole modded-launch path on consumer
   machines, independent of staging.

## Artifacts and reproducibility

- `spike/usvfs/src/UsvfsSpike/` — the harness (`UsvfsInterop.cs` P/Invoke layer +
  `Program.cs` scenarios/modes).
- `spike/usvfs/fetch-usvfs.ps1` — downloads + SHA-256-verifies + extracts the
  USVFS release into `spike/usvfs/bin/`.
- `spike/usvfs/run.ps1` — builds the harness and runs a scenario.
- `spike/usvfs/README.md` — the spike's state and how to run it.

Reproduce the block on any SAC-enabled Windows 11 machine:

```powershell
cd D:\Repos\ModifAmorphic\darktide-modificus-curator
.\spike\usvfs\fetch-usvfs.ps1
.\spike\usvfs\run.ps1 version
# on an SAC-enforced machine:
#   Unable to load DLL 'usvfs_x64.dll' ... 0x800711C7 (Application Control blocked)
```

Confirm the policy:

```powershell
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' |
  Select VerifiedAndReputablePolicyState   # 1 = SAC enforced
Get-AuthenticodeSignature .\spike\usvfs\bin\usvfs_x64.dll |
  Select Status                            # NotSigned
```

## Version grounding

- USVFS `v0.5.7.2` (`usvfs_v0.5.7.2.7z`, SHA-256
  `c6252eed78ee1c307733a4412cb68522cffc48107be4795c4e38b2b8d7c76d01`); both
  `usvfs_x64.dll` and `usvfs_proxy_x64.exe` ship unsigned.
- Test host: Windows 11 Pro N, build 26200, Smart App Control enforced
  (`VerifiedAndReputablePolicyState = 1`), WDAC user-mode code integrity running
  and enforced.
- `usvfsParameters` is opaque (`usvfsCreateParameters` + setters), marshaled as
  `IntPtr`. `LogLevel` / `CrashDumpsType` are `uint8` enums (0..3).
- Harness: .NET 10.0.301, `net10.0`, x64.
