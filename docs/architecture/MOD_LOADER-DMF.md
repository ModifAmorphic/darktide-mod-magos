# mod_loader ↔ DMF — integration

> **Audience:** a future dev (or the operator) who needs to understand and
> maintain the integration between the mod loader and DMF
> (the Darktide Mod Framework). This is the dedicated reference for *how the
> two fit together*, especially the IO re-rooting and the load timing.
>
> **Prereq reading:** `docs/architecture/ENGINSEER.md` (the Enginseer runtime
> architecture + the engine-context mechanism + the deferred bootstrap), then
> this doc.

## The core model: who loads what

**The mod loader loads mods.** DMF is *just a mod* — a bag of helper APIs
(hooks, options, keybinds, `new_mod`/`get_mod`, …) that other mods opt into.
**DMF does not load mods; the mod loader does.**

This is the single most important thing to keep straight when reading either
codebase. The community's old chain was:

```text
patch_999 -> mod_loader  ->  DMF  ->  user mods
               (loads mods)    (a mod)
```

Magos replaces `patch_999` + the community `mod_loader` with its own mod loader
(shipped inside the Enginseer runtime), but keeps DMF in exactly the same role —
the first mod loaded, nothing more:

```text
DLL injection -> mod loader  ->  DMF (mod #1)  ->  user mods
                 (loads mods)    (a mod)
```

Concretely, the loader — not DMF — reads `mod_load_order.txt`, decides the order
(always `dmf` first), loads each `.mod`, calls `run()` / `init()`, and exposes
itself as `Managers.mod` to drive the per-frame + state-change lifecycle. DMF's
own `dmf_mod_object` is driven by the loader like any other top-level mod object;
DMF then drives *its* registered user mods through its inner update loop (see
[Two-level driving](#two-level-driving) below).

### Two roots (Enginseer-controlled vs user-controlled)

The mod loader and the mods live in **separate** directories, set as two globals
by the C trampoline before the entry opens:

- **Loader root** (`MOD_LOADER_DIR`; self-located by the shell from its own DLL
  path as `<dll-dir>\mod_loader\`, set as an **internal** global — not an env
  var/flag) — Enginseer-controlled. Holds `init.lua` + its modules (`file`,
  `hook`, `class_patch`, `require_wrap`, `lifecycle`, `mod_manager`).
  `make build` stages these into `bin/mod_loader/`. The entry's `bootstrap_load`
  (exposed as `Mods.load_module`) roots here.
- **Mod root** (`MAGOS_MOD_PATH`, from `--mod-path` / `DARKTIDE_MOD_PATH`;
  user/mod-manager-controlled) — holds DMF + user mods + `mod_load_order.txt`.
  `Mods.file.*` roots here (via `Mods._staging_base`). DMF and the mods never
  live under the loader root.

So the loader's own code is Enginseer-owned (ships with the build), while the
mods it loads are user-owned — the split keeps a DMF/mod update from requiring an
Enginseer rebuild and vice versa.

## The loader (`enginseer/mod_loader/mod_manager.lua`)

The loader is the `ModManager` class (a `class("ModManager")`, loaded via
`Mods.load_module("mod_manager")` from the deferred bootstrap hook —
see `lifecycle.lua`; `mod_manager.lua` is a loader module, so it loads from the
**loader root** `MOD_LOADER_DIR`, not the mod root). Loading is split across two
entry points:

- **`ModManager:init()`** — **SCAN only.** Read `mod_load_order.txt`
  (`Mods.file.read_content_to_table`), prepend `"dmf"`, and build the **entire**
  `_mods` table up front. Each entry is shaped
  `{ id, name, handle, enabled, state, object }`. **No mod is loaded here** —
  `init()` only scans. It also installs the one-shot DMF IO watch (see
  [IO re-rooting](#io-re-rooting)).
- **`ModManager:update(dt)`** — **LOAD on the first call**, then drive per-frame
  callbacks on every call. On the first call (`not self._mods_loaded`): run the
  LOAD loop — for each entry, in order, `exec_with_return` the mod's `.mod` file
  → call its `run()` (pcall-guarded) → if it returns an object, store it and
  call `object:init()` synchronously, **before the next mod loads** — then set
  `_mods_loaded = true` and `_state = "done"`. On every call (including the
  first, after the load): pump each loaded mod's `update(dt)`.

That per-mod run+init ordering (inside the LOAD loop) is what makes DMF's
`init()` define `new_mod`/`get_mod` before any user mod's `run()` runs.

`_state` is DMF's contract field (DMF reads `_state == "done"`). It is written
**exactly once** — `"done"` — when the load completes, and is `nil` before that
(DMF doesn't read any other value; nil-before-done is fine). The loader's own
"have I loaded?" flag is the **separate** instance field `_mods_loaded`
(loader-internal; DMF never reads it). Clean separation: `_state` = DMF's,
written once; `_mods_loaded` = the loader's.

`Managers.mod:update(dt)` and `Managers.mod:on_game_state_changed(status,
state_name, state_object)` are wired into `CLASS.StateGame.update` and
`CLASS.GameStateMachine._change_state` respectively by the deferred bootstrap
(see [Deferred bootstrap](#deferred-bootstrap)).

### Why load is deferred to the first `StateGame.update` tick

`init()` runs inside the `BootStateRequireGameScripts._state_update` hook —
**before `Managers.input` and other boot-complete globals exist.** Mods whose
`new_mod`/`init` touches those globals (e.g. Power_DI's option/keybind
validation reads `Managers.input`) would hit `nil` if loaded that early. The
LOAD is therefore deferred to the first `StateGame.update` tick, which fires
after boot, where those globals exist. The scan can safely run at boot (it reads
no engine globals, only `Mods.file`), so it stays in `init()` — pre-building the
full `_mods` table there means every entry exists before any mod's `run()`/`init()`
reads it (see [The `Managers.mod` shape contract](#the-managersmod-shape-contract-dmf-requires)).

### Fault isolation

Every mod's `run()`/`init()`/`update()`/`on_game_state_changed()` is
pcall-guarded. One bad mod logs (`[mod_loader] mod '<name>' <phase> failed: …`)
and the load continues to the next; a missing/unreadable `.mod` or a `.mod`
without `run()` is logged and skipped. The whole bootstrap is itself wrapped in
a pcall in `lifecycle.lua`, so a DMF/mod failure degrades to vanilla + a log
line, never a boot crash.

## Control flow

Lua runs **single-threaded**; the engine drives it at fixed points — a state's
`init()` once on entry, and `update(dt)` once per frame. The mod loader attaches
to two of those points and otherwise waits:

```text
boot:
  BootStateRequireGameScripts._state_update  (hooked)
    -> original runs (requires game scripts -> StateGame registered)
    -> loader bootstrap:
         load mod_manager, Managers.mod = ModManager:new()  -> init() SCANs
         install StateGame.update hook
         install GameStateMachine._change_state hook

first StateGame.update tick  (after boot; Managers.input now exists):
  StateGame.update  (hooked)
    -> Managers.mod:update(dt)
         not _mods_loaded -> LOAD: per-mod run()/init() in order
                              (DMF's init loads its modules; the IO watch
                               re-roots DMFMod:io_* mid-init — see below)
         _mods_loaded = true; _state = "done"
         drive each loaded mod's update(dt)

every subsequent StateGame.update tick:
  StateGame.update  (hooked)
    -> Managers.mod:update(dt)
         _mods_loaded already true -> just drive per-mod update(dt)

  (DMF is one of those mods. Its own per-frame update polls
   Managers.mod._state == "done"; the first time it sees it, DMF fires its
   all_mods_loaded event to its registered user mods.)
```

Same thread, frame by frame. `_state` is the **loader↔DMF hand-off**: the loader
writes `"done"` once the load finishes; DMF reads it on a later frame and reacts.
Nothing polls off-thread.

## Surfaces the mod loader provides for DMF/mods

DMF expects the loader's surfaces to already exist when it loads (it is not a
standalone guest). The mod loader builds all of these in Lua at pcall#1 (entry
`init.lua`) and during the deferred boot. They are the **loader API**; the load
logic is the loader **logic**.

| Surface | Provided by | Purpose |
| --- | --- | --- |
| `Mods.original_require` | entry | the engine's real `require` (captured before globals are stripped ~pcall#6) |
| `Mods.require_store` | entry + `require_wrap` | per-file require-result cache; populated by the wrapped `require` (enables DMF's `hook_require`) |
| `Mods.lua.io` / `Mods.lua.loadstring` | entry | the engine's real `io` / `loadstring` (captured before stripped) |
| `Mods.lua.os` / `Mods.lua.ffi` | entry | captured for DMF's debug modules (`table_dump`, dev console); `or` so nil-safe if absent |
| `Mods.file.*` | `file.lua` | mod-root-rooted file IO: `dofile`, `exec`/`exec_unsafe`, `exec_with_return`/`exec_unsafe_with_return`, `read_content`, `read_content_to_table`, `exists`, `get_file_path` |
| `Mods.hook` | `hook.lua` | the hook-chain system (`set`/`enable`/`remove`/`set_on_file`/`enable_by_file`, loadstring-driven) |
| `CLASS` | `class_patch.lua` | global registry of every `class()` result, built by monkey-patching `_G.class` (engine state classes are never bare `_G` globals — this is the only handle) |
| `__print` / `print` | entry | the engine's print, aliased as the global `__print` for loader/mod logging |

The wrapped global `require` (`require_wrap.lua`) additionally carries the
**deferred bridge**: after each `require`, it (a) one-shot installs the class
patch once `_G.class` is a function, and (b) flushes the deferred-hook queue.
That bridge is what lets pcall#1 code attach hooks to classes that don't exist
until late in boot.

## The DMF load sequence

DMF is always the first entry in the load order (the loader prepends `"dmf"`).
The sequence for DMF is the same as for any mod, just first — and it runs on the
first `StateGame.update` tick (not at boot):

1. The loader loads `dmf.mod` (`Mods.file.exec_with_return("dmf", "dmf", "mod")`).
2. It calls `dmf.mod`'s `run()`, which returns the `dmf_mod_object` — a **plain
   singleton table with `init`/`update`/`on_game_state_changed`, NOT a class
   instance** (no `:new()`; it is built directly inside DMF's loader).
3. The loader stores that object and calls `dmf_mod_object:init()` synchronously.
   That `init()` loads all of DMF's modules. DMF's `dmf_loader` loads them in two
   phases (this matters for [IO re-rooting](#io-re-rooting)):
   - **Phase 1** (incl. `core/io.lua`) via a local `io_dofile = Mods.file.dofile`
     (mod-root-rooted) — captured when `dmf_loader` runs.
   - **Phase 2** (`hooks`, `require`, `keybindings`, `options`, …) via
     `dmf:io_dofile(...)` — the `DMFMod:io_dofile` **method**, which `core/io.lua`
     roots at its hardcoded `./../mods`.
4. The IO watch (installed in `init()`) re-roots `DMFMod:io_*` mid-DMF-init —
   after `core/io.lua` defines them, before Phase-2 uses them (see
   [IO re-rooting](#io-re-rooting)).

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

Three consequences drive the loader's design:

- **Scan builds the full `_mods` table before any load.** DMF reads
  `_mods[_mod_load_index]` during a mod's own `init()`, and a user mod may also
  *walk* `_mods`. Building every entry up front in SCAN means they all exist
  before any mod's `run()`/`init()` reads the table — and SCAN can run at boot
  (it needs no engine globals), so it stays in `init()`.
- **`_mod_load_index` is set per-mod during the LOAD loop.** The loader sets it
  to the current entry's index *before* its `run()`, so DMF's `new_mod` →
  `DMFMod:init()` (fired synchronously inside `run()` for user mods, inside
  `object:init()` for DMF) reads the right `_mods` entry. It is cleared after
  the loop.
- **`Managers.mod = self` is assigned *inside* `init()`.** DMF reads it
  synchronously during the LOAD loop, which runs in `update()`. Assigning it in
  `init()` (and making the lifecycle's assignment
  `Managers.mod or ModManager:new()` idempotent) means the DMF-visible contract
  holds from the first tick.

## IO re-rooting

This is the integration's one real adaptation, and it deserves to be called out
prominently.

**The problem.** DMF's `core/io.lua` hardcodes its mod directory as
`_mod_directory = "./../mods"` (DML heritage — the old loader staged mods next
to the game). Magos stages the user's mods under `MAGOS_MOD_PATH` (the mod root
the launcher passes through). Unadapted, two things miss:

- DMF's **Phase-2 module loads** (`hooks`, `require`, …) go through
  `dmf:io_dofile(...)`, which routes through that hardcoded local.
- A user mod's **resources** (`mod_script` / `mod_data`) go through
  `dmf_mod_manager.resolve_resource` → `dmf.safe_call_io_dofile` →
  `mod:io_dofile_unsafe(...)`, also through that hardcoded local.

**The adaptation.** The loader keeps DMF **stock** (no vendored edits) and
instead overrides the mod-facing `DMFMod:io_*` methods to delegate to the matching
`Mods.file.*` (which is mod-root-rooted). See `_adapt_dmf_io()` in
`mod_manager.lua`. The eight overridden methods:

```text
DMFMod:io_dofile / io_dofile_unsafe          -> Mods.file.dofile
DMFMod:io_exec / io_exec_unsafe              -> Mods.file.exec / exec_unsafe
DMFMod:io_exec_with_return                   -> Mods.file.exec_with_return
DMFMod:io_exec_unsafe_with_return            -> Mods.file.exec_unsafe_with_return
DMFMod:io_read_content / io_read_content_to_table -> Mods.file.read_content(_to_table)
```

**When it lands — the one-shot `Mods.file.dofile` watch.** The re-root must land
**mid-DMF-init**: *after* `core/io.lua` defines `DMFMod:io_*` (Phase 1), but
*before* Phase-2 modules use the method. The loader achieves this with a one-shot
wrapper installed on `Mods.file.dofile` in `init()` (see
`_install_dmf_io_watch`), *before* any mod loads:

- DMF's `dmf_loader` captures `Mods.file.dofile` into its local `io_dofile` when
  it runs, so with the wrapper installed beforehand, Phase-1 `core/io` loads
  traverse the wrapper.
- After each dofile, the wrapper checks whether `DMFMod.io_dofile` just became a
  function. The dofile that loaded `core/io.lua` is the one that defines it — so
  right after that dofile returns, the wrapper calls `_adapt_dmf_io()` (re-rooting
  `DMFMod:io_*` to `Mods.file.*`), sets a one-shot flag, and **unwraps**
  (restores `Mods.file.dofile` to the original) so there is no permanent wrap.
- Phase-2 `dmf:io_dofile(...)` then uses the re-rooted method → resolves from the
  mod root.

This is the window an earlier approach missed: re-rooting in a post-init call
*inside the load loop* landed only after DMF's whole `init()` had run — by which
time Phase-2 had already used the stale `./../mods` method.

> **Unwrap note.** The wrapper restores the `Mods.file.dofile` *field* to the
> original. Any local that captured the wrapper (`dmf_loader`'s `io_dofile`)
> keeps calling through the wrapper closure — harmless, since the one-shot flag
> is now set and the wrapper is a thin pass-through.

**Scope + safety.** The override touches *only* the mod-facing IO surface
(`DMFMod:io_*`). DMF's *own Phase-1 internal* module loads use the
mod-root-rooted local (`io_dofile = Mods.file.dofile`, captured in `dmf_loader`),
not these methods — so this cannot break DMF's internal Phase-1 loads. It is
guarded and targets the stable `DMFMod:io_*` API, so it survives routine DMF
updates. If DMFMod never surfaces (no DMF / DMF load failed), the wrapper stays
installed as a thin pass-through and never fires — harmless.

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

- **Outer (the loader).** `Managers.mod:update(dt)` and
  `on_game_state_changed(...)` iterate `_mods` and call each entry's `object`
  callbacks — but **only for entries that have an `object`** (a `run()` that
  returned one).
- **Inner (DMF).** DMF's own `dmf_mod_object:update()` is itself one of those
  outer-driven objects, and *it* drives every DMF-registered user mod
  (`mods_update_event`).

So a user mod whose `.mod` `run()` **returns nil** registered itself via
`new_mod(...)` for its side effect (the typical DMF authoring pattern). The
loader treats that as a *success, not a failure*: it leaves `entry.object` nil
(so the outer update/state-change loops skip it) and keeps the scan-phase
`_mods` entry (so `_state` still reaches `"done"` and `_mod_load_index`
accounting is unchanged). That mod is **DMF-driven**, not outer-driven.

The flip side: a mod whose `run()` *does* return an object is **outer-driven** —
the loader calls its `init`/`update`/`on_game_state_changed` directly. DMF is the
canonical example.

## Deferred bootstrap

The loader does not run at pcall#1 — at pcall#1 the engine classes it needs
(`CLASS.BootStateRequireGameScripts`, `StateGame`, `GameStateMachine`) and
`Managers` do not exist yet. SCAN runs inside a **deferred hook** on
`CLASS.BootStateRequireGameScripts._state_update`, installed via the
require-wrap + class-patch deferred-queue mechanism; LOAD runs on the first
`StateGame.update` tick that same mechanism wires up.

The full engine-context + deferral mechanism (the require-wrap, the class
monkey-patch, the deferred-hook queue, the pcall#1 → boot bridge) is documented
in `docs/architecture/ENGINSEER.md` → "Engine-equivalent loader path" and the
shell/ subsection. The bootstrap hook body (in `lifecycle.lua`) is:

1. call the original `_state_update` (requires game scripts → `StateGame`
   created and registered in `CLASS`);
2. `Mods.load_module("mod_manager")` →
   `Managers.mod = ModManager:new()` → `init()` SCANs (reads `mod_load_order`,
   prepends `dmf`, builds `_mods`; installs the IO watch). **No mod loads here.**
3. install `CLASS.StateGame.update` hook → drives `Managers.mod:update(dt)` —
   the first tick LOADs (DMF + every user mod), every tick pumps per-mod
   `update(dt)`;
4. install `CLASS.GameStateMachine._change_state` hook → drives
   `on_game_state_changed` enter/exit.

DMF and the mods root at the **mod root** via `Mods.file.*` / `MAGOS_MOD_PATH`;
the loader itself is a loader module loaded from the loader root.

Steps 2–4 are wrapped in a `pcall` so a bootstrap failure degrades to vanilla +
a log line (`[mod_loader] lifecycle bootstrap failed: …`), never a boot crash.

## Status

**Live-validated to StateMainMenu (pre-fix).** The full chain runs in the real
game: injection → trampoline `OK` at pcall#1 → the mod loader loads → class patch
+ deferred bootstrap fire at `BootStateRequireGameScripts._state_update` → the
loader scans + loads DMF + the test mod → the test mod's hook fires in-game →
the game reaches `StateMainMenu`. No crashes; one bad mod degrades cleanly.

**Live validation pending (post-fix).** Two fixes in this branch
(scan-in-init/load-in-`StateGame.update`, and the IO-watch re-root) are
offline-tested but not yet run against the real engine. The pending checks:
DMF's **Phase-2 modules** load from the mod root (the `core/io.lua` `./../mods`
fallback no longer serves them), and a mod whose `init` touches
`Managers.input` (e.g. Power_DI's option/keybind setup) succeeds now that LOAD
runs after boot. The offline harness covers the logic but not the real
engine/managers.

**Production DMF acquisition is future work.** Today DMF is vendored locally
at the **mod root** (gitignored — local only; for live validation it lives in a
repo-root `mods/` dir pointed at by `--mod-path`). In production, the mod
manager (Darktide Magos) will download DMF into the mod root and pass that path
via `--mod-path`; the Enginseer-runtime side of the integration described here is
unchanged.

## References

- `enginseer/mod_loader/mod_manager.lua` — the loader (`ModManager:init` scan +
  IO-watch install, `ModManager:update` load + per-frame drive), the DMF shape
  contract, `_adapt_dmf_io`, and `_install_dmf_io_watch`.
- `enginseer/mod_loader/lifecycle.lua` — the deferred bootstrap hook.
- `enginseer/mod_loader/{file,hook,class_patch,require_wrap}.lua` — the loader API.
- `docs/architecture/ENGINSEER.md` — the engine-context mechanism + the deferred
  bootstrap bridge + the launcher/shell contracts.
- `docs/reference/darktide-framework-analysis.md` — how the existing community
  toolchain (`patch_999` → `mod_loader` → DMF → mods) works; the loader
  replicates the community `mod_loader` role.
