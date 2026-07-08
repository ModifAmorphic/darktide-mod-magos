# Reference

Reference material, organized by category. Updated as we learn more.

- [`src/`](src/) -- per-library API reference for the
  Modificus Curator backend libraries (public interfaces, key types, DI
  registration, cross-platform notes).
- [`src/ui.md`](src/ui.md): per-surface API reference
  for the UI layer (the profile session, dialog service, preferences service,
  localization, the DMF prompt coordinator, the update-check runner,
  converters, DI registration).
- [`release-strategy.md`](release-strategy.md): how Curator releases are
  produced, attested, scanned, and installed (release-please workflow,
  post-release AV/VT scan, PR gate, Linux installer, sandbox rehearsal).
- [darktide-modificus-relay](https://github.com/ModifAmorphic/darktide-modificus-relay) --
  game-binary reference (LuaJIT, `lua_State` offsets, discovery methodology)
  and the existing modding-ecosystem audit, now live with the runtime.
