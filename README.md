# darktide-mod-magos

**Magos** is a mod manager for **Warhammer 40,000: Darktide**. It launches the
game modded via DLL injection — no files in the game directory, no
bundle-database patching — and stays out of the way for vanilla play: launch
the game from Steam and it runs unmodified.

## Components

Magos has two components:

- **Enginseer (runtime)** — the injected modding runtime + its launcher (the
  runtime includes the mod loader that loads DMF + user mods). Built, and the
  production seed of this repo. See
  [`enginseer/README.md`](enginseer/README.md) for build + developer details.
- **Magos Modificus** — the mod manager app (UI, staging, load order, profiles,
  dependency resolution). Phase 0 scaffold in place (.NET 10 + Avalonia 12
  foundation, DI + logging + config + a bare UI window); library behavior is
  stubbed for now. See [`magos-modificus/README.md`](magos-modificus/README.md)
  for developer/build details.

## Getting started

> **Early-access / manual setup.** Mod Magos (the app) is not built yet, so for
> now setup is manual: you run the launcher directly with a mod directory you
> assemble by hand. This will be replaced by Mod Magos when it ships.

### 1. Get the runtime

There are no releases yet. To get the runtime artifacts, download them from the
**latest CI run**:

1. Go to the **Actions** tab → **mingw-build** → the latest green run →
   **Artifacts** → **`magos-shell-mingw`**.
2. That artifact contains the complete Enginseer runtime:
   - `magos_launcher.exe` — the launcher/injector.
   - `magos_shell.dll` — the injected DLL.
   - `mod_loader/` — the mod loader Lua (loaded by the shell at runtime).

When laid out, your runtime directory should look like:

```
<runtime-dir>/
  magos_launcher.exe
  magos_shell.dll
  mod_loader/
    init.lua
    file.lua, hook.lua, class_patch.lua, require_wrap.lua, lifecycle.lua, mod_manager.lua
```

### 2. Run it

The launcher starts the game modded. The only required flag is the game binary;
the shell DLL and mod loader root default to next to the launcher exe (the shell
self-locates the mod loader from its own path), so you only point it at your
mods:

```bat
magos_launcher.exe --game-binary "C:\Path\To\Darktide.exe" --mod-path "C:\Path\To\mods"
```

A minimal `launch.bat` (next to the launcher) makes this easier:

```bat
magos_launcher.exe ^
  --game-binary "C:\Games\Steam\steamapps\common\Warhammer 40,000 DARKTIDE\binaries\Darktide.exe" ^
  --mod-path "C:\Path\To\mods"
```

> On Linux/Proton, use Windows-style `Z:\` paths (the Proton `Z:` drive maps to
> your Linux filesystem).

### 3. Configure Steam (Linux/Proton)

The cleanest way to launch modded is as a **Steam non-Steam game**, so Steam's
Proton layer handles the Windows runtime:

1. In Steam, **Add a non-Steam game** → browse to your `launch.bat`.
2. Open its **Properties**:
   - **Target:** the full path to `launch.bat`.
   - **Start In:** the runtime directory (where the launcher + DLL live).
   - **Launch options:**
     ```
     PROTON_LOG=1 STEAM_COMPAT_DATA_PATH=<path-to-compatdata-for-darktide> %command%
     ```
   - **Compatibility:** check **"Force the use of a specific Steam Play
     compatibility tool"** and pick a Proton version.
3. Launch it. The launcher creates the game suspended, injects the DLL, waits
   for the hook to arm, and resumes — Steam UX + zero game-directory footprint
   in one step.

`PROTON_LOG=1` is handy while verifying setup: the launcher's shell log lands
in `magos_enginseer.log` next to the launcher, and the engine's own Lua-side
output lands in the Proton log (`steam-1361210-proton-log`). The trampoline's
one-line `OK`/`FAIL` in `magos_enginseer.log` is the reliable bootstrap check.

### 4. Where mods go

Mods live in the **mod directory** you point `--mod-path` at. Lay it out as:

```
<mod-path>/
  mods.lst             one mod name per line, in load order (list dmf first)
  dmf/                 the Darktide Mod Framework (DMF) — the API mods are built against
  <your-mod>/          your mod(s)
```

- **DMF** (the Darktide Mod Framework) is the framework mods are built against;
  place it at `<mod-path>/dmf/`.
- **`mods.lst`** lists the mods to load, one name per line, in the order they
  load (list `dmf` first). The loader loads exactly what's listed, in order — it
  injects nothing. Magos Modificus regenerates this file each launch; until Magos
  ships, create/edit it by hand.

## License

GNU General Public License v3 — see [`LICENSE`](LICENSE).
