# Darktide Mod Deployment: Landscape of Options

> **Status:** Complete. Decision rationale — documents why Lua VM
> Injection was chosen over alternatives.
>
> Survey of how mod managers solve the "don't pollute the game
> directory" and "don't break on game updates" problems, mapped to
> Darktide's specific constraints. Covers Bundle Virtualization (the
> fallback approach), full VFS (MO2-style), symlinks, proxy DLLs, and
> Lua VM Injection. The conclusion informed the POC direction.

---

## 1. The problem, restated

The current Darktide modding flow has two installation-friction
pitfalls the user wants to solve:

1. **Game-directory pollution.** The mod system scatters files across 6
   locations in the game directory: a patched `bundle_database.data`, a
   524KB `patch_999` bundle, `binaries/mod_loader`, the entire `mods/`
   tree (1.5MB), `tools/`, and two `.bat`/`.sh` scripts. Users don't
   know what's safe to touch, what to delete, or what belongs to mods.

2. **Game updates / Steam verification break the patch.** Steam file
   verification restores `bundle_database.data` to its original state,
   wiping the bundle patch. Game updates can do the same. Users must
   re-run the patch tool after every update.

The user wants to understand how other modding tools (especially
Mod Organizer 2) avoid these problems, and what options exist, before
committing to a direction.

---

## 2. The critical constraint: Darktide has no anti-cheat

This is the single most important finding, because it determines which
deployment approaches are even feasible.

**Evidence gathered by direct inspection of the live install:**

- No `EasyAntiCheat/` directory (the standard EAC install layout).
- No EAC, BattlEye, nProtect, XIGNCODE3, or similar binaries anywhere
  in `binaries/`, the game root, or the launcher directory.
- The `launcher/` directory contains only standard Fatshark tooling
  (`Launcher.exe`, `CrashReporter.exe`, `GPUDetection.exe`,
  `HashDatabaseCreator.exe`, DLSS/NRD DLLs).
- No anti-cheat DLLs in `binaries/`.

**What this means:** DLL injection, API hooking, and process-level
filesystem virtualization are all *technically* feasible for Darktide
without tripping a kernel-mode anti-cheat. This is not true for most
modern online games (which typically ship EAC or BattlEye).

**What this does NOT mean:** "No ban risk" or "Fatshark is fine with
it." There could be server-side heuristics, ToS clauses, or future
policy changes. I could not load the Steam EULA for Darktide to check
(403/error). Fatshark's Vermintide games had a tolerated modding
community using a similar Lua-based approach, which is weakly
suggestive but not authoritative for Darktide.

**Confidence:** High that no client-side anti-cheat exists in the
current install. Medium that this will remain true. Low on official
policy.

---

## 3. The deployment landscape (broad survey)

Five distinct approaches exist in the wild. I'll describe each, then
map them to Darktide in §4.

### Approach A — Process-level filesystem virtualization (MO2 / USVFS)

**How it works:** Mod Organizer 2 ships a library called USVFS (User
Space Virtual File System). It works by injecting a DLL into the game
process and hooking Windows file-system APIs (`CreateFile`,
`FindFirstFile`, `GetFileAttributes`, etc.). The hooks intercept file
accesses and transparently redirect them to a "virtual" merged view of
multiple mod directories. The game "sees" mods installed; the game
directory is untouched.

**Key properties (from the [USVFS README](https://github.com/ModOrganizer2/usvfs)):**
- Per-process, not system-wide — only affects processes MO2 launches
- Links disappear when the session ends
- No administrator rights required (neither install nor use)
- Filesystem-independent (works on FAT32, network drives, read-only media)
- Can overlay multiple directories onto one target (conflict resolution)
- Can "virtually" unlink files (make them invisible to the process)

**Drawbacks (stated by the project itself):**
- Memory and CPU overhead (described as "hopefully marginal")
- Only becomes active during each process's initialization phase —
  dependent DLLs loaded earlier may not see the virtual view
- New source of hard-to-diagnose bugs
- *"May rub antivirus software the wrong way as the used techniques are
  similar to what some malware does"* — this is the project's own warning

**Why MO2 needs this:** Bethesda games (Skyrim, Fallout) use **file-
replacement modding** — mods literally overwrite game files (textures,
meshes, plugins). Hundreds of mods can conflict over the same paths.
The VFS lets MO2 present a coherent merged view without copying
anything, and lets users reorder/enable/disable mods instantly by
changing the virtual overlay rather than moving files.

**License:** GPLv3 (C++, Windows-only).

### Approach B — Symlink / hardlink / junction deployment (Vortex, r2modman)

**How it works:** The mod manager maintains a "staging" directory
(outside the game) where each mod's files live in isolation. At
deploy time, it creates filesystem links from the game directory into
the staging directory, so the game sees the mod files as if they were
physically present. Disabling a mod = removing its links.

**Three Windows linking mechanisms, with distinct semantics:**

| Property | Symbolic link | Junction point | Hard link |
|----------|--------------|----------------|-----------|
| Can link to files? | Yes | **No (dirs only)** | Yes |
| Can link to directories? | Yes | Yes | No (modern FS) |
| Cross-volume? | Yes | Yes | **No** |
| Survives target move? | No | No | Yes |
| Requires admin? | **Yes*** | **No** | No |

\* Symlink creation requires the `SeCreateSymbolicLinkPrivilege`, held
by admins by default. **Non-admin users can create symlinks if they
enable Developer Mode (Windows 10 v1703+).** Junction points and hard
links do not need this privilege. ([Source: Wikipedia on symbolic
links](https://en.wikipedia.org/wiki/Symbolic_link), Microsoft Learn.)

**Vortex** (Nexus Mods' manager) auto-selects among these based on the
game and filesystem layout — hardlinks when staging is on the same
volume, symlinks otherwise, with a copy fallback. I could not load the
Vortex deployment wiki directly (403) but this is well-documented
behavior.

**r2modman** (Electron app for Unity/BepInEx games) uses a staging
directory + links model with profile support. I was unable to confirm
from primary sources whether it uses symlinks or copies by default
(wiki/code search required sign-in). General community consensus is
symlinks with a copy fallback, but treat this as medium-confidence.

**Drawback:** Symlinks hit the privilege problem on Windows. Hardlinks
require same-volume staging. Neither helps with *modifying* existing
game files — links can only add/overlay, not patch binary files in
place.

### Approach C — Copy / staging (the current Darktide approach)

**How it works:** Physically copy mod files into the game directory.
This is what dtkit-patch + the manual installation instructions do
today.

**Drawback:** This is the status quo the user wants to improve. It
doesn't solve either pitfall.

### Approach D — Proxy DLL injection (BepInEx / MelonLoader style)

**How it works:** Drop a DLL named after a library the game will load
anyway (e.g., `winhttp.dll`, `version.dll`) into the game directory.
The OS loads the proxy DLL, which then loads the real library and
patches the game in memory. BepInEx and MelonLoader use this to inject
into Unity games.

**Drawback for Darktide:** Darktide doesn't use Unity — it uses
Fatshark's proprietary engine. Proxy-DLL approaches are engine-
specific and fragile. And this still requires putting a DLL in the game
directory. It offers no advantage over the existing Lua-based injection
for Darktide's purposes.

### Approach E — Bootstrap redirect (Darktide-specific)

This is the approach the existing architecture *almost* enables, and
it doesn't have a name in the broader modding ecosystem because it's
specific to how Darktide's loader works.

**How it works:** The current chain is:

```
bundle_database.data (patched)
  → engine loads patch_999 bundle
    → trampoline script does: io.open("./mod_loader")
      → mod_loader reads ./mods/ via relative paths
```

The trampoline is **hardcoded to a relative path** (`"./mod_loader"`)
and baked into the 524KB `patch_999` bundle. BUT — the trampoline is
just Lua `io.open` calls. If we rebuild the bundle with a trampoline
that reads from an absolute path (or an env var, or a registry key),
the entire downstream mod tree can live **outside the game directory**.

The workspace already contains a `darktide-extractor` tool (Rust, with
Oodle decompression libs) and `darktide-re-docs` (reverse-engineered
bundle format docs). The infrastructure to extract, modify, and
rebuild the `patch_999` bundle exists.

Under this approach, the game-directory footprint shrinks to:
- **1 modified file:** `bundle_database.data` (patched, backed up)
- **1 added file:** `bundle/9ba626afa44a3aa3.patch_999` (the boot
  bundle)

Everything else — `mod_loader`, `mods/`, DMF, load order, all user
mods — lives in a staging directory managed by the mod manager app.

---

## 4. Mapping the approaches to Darktide's hard constraints

Darktide imposes a constraint that most modded games do not: **the
`bundle_database.data` patch is unavoidable** for code execution. The
engine reads the bundle database before any Lua runs, so there is no
opportunity to hook anything earlier without a pre-Lua DLL injection.
That makes "zero game-directory footprint" impossible without an
extremely invasive, engine-specific boot-time DLL hook.

Given that, the real question is: **how much of the *downstream* file
tree can we get out of the game directory?**

| Approach | Game-dir footprint | Solves pollution? | Solves update-break? | Complexity |
|----------|--------------------|------------------|---------------------|------------|
| A. Full VFS | bundle patch + patch_999 (still needed) | Yes (for downstream files) | No (bundle patch still reverts) | Very high |
| B. Symlinks/junctions | bundle patch + patch_999 + links | Partial (dirs yes, files need admin) | No | Medium |
| C. Copy/staging | everything (status quo) | No | No | Low |
| D. Proxy DLL | bundle patch + proxy DLL + ... | No | No | High, fragile |
| E. Bootstrap redirect | bundle patch + patch_999 only | **Yes** | No (but only 2 files to re-patch) | Low-medium |

**Key honest observations:**

- **No approach eliminates the bundle_database.data re-patch problem
  on game updates/verification.** That file is in the Steam depot and
  will be restored. The best any approach can do is make re-patching
  trivial and automatic (the app detects the revert and re-applies).
  This is a different, smaller problem than the current manual flow.

- **The VFS approach (A) is overkill for Darktide.** Its complexity is
  justified for Bethesda games where file-overlay conflict resolution
  across hundreds of mods is the core problem. Darktide's modding is
  Lua-hook-based — mods don't replace files, they hook functions. The
  conflict model is fundamentally simpler. Also, USVFS is GPLv3 C++,
  which has licensing and language-stack implications.

- **The bootstrap redirect (E) fits Darktide's architecture uniquely
  well** because the existing design already separates "the unavoidable
  boot bundle" from "the updatable file-on-disk mod_loader." The
  current trampoline just happens to point at a relative path. Making
  it point at a staging location is a small change to the boot bundle,
  and the workspace already has the tooling to do it.

- **Symlinks (B) are a reasonable complement** for the parts that
  remain file-based, but the Windows symlink privilege issue is a real
  UX hurdle (asking users to enable Developer Mode or run as admin).
  Junctions avoid the privilege issue but only work for directories,
  not files like `mod_loader`.

---

## 5. The crux decisions

Before picking a direction, these are the questions that determine
everything downstream:

1. **Is "2 files in the game dir" acceptable, or is the goal truly
   zero?** If 2 files (patched db + boot bundle) is acceptable, the
   bootstrap redirect (E) is dramatically simpler than full
   virtualization. If zero is the hard requirement, only a boot-time
   DLL hook could achieve it — much higher complexity and risk.

2. **Is the re-patch-on-update problem acceptable if it's fully
   automated?** No approach can prevent Steam from reverting
   `bundle_database.data`. But an app that auto-detects the revert and
   silently re-applies the patch turns a manual pain point into a
   non-event. Is that good enough, or is even the *existence* of a
   patched game file a dealbreaker?

3. **How important is multi-profile support?** (Different mod sets for
   different play sessions.) This is where VFS/symlink approaches shine
   and the redirect approach needs to think harder (it can still do
   profiles via different staging dirs + a config switch, but it's less
   elegant than MO2's instant-toggle).

4. **Cross-platform considerations?** The user's own install is on
   Linux (`/games/steamapps/...`, Proton). Symlinks are first-class on
   Linux but privilege-gated on Windows. The boot-bundle approach is
   OS-agnostic. Does the app need to support Windows and Linux equally?

---

## 6. What I don't know yet (honest gaps)

- **r2modman's exact deployment mechanism** — I have medium confidence
  it's symlinks-with-copy-fallback but couldn't confirm from primary
  sources (not independently verified).
- **Vortex's auto-selection logic details** — the deployment wiki 403'd.
  I know the methods exist (hardlink/symlink/copy) from general
  knowledge but couldn't read Nexus's own documentation.
- **Whether the `patch_999` bundle can be cleanly rebuilt with a custom
  trampoline.** The extractor tool and RE docs exist in the workspace,
  but I haven't verified that the bundle format supports a
  round-trip (extract → modify script → repack) without breaking the
  engine's signature/hash checks. This is a **high-value follow-up
  investigation** — it's the linchpin of Approach E.
- **Fatshark's official modding stance for Darktide specifically.**
  Vermintide's tolerated-modding precedent is suggestive but not
  authoritative.
- **Whether Darktide's engine validates bundle integrity at runtime**
  (beyond the database lookup). If it hashes bundles, a rebuilt
  `patch_999` might be rejected.

---

## 7. Sources consulted

- `/games/steamapps/common/Warhammer 40,000 DARKTIDE/` — live modded
  install (filesystem inspection, anti-cheat search)
- `ModOrganizer2/usvfs` README — USVFS architecture and trade-offs
- `ebkr/r2modmanPlus` README + wiki — deployment model (partial)
- Wikipedia, "Symbolic link" — Windows symlink/junction/hardlink
  semantics and privilege requirements
- `Darktide-Mod-Loader/README.md` — current install/uninstall flow
- `Darktide-Mod-Loader/bundle/9ba626afa44a3aa3.patch_999` — boot bundle
  (trampoline path extraction via `strings`)
- `the darktide-extractor tool` — bundle
  extraction tooling presence
- `the reverse-engineering documentation` — RE
  documentation presence
