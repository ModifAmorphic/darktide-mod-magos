# Modificus Curator

**Modificus Curator** is a mod manager for **Warhammer 40,000: Darktide**. It launches the
game modded via
[Modificus Relay](https://github.com/ModifAmorphic/darktide-modificus-relay) (DLL
injection: no files in the game directory, no bundle-database patching) and
stays out of the way for vanilla play (launch the game from Steam and it runs
unmodified).

## Components

- **Modificus Curator** (this repo): the mod manager app (UI, staging, load order,
  profiles, dependency resolution, mod-source integrations). The backend
  libraries (Profiles, Mods, Steam, Integrations, Relay-client, General) and
  the UI (the app shell + profile management, global Preferences, the mod-list
  UI, the Launch flow + Settings window) are in place. The app is user-usable.
  The Launcher is a stub. See
  [`src/README.md`](src/README.md) for developer/build
  details.
- **Modificus Relay** (separate repo):
  [darktide-modificus-relay](https://github.com/ModifAmorphic/darktide-modificus-relay): the
  injected modding runtime + its launcher (including the mod loader that loads
  DMF + user mods). Curator consumes its launcher.

## Status

Initial releases are published on the
[releases page](https://github.com/ModifAmorphic/darktide-modificus-curator/releases) and
are marked as prereleases while the release pipeline settles. To build from
source instead, see [`src/README.md`](src/README.md). The bundled runtime
artifacts (launcher, shell DLL, mod loader) come from
[Modificus Relay](https://github.com/ModifAmorphic/darktide-modificus-relay).

## Installation

### Windows

1. Download `modificus-curator-setup.exe` from the
   [latest release](https://github.com/ModifAmorphic/darktide-modificus-curator/releases).
2. Run it. It is a one-click installer (no wizard): it installs Curator into
   `%LOCALAPPDATA%\ModifAmorphic.ModificusCurator\`, creates Start Menu and
   desktop shortcuts, and registers in Apps & Features so you can uninstall it
   the usual way. If the .NET 10 Runtime is missing, the installer downloads
   and installs it for you.

On first launch Curator registers the `nxm://` handler itself, so Nexus
"Download with manager" links work without any extra setup.

> The installer is not code-signed yet, so Windows SmartScreen may warn on the
> first run. Choose **More info** > **Run anyway** to continue.

Curator checks for new versions on startup and can update itself in place.
When one is available, a notice appears in the bottom status strip and the
**Settings** window's **Updates** section lets you download and restart into
the new version, so you never need to re-run the installer to stay current.
(This is Windows-only; on Linux, re-run the install script to update.)

### Linux

Curator needs the **.NET 10 Runtime**. If it is not already installed, get it
from <https://dotnet.microsoft.com/download/dotnet/10.0>.

One line, installs the latest stable release:

```sh
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh
```

To install the latest prerelease instead, pass `--prerelease`:

```sh
curl https://raw.githubusercontent.com/ModifAmorphic/darktide-modificus-curator/main/scripts/install.sh | sh -s -- --prerelease
```

The script resolves the archive from a manifest the release pipeline maintains
(no GitHub API calls), installs into `~/.local/share/Modificus Curator/`,
replaces only the `app/` and `relay/` folders (your profiles, mods, logs, and
`config.json` are left alone), marks the binaries executable, and adds a
`modificus-curator` symlink in `~/.local/bin/`. If the symlink cannot be
created, it prints the executable path to run instead.

The Linux release archive contains two top-level folders:

- `app/` - the Curator UI, the `nxm://` handler, and the launcher stub.
- `relay/` - the bundled Modificus Relay runtime.

Extracting it into Curator's default data folder seeds both the app and the
default Relay location, so no extra configuration is needed.

Manual install:

1. Download `curator-<tag>-linux-x64.tar.gz` from the
   [latest release](https://github.com/ModifAmorphic/darktide-modificus-curator/releases).
2. Extract it into `~/.local/share/Modificus Curator/` (create the folder if it
   does not exist), for example:
   `tar -xzf curator-<tag>-linux-x64.tar.gz -C "$HOME/.local/share/Modificus Curator/"`.
3. Make the UI executable:
   `chmod +x "$HOME/.local/share/Modificus Curator/app/Modificus.Curator"`.
4. Optionally symlink it onto your PATH:
   `ln -sf "$HOME/.local/share/Modificus Curator/app/Modificus.Curator" "$HOME/.local/bin/modificus-curator"`.

## License

GNU General Public License v3; see [`LICENSE`](LICENSE).
