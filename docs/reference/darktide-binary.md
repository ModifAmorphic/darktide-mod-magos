# Darktide binary — validated constraints

Immutable technical facts about the Darktide engine binary that any
implementation must respect. These are properties of the game, not of any
implementation. Detailed historical reference: `../poc/production-spec.md` and
`../poc/lua-vm-injection-anchors.md`.

## LuaJIT

- Version: LuaJIT 2.1, statically linked into `Darktide.exe` (no `lua51.dll`);
  functions are in `.text`, not exported.
- Mode: non-GC64 (LJ_64, 32-bit MRefs).

## `lua_State` field offsets (LJ_64 non-GC64)

| Offset | Field | Type |
|--------|-------|------|
| `0x08` | `glref` (global_State*) | 4-byte MRef |
| `0x10` | `base` (TValue*) | 8-byte |
| `0x18` | `top` (TValue*) | 8-byte |
| `0x24` | `stack` (TValue*) | 4-byte MRef |
| `0x38` | `stacksize` | integer |

Stack slot size: **8 bytes** (TValue). `lua_gettop` = `(top - base) >> 3`.

## Sandboxed `_G`

The engine's script init calls `luaL_openlibs`, then replaces `_G.print`,
`_G.require`, `_G.dofile`, `_G.loadfile`, `_G.load` with engine wrappers. The
standard library (`io`, `table`, `string`, `math`, …) is **not** exposed to
injected chunks.

Re-calling `luaL_openlibs` is **destructive** — it overwrites the engine's
custom wrappers and crashes the game. The solution is to register dependencies
as C functions via `lua_pushcclosure` + `lua_setfield(L, LUA_GLOBALSINDEX,
name)` — the same mechanism the engine uses.

## Function discovery

16 LuaJIT/engine functions confirmed at runtime, discovered via:
- **String-anchor** — `stingray::` names → LEA xref → `.pdata` function
  (engine functions).
- **Source-pattern** — match compiled bodies against LuaJIT 2.1 source (the C
  API cluster).
- `.pdata` gap handling — CFG thunks (`E9 rel32`), leaf functions, import
  thunks (`FF 25`).

Build-agnostic: the engine finds all 16 at shifted RVAs across binary versions
(validated — a newer build shifted the cluster uniformly +0xf0680).

## Timing

Retry-on-error: the injected chunk self-checks for readiness and retries on
the engine's `lua_pcall` calls (succeeds ~1.3–2.4s after VM creation).

## Key constants

- `LUA_GLOBALSINDEX` = `-10002`; `LUA_REGISTRYINDEX` = `-10000`.
- `lua_pushcfunction(L, f)` → `lua_pushcclosure(L, f, 0)`; `lua_setglobal(L,
  name)` → `lua_setfield(L, LUA_GLOBALSINDEX, name)` (macros, not real fns).
- `sizeof(GCstr)` = `0x14`; `strdata(s)` = `(char*)s + 0x14`.

## Pinned reference binary

SHA-256 `132eed5fe58515774a41199269dd240ef6092f84b1efc8ad4a28e23ea6791661`
(the `docs/poc` addresses are for this build; the engine is build-agnostic).
