# darktide-mod-magos

A mod manager for **Warhammer 40,000: Darktide**. It launches the game
modded via DLL injection — no files in the game directory, no
bundle-database patching — and stays out of the way for vanilla play
(launch the game from Steam and it runs unmodified).

## Status

- [**Runtime**](runtime) — the injected modding runtime + launcher — is merged as
  the production seed: a Rust discovery engine + C shell, validated
  end-to-end (the game reaches the main menu, the Lua VM hook fires, all 16
  LuaJIT functions are discovered in-process).
- [**Mod Magos (Mod Manager)**](mod-manager) — the mod manager app (UI, staging, load order, profiles,
  dependency resolution) — is planned, not yet built.
- The `poc` branch holds the historical proof-of-concept (reference only).

## Directory layout

```
runtime/        the injected modding runtime + injector
  discovery/      Rust: discovers Darktide's LuaJIT functions at runtime
  shell/          C: the injected DLL (hooks the game's Lua VM)
  launcher/       C: launches the game modded (injects the DLL)
  tests/          C unit tests
mod-manager/    Darktide Magos — the mod manager app (planned, not yet built)
docs/           architecture, reference, and POC record
.github/        CI workflows
```

How it all works is documented under [`docs/`](docs/) — start with
[`docs/architecture/`](docs/architecture/).

## Building

From a Linux box with Rust + MinGW installed:

```sh
make build    # cross-compile the DLL + launcher for Windows
make check    # verify the DLL
make test     # run the C + Rust tests
```

Full build/test setup (including the local Steam/game-path config) is in
[`AGENTS.md`](AGENTS.md).

## License

MIT — see [`LICENSE`](LICENSE).
