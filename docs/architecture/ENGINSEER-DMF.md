# Enginseer ↔ DMF — integration

> **Audience:** a future dev (or the operator) who needs to understand and
> maintain the integration between the Enginseer (aka the Mod Loader) and DMF
> (the Darktide Mod Framework). This is the dedicated reference for *how the
> two fit together*, especially the IO re-rooting.
>
> **Prereq reading:** `docs/architecture/RUNTIME.md` (the runtime architecture +
> the engine-context mechanism + the deferred bootstrap), then this doc.

## The core model: who loads what

**The Enginseer IS the mod loader. Loading mods is its "rite."** DMF is *just a
mod* — a bag of helper APIs (hooks, options, keybinds, `new_mod`/`get_mod`, …)
that other mods opt into. **DMF does not load mods; the Enginseer does.**

This is the single most important thing to keep straight when reading either
codebase. The community's old chain was:

```text
patch_999 -> mod_loader  ->  DMF  ->  user mods
             (loads mods)    (a mod)
```

Magos replaces `patch_999` + `mod_loader` with the Enginseer, but keeps DMF in
exactly the same role — the first mod loaded, nothing more:

```text
DLL injection -> Enginseer (the rite)  ->  DMF (mod #1)  ->  user mods
                (loads mods)              (a mod)
```

Concretely, the rite — not DMF — reads `mod_load_order.txt`, decides the order
(always `dmf` first), loads each `.mod`, calls `run()` / `init()`, and exposes
itself as `Managers.mod` to drive the per-frame + state-change lifecycle. DMF's
own `dmf_mod_object` is driven by the rite like any other top-level mod object;
DMF then drives *its* registered user mods through its inner update loop (see
[Two-level driving](#two-level-driving) below).

## The rite (`runtime/enginseer/mod_manager.lua`)

The rite is `ModManager:init()` (a `class("ModManager")`, loaded via
`Mods.file.dofile("mod_manager")` from the deferred bootstrap hook — see
`lifecycle.lua`). It runs in two phases:

1. **SCAN** — read `mod_load_order.txt` (`Mods.file.read_content_to_table`),
   prepend `"dmf"`, and build the **entire** `_mods` table up front. Each entry
   is shaped `{ id, name, handle, enabled, state, object }`. Every entry exists
   before any mod's `run()`/`init()` reads the table.
2. **LOAD** — for each entry, in order: `exec_with_return` the mod's `.mod` file
   → call its `run()` (pcall-guarded) → if it returns an object, store it and
   call `object:init()` synchronously, **before the next mod loads**. That
   per-mod run+init ordering is what makes DMF's `init()` define
   `new_mod`/`get_mod` before any user mod's `run()` runs.

State machine: `_state` goes `"scanning"` → `"loading"` → `"done"`. DMF only
needs `_state == "done"` eventually, which the rite reaches as soon as the LOAD
loop finishes.

The rite also exposes `Managers.mod:update(dt)` and
`Managers.mod:on_game_state_changed(status, state_name, state_object)`, which
the deferred bootstrap wires into `CLASS.StateGame.update` and
`CLASS.GameStateMachine._change_state` respectively (see [Deferred
bootstrap](#deferred-bootstrap)).

### Fault isolation

Every mod's `run()`/`init()`/`update()`/`on_game_state_changed()` is
pcall-guarded. One bad mod logs (`[Enginseer] mod '<name>' <phase> failed: …`)
and the rite continues to the next; a missing/unreadable `.mod` or a `.mod`
without `run()` is logged and skipped. The whole bootstrap is itself wrapped in
a pcall in `lifecycle.lua`, so a DMF/mod failure degrades to vanilla + a log
line, never a boot crash.

## Surfaces the Enginseer provides for DMF/mods

DMF expects the loader's surfaces to already exist when it loads (it is not a
standalone guest). The Enginseer builds all of these in Lua at pcall#1 (entry
`enginseer.lua`) and during the deferred boot. They are the **loader API**; the
rite is the loader **logic**.

| Surface | Provided by | Purpose |
| --- | --- | --- |
| `Mods.original_require` | entry | the engine's real `require` (captured before globals are stripped ~pcall#6) |
| `Mods.require_store` | entry + `require_wrap` | per-file require-result cache; populated by the wrapped `require` (enables DMF's `hook_require`) |
| `Mods.lua.io` / `Mods.lua.loadstring` | entry | the engine's real `io` / `loadstring` (captured before stripped) |
| `Mods.lua.os` / `Mods.lua.ffi` | entry | captured for DMF's debug modules (`table_dump`, dev console); `or` so nil-safe if absent |
| `Mods.file.*` | `file.lua` | staging-rooted file IO: `dofile`, `exec`/`exec_unsafe`, `exec_with_return`/`exec_unsafe_with_return`, `read_content`, `read_content_to_table`, `exists`, `get_file_path` |
| `Mods.hook` | `hook.lua` | the hook-chain system (`set`/`enable`/`remove`/`set_on_file`/`enable_by_file`, loadstring-driven) |
| `CLASS` | `class_patch.lua` | global registry of every `class()` result, built by monkey-patching `_G.class` (engine state classes are never bare `_G` globals — this is the only handle) |
| `__print` / `print` | entry | the engine's print, aliased as the global `__print` for loader/mod logging |

The wrapped global `require` (`require_wrap.lua`) additionally carries the
**deferred bridge**: after each `require`, it (a) one-shot installs the class
patch once `_G.class` is a function, and (b) flushes the deferred-hook queue.
That bridge is what lets pcall#1 code attach hooks to classes that don't exist
until late in boot.

## The DMF load sequence

DMF is always the first entry in the load order (the rite prepends `"dmf"`).
The sequence for DMF is the same as for any mod, just first:

1. The rite loads `dmf.mod` (`Mods.file.exec_with_return("dmf", "dmf", "mod")`).
2. It calls `dmf.mod`'s `run()`, which returns the `dmf_mod_object` — a **plain
   singleton table with `init`/`update`/`on_game_state_changed`, NOT a class
   instance** (no `:new()`; it is built directly inside DMF's loader).
3. The rite stores that object and calls `dmf_mod_object:init()` synchronously.
   That `init()` loads all of DMF's modules, which is what defines the global
   `new_mod`/`get_mod` the user mods rely on.
4. The rite then lands the DMF IO re-rooting (see [IO re-rooting](#io-re-rooting)).

User mods load after, each the same way. A user mod's `.mod` `run()` typically
calls `new_mod(...)` for its side effect and returns **nothing** — see
[Two-level driving](#two-level-driving).

## The `Managers.mod` shape contract DMF requires

DMF reads exactly three fields off `Managers.mod` (grepped across
`dmf/scripts/mods/dmf`):

| DMF read site | What it reads | When |
| --- | --- | --- |
| `dmf_mod_data.lua` | `Managers.mod._mods[Managers.mod._mod_load_index]` → `.id` / `.name` / `.handle` | synchronously, during `DMFMod:init()` — fired from a user mod's `run()` (via `new_mod`) and from DMF's own `init()` (via `create_mod("DMF")`) |
| `dmf_loader.lua` | `Managers.mod._state == "done"` | to fire DMF's `all_mods_loaded` event |
| `dmf_options.lua` | `Managers.mod._settings.developer_mode` | option registration |

Three consequences drive the rite's design:

- **Two-phase (scan-before-load).** DMF reads `_mods[_mod_load_index]` during a
  mod's own `init()`, and a user mod may also *walk* `_mods`. Pre-building the
  full table in SCAN means every entry exists before any mod's `run()`/`init()`
  reads it.
- **`_mod_load_index` is set per-mod.** The rite sets it to the current entry's
  index *before* its `run()`, so DMF's `new_mod` → `DMFMod:init()` (fired
  synchronously inside `run()` for user mods, inside `object:init()` for DMF)
  reads the right `_mods` entry. It is cleared after the loop.
- **`Managers.mod = self` is assigned *inside* `init()`.** DMF reads it
  synchronously during `init()`, *before* `:new()` returns and the lifecycle's
  own `Managers.mod = ModManager:new()` assignment completes. Assigning it here
  (and making the lifecycle's assignment `Managers.mod or ModManager:new()`
  idempotent) is what makes the DMF-visible contract hold during the load loop.

## IO re-rooting

This is the integration's one real adaptation, and it deserves to be called out
prominently.

**The problem.** DMF's `core/io.lua` hardcodes its mod directory as
`_mod_directory = "./../mods"` (DML heritage — the old loader staged mods next
to the game). Magos stages the user's mods under `MAGOS_MOD_PATH` (the staging
dir the launcher passes through). Unadapted, DMF would resolve every mod's
resources (its `mod_script` / `mod_data`) against the wrong root and miss.

DMF loads those resources via
`dmf_mod_manager.resolve_resource` → `dmf.safe_call_io_dofile` →
`mod:io_dofile_unsafe(...)`, all routing through that hardcoded local.

**The adaptation.** The rite keeps DMF **stock** (no vendored edits) and instead
overrides the mod-facing `DMFMod:io_*` methods to delegate to the matching
`Mods.file.*` (which is staging-rooted). See `_adapt_dmf_io()` in
`mod_manager.lua`. The eight overridden methods:

```text
DMFMod:io_dofile / io_dofile_unsafe          -> Mods.file.dofile
DMFMod:io_exec / io_exec_unsafe              -> Mods.file.exec / exec_unsafe
DMFMod:io_exec_with_return                   -> Mods.file.exec_with_return
DMFMod:io_exec_unsafe_with_return            -> Mods.file.exec_unsafe_with_return
DMFMod:io_read_content / io_read_content_to_table -> Mods.file.read_content(_to_table)
```

**When it lands.** The override is installed exactly once, *right after DMF's
`init()`* (which is what defines the `DMFMod:io_*` methods) and *before the
next mod's `run()`* (whose `new_mod` → `resolve_resource` → `mod:io_dofile_unsafe`
needs it). The rite checks `_adapt_dmf_io()` after every mod's `init()` and
gates on a one-shot `_dmf_io_adapted` flag, so it lands at exactly the right
slot (right after DMF, mod #1) without hard-coding `"dmf"` by name. It is a
no-op (returns false) until `DMFMod` exists.

**Scope + safety.** The override touches *only* the mod-facing IO surface
(`DMFMod:io_*`). DMF's *own internal* module loads use a staging-rooted local
(`io_dofile = Mods.file.dofile`, captured in `dmf_loader`), not these methods —
so this cannot break DMF's internal loads. It is idempotent + guarded and
targets the stable `DMFMod:io_*` API, so it survives routine DMF updates.

**Why this approach (and the alternatives).** The runtime-override choice keeps
DMF vendored unmodified — no fork to maintain, no reapply-on-update burden, and
the override targets a stable DMF API. The alternatives, considered and
rejected for now:

- **(a) Fork/patch the vendored DMF** to read its mod directory from a global
  (a one-line change in `core/io.lua`). Simple, but diverges from upstream and
  must be reapplied on every DMF update.
- **(b) Upstream a PR to DMF** making `_mod_directory` configurable. The
  cleanest long-term answer, but depends on DMF's maintainers and timing.

The runtime override is the current choice; (b) remains a future option if DMF
upstream is open to it.

## Two-level driving

There are two driving loops, and confusing them is the common bug:

- **Outer (the rite).** `Managers.mod:update(dt)` and `on_game_state_changed(...)`
  iterate `_mods` and call each entry's `object` callbacks — but **only for
  entries that have an `object`** (a `run()` that returned one).
- **Inner (DMF).** DMF's own `dmf_mod_object:update()` is itself one of those
  outer-driven objects, and *it* drives every DMF-registered user mod
  (`mods_update_event`).

So a user mod whose `.mod` `run()` **returns nil** registered itself via
`new_mod(...)` for its side effect (the typical DMF authoring pattern). The rite
treats that as a *success, not a failure*: it leaves `entry.object` nil (so the
outer update/state-change loops skip it) and keeps the scan-phase `_mods` entry
(so `_state` still reaches `"done"` and `_mod_load_index` accounting is
unchanged). That mod is **DMF-driven**, not outer-driven.

The flip side: a mod whose `run()` *does* return an object is **outer-driven** —
the rite calls its `init`/`update`/`on_game_state_changed` directly. DMF is the
canonical example.

## Deferred bootstrap

The rite does not run at pcall#1 — at pcall#1 the engine classes it needs
(`CLASS.BootStateRequireGameScripts`, `StateGame`, `GameStateMachine`) and
`Managers` do not exist yet. The rite runs inside a **deferred hook** on
`CLASS.BootStateRequireGameScripts._state_update`, installed via the
require-wrap + class-patch deferred-queue mechanism.

The full engine-context + deferral mechanism (the require-wrap, the class
monkey-patch, the deferred-hook queue, the pcall#1 → boot bridge) is documented
in `docs/architecture/RUNTIME.md` → "Engine-equivalent loader path" and the
shell/ subsection. The bootstrap hook body (in `lifecycle.lua`) is:

1. call the original `_state_update` (requires game scripts → `StateGame`
   created and registered in `CLASS`);
2. `Mods.file.dofile("mod_manager")` → `Managers.mod = ModManager:new()` → the
   rite loads DMF + every user mod;
3. install `CLASS.StateGame.update` hook → drives `Managers.mod:update(dt)`;
4. install `CLASS.GameStateMachine._change_state` hook → drives
   `on_game_state_changed` enter/exit.

Steps 2–4 are wrapped in a `pcall` so a bootstrap failure degrades to vanilla +
a log line (`[Enginseer] lifecycle bootstrap failed: …`), never a boot crash.

## Status

**Live-validated end-to-end.** The full chain runs in the real game: injection
→ trampoline `OK` at pcall#1 → Enginseer v2 loaded → class patch + deferred
bootstrap fire at `BootStateRequireGameScripts._state_update` → the rite loads
DMF (modules init) + the test mod → the test mod's hook fires in-game → the
game reaches `StateMainMenu`. No crashes; one bad mod degrades cleanly.

**Production DMF acquisition is future work.** Today DMF is vendored locally
under `runtime/enginseer/dmf/` (gitignored — local only). In production, the
mod manager (Darktide Magos) will download DMF and pass the staging path; the
runtime side of the integration described here is unchanged.

## References

- `runtime/enginseer/mod_manager.lua` — the rite (`ModManager:init`), the DMF
  shape contract, and `_adapt_dmf_io`.
- `runtime/enginseer/lifecycle.lua` — the deferred bootstrap hook.
- `runtime/enginseer/{file,hook,class_patch,require_wrap}.lua` — the loader API.
- `docs/architecture/RUNTIME.md` — the engine-context mechanism + the deferred
  bootstrap bridge + the launcher/shell contracts.
- `docs/reference/darktide-framework-analysis.md` — how the existing community
  toolchain (`patch_999` → `mod_loader` → DMF → mods) works; the rite replicates
  the `mod_loader` role.
