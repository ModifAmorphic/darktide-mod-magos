# Modificus Curator

**Modificus Curator** is a mod manager for **Warhammer 40,000: Darktide**. It launches the
game modded via the
[Enginseer runtime](https://github.com/ModifAmorphic/darktide-enginseer) (DLL
injection: no files in the game directory, no bundle-database patching) and
stays out of the way for vanilla play (launch the game from Steam and it runs
unmodified).

## Components

- **Modificus Curator** (this repo): the mod manager app (UI, staging, load order,
  profiles, dependency resolution, mod-source integrations). The backend
  libraries (Profiles, Mods, Steam, Integrations, Enginseer-client, General) and
  the UI (the app shell + profile management, global Preferences, the mod-list
  UI, the Launch flow + Settings window) are in place. The app is user-usable.
  The Launcher is a stub. See
  [`src/README.md`](src/README.md) for developer/build
  details.
- **Enginseer runtime** (separate repo):
  [darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer): the
  injected modding runtime + its launcher (including the mod loader that loads
  DMF + user mods). Curator consumes its launcher.

## Status

Modificus Curator is **pre-release**. The app is not distributed yet; to try it,
build it from source (see
[`src/README.md`](src/README.md)). Runtime artifacts
(launcher, shell DLL, mod loader) come from the
[darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer) repo.

## License

GNU General Public License v3; see [`LICENSE`](LICENSE).
