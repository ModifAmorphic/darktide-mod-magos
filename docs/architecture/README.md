# Architecture

**Magos Modificus** is the user-facing mod manager app for Darktide. It launches
the game modded via the
[Enginseer runtime](https://github.com/ModifAmorphic/darktide-enginseer) (DLL
injection: no game-directory footprint, no bundle-database patching) and stays
out of the way for vanilla play (launch from Steam and the game runs
unmodified).

## Component model

- **Magos Modificus (`magos-modificus/`)**: the user-facing app:
  staging-directory management, load order, profiles, dependency resolution,
  mod-source integrations, the Launch flow. The backend libraries and the UI
  are implemented (the app is user-usable); the Launcher is a stub. See
  [`MAGOS-MODIFICUS.md`](MAGOS-MODIFICUS.md) for the architecture.
- **Enginseer runtime** (external): the injected modding runtime + its launcher.
  Lives in a separate repo,
  [darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer).
  Magos consumes its launcher via the `enginseer-client` library (the launch
  façade) and treats the rest as a black box.

## References

- [`MAGOS-MODIFICUS.md`](MAGOS-MODIFICUS.md): the Magos Modificus architecture
  (project layout, domain libraries, the Enginseer contract Magos consumes,
  profiles, the Windows/Linux launch paths, v1 scope).
- [`ui-architecture.md`](ui-architecture.md): the UI layer (the shell, the
  profile session, the mod list, the update UI, the DMF install prompt,
  dialogs, preferences, and i18n).
- [darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer): the
  runtime architecture (the Rust↔C Hybrid, the seam, the launcher flow,
  discovery, the mod loader).
- `docs/reference/magos-modificus/`: per-library API reference for the Magos
  Modificus backend libraries.
