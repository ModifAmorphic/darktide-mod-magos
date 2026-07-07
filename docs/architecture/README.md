# Architecture

**Modificus Curator** is the user-facing mod manager app for Darktide. It launches
the game modded via the
[Enginseer runtime](https://github.com/ModifAmorphic/darktide-enginseer) (DLL
injection: no game-directory footprint, no bundle-database patching) and stays
out of the way for vanilla play (launch from Steam and the game runs
unmodified).

## Component model

- **Modificus Curator (`src/`)**: the user-facing app:
  staging-directory management, load order, profiles, dependency resolution,
  mod-source integrations, the Launch flow. The backend libraries and the UI
  are implemented (the app is user-usable); the Launcher is a stub. See
  [`MODIFICUS-CURATOR.md`](MODIFICUS-CURATOR.md) for the architecture.
- **Enginseer runtime** (external): the injected modding runtime + its launcher.
  Lives in a separate repo,
  [darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer).
  Curator consumes its launcher via the `enginseer-client` library (the launch
  façade) and treats the rest as a black box.

## References

- [`MODIFICUS-CURATOR.md`](MODIFICUS-CURATOR.md): the Modificus Curator architecture
  (project layout, domain libraries, the Enginseer contract Curator consumes,
  profiles, the Windows/Linux launch paths, v1 scope).
- [`ui-architecture.md`](ui-architecture.md): the UI layer (the shell, the
  profile session, the mod list, the update UI, the DMF install prompt,
  dialogs, preferences, and i18n).
- [darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer): the
  runtime architecture (the Rust↔C Hybrid, the seam, the launcher flow,
  discovery, the mod loader).
- `docs/reference/src/`: per-library API reference for the Modificus
  Curator backend libraries.
