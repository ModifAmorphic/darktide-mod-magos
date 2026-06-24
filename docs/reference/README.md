# Reference — living

Validated facts about the Darktide game binary and the existing modding
ecosystem. Updated as we learn more.

- `darktide-binary.md` — the validated game-binary constraints (LuaJIT,
  `lua_State` offsets, sandboxed `_G`, discovery methodology, timing).
- `darktide-framework-analysis.md` — how the current (DMF + dtkit-patch)
  toolchain works; what production replaces.
- `analysis-verification.md` — audit of the above.

These are properties of the game/ecosystem, independent of any implementation.
The frozen POC deep-dive lives in `../poc/`.
