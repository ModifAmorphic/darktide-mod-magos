-- mod_manager.lua — the mod loader's driver (ModManager).
--
-- This module is the mod loader's driver. In the
-- deferred bootstrap hook (lifecycle.lua), it reads the user's mod_load_order,
-- prepends "dmf" (DMF is just the first mod — a bag of helper APIs other mods
-- opt into), and loads each mod: exec its `.mod` file, call its `run()`, and if
-- it yields a mod object store it + call `object:init()`. DMF loads first; its
-- `init()` defines `new_mod`/`get_mod` that the subsequent user mods' `run()`
-- calls — so the per-mod ordering (run+init before the next mod loads) is what
-- makes that dependency work.
--
-- Two authoring patterns: a mod whose `run()` returns an object is
-- OUTER-DRIVEN (the loader calls its init/update/on_game_state_changed). A mod
-- whose `run()` returns nil registered itself via `new_mod()` for its side
-- effect (the typical DMF pattern) and is DMF-DRIVEN — its update fires from
-- DMF's inner loop, not here, so entry.object stays nil and the outer update/
-- gsc loops skip it. Either way the scan-phase _mods entry is retained.
--
-- SCAN vs LOAD split: init() SCANs ONLY — it reads mod_load_order, prepends
-- "dmf", and builds the full _mods table, but loads NO mod. The LOAD (per-mod
-- run()/init()) is deferred to the first StateGame.update tick (via
-- Managers.mod:update), where boot-complete globals like Managers.input exist.
-- Mods whose new_mod/init touch those globals (e.g. Power_DI's option/keybind
-- validation) would otherwise hit nil mid-boot. The engine drives Lua
-- single-threaded at fixed points; the loader hooks
-- BootStateRequireGameScripts._state_update (create Managers.mod, scan) and
-- StateGame.update (drive Managers.mod:update -> load on the first tick, then
-- per-frame callbacks).
--
-- DMF-shape compatibility: DMF reads exactly three fields off Managers.mod
-- (grepped across dmf/scripts/mods/dmf):
--   - dmf_mod_data.lua:39  -> Managers.mod._mods[Managers.mod._mod_load_index],
--                             then .id / .name / .handle (read synchronously
--                             inside DMFMod:init — fired during a user mod's
--                             run() via new_mod, and during DMF's object:init()
--                             via dmf_mod_manager.lua's create_mod("DMF"));
--   - dmf_loader.lua:69    -> Managers.mod._state == "done";
--   - dmf_options.lua:253  -> Managers.mod._settings.developer_mode.
-- So the loader builds _mods entries shaped {id,name,handle,...}, sets
-- _mod_load_index to the currently-loading mod's index for the duration of the
-- load loop, and defaults _settings. _state is DMF's contract field, written
-- ONCE ("done") when mods are loaded; the loader's own "have I loaded?" flag is
-- the separate instance field _mods_loaded (DMF never reads it). Managers.mod is
-- assigned in init() (not by the lifecycle) so it's set before the load loop.
--
-- DMF IO re-rooting: DMF's dmf_loader loads Phase-1 modules (incl. core/io.lua)
-- via the local io_dofile = Mods.file.dofile (mod-root-rooted), but Phase-2
-- modules (hooks, require, keybindings, options, …) via dmf:io_dofile — the
-- DMFMod METHOD, which core/io.lua roots at its hardcoded "./../mods". To
-- re-root the method at the right moment — mid-DMF-init, after core/io.lua
-- defines DMFMod:io_* but BEFORE Phase-2 uses the method — init() installs a
-- one-shot wrapper on Mods.file.dofile: after each dofile, if DMFMod.io_dofile
-- just became a function, _adapt_dmf_io() re-roots DMFMod:io_* to Mods.file.*
-- (mod-root-rooted), the flag is set, and the wrapper unwraps (restores the
-- original dofile) so there's no permanent wrap. dmf_loader captured
-- Mods.file.dofile into its local io_dofile, so the Phase-1 core/io load
-- traverses the wrapper and the re-root lands before Phase-2. The old approach
-- (re-root in a post-init call inside the load loop) landed only after DMF's
-- whole init() — too late: Phase-2 had already used the stale method. See
-- _adapt_dmf_io + _install_dmf_io_watch.
--
-- Loaded via Mods.load_module("mod_manager") (rooted at MOD_LOADER_DIR), NOT
-- via the entry's bootstrap_load, because `class("ModManager")` only exists
-- after the class patch installs at boot time (pcall#1's require-wrap), not at
-- the entry's pcall#1. Runs in the real `_G` env (loadstring chunk default), so
-- the patched `class`, `Mods`, `CLASS`, `__print`, `Managers` globals all
-- resolve.
--
-- LIVE-VALIDATE: the full load against the real engine — that DMF's Phase-1 AND
-- Phase-2 modules load from the mod root, and that user mods' run()/init() work
-- with Managers.input ready. Offline, the harness mocks class/Mods.file/the mod
-- objects and validates the driver's ordering + fault isolation + DMF shape +
-- the wrapper timing.

local ModManager = class("ModManager")

local pcall = pcall
local table = table
local tostring = tostring
local type = type

-- One bad mod must not break the others or the engine. Each guarded call
-- returns true on success; on failure it logs via __print and returns false so
-- the load continues to the next mod. __print may be nil in exotic sandboxes;
-- fall back to the raw print.
local function _log(msg)
    if __print then
        __print(msg)
    elseif print then
        print(msg)
    end
end

local function _call_mod(object, phase, name, method, ...)
    local fn = object[method]
    if type(fn) ~= "function" then
        return true  -- a mod without this callback is fine; not a failure
    end
    local ok, err = pcall(fn, object, ...)
    if not ok then
        _log(("[mod_loader] mod '%s' %s failed: %s"):format(name, phase, tostring(err)))
        return false
    end
    return true
end

-- _adapt_dmf_io — re-root DMF's mod-facing IO at the Magos mod root.
--
-- WHY: DMF (kept stock — no vendored edits) hardcodes its mod directory as
-- "./../mods" in dmf/scripts/mods/dmf/modules/core/io.lua:14 (DML heritage).
-- Magos stages the user's mods under MAGOS_MOD_PATH instead, so a mod's
-- resources (mod_script / mod_data) AND DMF's own Phase-2 modules would
-- otherwise miss: they load via mod:io_dofile_unsafe / dmf:io_dofile, which
-- route through that hardcoded local. Replacing each DMFMod:io_* method with a
-- thin delegate to the matching Mods.file.* (mod-root-rooted) intercepts that
-- path and makes those loads resolve from the mod root.
--
-- SCOPE: this overrides ONLY the mod-facing IO surface (DMFMod:io_*). DMF's OWN
-- Phase-1 internal module loads use a mod-root-rooted local
-- `io_dofile = Mods.file.dofile` (captured in dmf_loader), NOT these methods —
-- so this cannot break DMF's internal Phase-1 loads. Targets the stable
-- DMFMod:io_* API, so it survives DMF updates.
--
-- Idempotent + guarded: a no-op (returns false) if DMFMod isn't a table yet or
-- Mods.file is missing; otherwise installs all 8 delegates and returns true.
-- The `self` arg DMF passes is dropped — Mods.file.* don't take it. The
-- delegates read `file.<fn>` (a field on the Mods.file table) at CALL time, so
-- they track whatever Mods.file.<fn> currently is (e.g. after the watch wrapper
-- unwraps) — they do NOT snapshot the function at adapt time. _adapt_dmf_io is
-- driven by the watch wrapper (_install_dmf_io_watch), which fires it at exactly
-- the right slot (right after core/io.lua defines DMFMod:io_*, mid-DMF-init).
local function _adapt_dmf_io()
    if type(DMFMod) ~= "table" then return false end
    if type(Mods) ~= "table" or type(Mods.file) ~= "table" then return false end
    local file = Mods.file

    DMFMod.io_dofile = function(self, fp)
        return file.dofile(fp)
    end
    DMFMod.io_dofile_unsafe = function(self, fp)
        return file.dofile(fp)
    end
    DMFMod.io_exec = function(self, lp, fn, ext, args)
        return file.exec(lp, fn, ext, args)
    end
    DMFMod.io_exec_unsafe = function(self, lp, fn, ext, args)
        return file.exec_unsafe(lp, fn, ext, args)
    end
    DMFMod.io_exec_with_return = function(self, lp, fn, ext, args)
        return file.exec_with_return(lp, fn, ext, args)
    end
    DMFMod.io_exec_unsafe_with_return = function(self, lp, fn, ext, args)
        return file.exec_unsafe_with_return(lp, fn, ext, args)
    end
    DMFMod.io_read_content = function(self, fp, ext)
        return file.read_content(fp, ext)
    end
    DMFMod.io_read_content_to_table = function(self, fp, ext)
        return file.read_content_to_table(fp, ext)
    end

    return true
end

-- ModManager:init — SCAN ONLY.
--
-- Reads mod_load_order, prepends "dmf", and builds the full _mods table, but
-- loads NO mod. The LOAD happens on the first StateGame.update tick (see
-- update()), where boot-complete globals like Managers.input exist — loading
-- here (inside BootStateRequireGameScripts._state_update) was too early and
-- broke mods whose init reads Managers.input.
--
-- _state is DMF's contract field; it is NOT set here — it's written once
-- ("done") when the load completes (see update). nil before that is fine (DMF
-- only reads _state == "done"). The loader's own "have I loaded?" flag is the
-- separate field _mods_loaded (loader-internal; DMF never reads it).
--
-- Also installs the one-shot DMF IO watch (see _install_dmf_io_watch) so the
-- re-root lands mid-DMF-init when the LOAD eventually runs.
--
-- Managers.mod is assigned here (not by the lifecycle) so it's set before the
-- load loop runs; the lifecycle's `Managers.mod or ModManager:new()` is
-- idempotent on re-entry.
function ModManager:init()
    Managers = Managers or {}
    Managers.mod = self

    self._mods = {}
    self._settings = { log_level = 1, developer_mode = false }
    self._mods_loaded = false
    self._dmf_io_adapted = false

    -- SCAN: read the user's load order and build the full _mods table up front
    -- (id/name/handle/enabled/state/object), so every entry exists before any
    -- mod's run()/init() reads it. nil (missing file) -> empty, so a bare
    -- DMF-only bootstrap still works. Mirrors DML's _build_mod_table entry shape
    -- (id=i, name=mod_name, handle=mod_name); DMF only reads id/name/handle.
    local order = Mods.file.read_content_to_table("mod_load_order", "txt") or {}
    table.insert(order, 1, "dmf")
    for i, name in ipairs(order) do
        self._mods[i] = {
            id = i,
            name = name,
            handle = name,
            enabled = true,
            state = "not_loaded",
            object = nil,
        }
    end

    self:_install_dmf_io_watch()
end

-- _install_dmf_io_watch — wrap Mods.file.dofile with a one-shot that re-roots
-- DMF's mod-facing IO (DMFMod:io_*) the moment core/io.lua defines it.
--
-- DMF's dmf_loader captures `Mods.file.dofile` into a local `io_dofile` when it
-- runs, then loads Phase-1 modules (incl. core/io.lua, which defines
-- DMFMod:io_* rooted at "./../mods") through that local, and Phase-2 modules
-- via the DMFMod:io_* method. With this watch installed BEFORE DMF loads, the
-- Phase-1 core/io load traverses the wrapper; right after it returns (DMFMod.
-- io_dofile now a function), _adapt_dmf_io re-roots the methods to Mods.file.*
-- (mod-root-rooted), the one-shot flag is set, and the wrapper unwraps — so
-- Phase-2's dmf:io_dofile uses the re-rooted method. This is the window the old
-- post-init call missed (it ran only after DMF's whole init(), by which time
-- Phase-2 had already used the stale method).
--
-- Unwrap note: the wrapper restores the Mods.file.dofile FIELD to the original.
-- Any local that captured the wrapper (dmf_loader's `io_dofile`) keeps calling
-- through the wrapper closure — harmless, since _dmf_io_adapted is now true and
-- the wrapper is a thin pass-through.
--
-- No-op if Mods.file.dofile isn't a function (defensive). If DMFMod never
-- surfaces (no DMF / DMF load failed), the wrapper stays installed as a thin
-- pass-through and never fires — harmless.
function ModManager:_install_dmf_io_watch()
    local file = Mods.file
    if type(file) ~= "table" or type(file.dofile) ~= "function" then return end
    local original_dofile = file.dofile
    local manager = self

    file.dofile = function(fp, ...)
        local result = original_dofile(fp, ...)
        if not manager._dmf_io_adapted
            and type(DMFMod) == "table"
            and type(DMFMod.io_dofile) == "function"
            and _adapt_dmf_io() then
            manager._dmf_io_adapted = true
            file.dofile = original_dofile  -- unwrap; no permanent wrap
        end
        return result
    end
end

-- _load_mods — the LOAD phase: drive each mod's run() -> object -> init() in
-- order. Called once, on the first StateGame.update tick (see update()).
--
-- _mod_load_index is set to the entry's index BEFORE run() so DMF's new_mod ->
-- DMFMod:init (fired synchronously inside a mod's run() for user mods, inside
-- object:init() for DMF) reads the right _mods entry. Cleared after the loop.
function ModManager:_load_mods()
    for i, entry in ipairs(self._mods) do
        self._mod_load_index = i

        -- Load the `.mod` file -> the mod_data table (with .run).
        -- exec_with_return returns false/nil on missing file / parse error.
        local mod_data = Mods.file.exec_with_return(entry.name, entry.name, "mod")
        if not mod_data then
            _log(("[mod_loader] mod '%s' load failed: .mod file missing or unreadable"):format(entry.name))
        elseif type(mod_data.run) ~= "function" then
            _log(("[mod_loader] mod '%s' load failed: .mod has no run() function"):format(entry.name))
        else
            -- run() yields the mod object. A run() error logs + skips this mod
            -- (must not abort the load). A successful nil return is the BENIGN
            -- DMF-managed case — see the object == nil branch below.
            local ok, object = pcall(mod_data.run)
            if not ok then
                _log(("[mod_loader] mod '%s' run failed: %s"):format(entry.name, tostring(object)))
            elseif object == nil then
                -- DMF-managed mod: run() registered the mod via new_mod() (the
                -- typical DMF authoring pattern — call new_mod for its side
                -- effect, return nothing) so it's driven by DMF's inner update
                -- loop, NOT this loader's outer update/on_game_state_changed.
                -- This is a success, not a failure: leave entry.object nil so
                -- the outer update/gsc loops skip it (DMF drives it), mark
                -- entry.state = "dmf_driven" (loaded, but driven by DMF's inner
                -- loop — distinct from "not_loaded" which means a failure/miss),
                -- and keep the scan-phase _mods entry so _state still reaches
                -- "done" and _mod_load_index accounting is unchanged. DMF reads
                -- _mods[_mod_load_index].id/.name/.handle during run()'s new_mod
                -- call, and the scan-phase entry still resolves those.
                entry.state = "dmf_driven"
                _log(("[mod_loader] mod '%s' loaded (no top-level object; DMF-driven)"):format(entry.name))
            else
                entry.object = object
                entry.state = "running"
                -- init() runs BEFORE the next mod loads — that ordering is what
                -- lets DMF's init() define new_mod/get_mod before user mods run.
                _call_mod(object, "init", entry.name, "init")
            end
        end
    end

    self._mod_load_index = nil
end

-- ModManager:update — load on the first tick, then pump every loaded mod's
-- per-frame update on every tick.
--
-- The load is deferred to the first StateGame.update (after boot, where
-- Managers.input etc. exist). On the first call: run _load_mods(), then set
-- _mods_loaded + _state = "done" (DMF's contract field, written once). On every
-- call (including the first, after the load): drive each entry's update(dt).
--
-- Mods without an update() (and ones whose update() errors) are skipped
-- silently/in-log; one failure never stops the others. Entries without an
-- object (failed mods + DMF-driven mods whose run() returned nil) are skipped.
function ModManager:update(dt)
    if not self._mods_loaded then
        self:_load_mods()
        self._mods_loaded = true
        self._state = "done"
    end

    for _, entry in ipairs(self._mods) do
        if entry.object then
            _call_mod(entry.object, "update", entry.name, "update", dt)
        end
    end
end

-- ModManager:on_game_state_changed — fan enter/exit notifications out to every
-- loaded mod that opted into on_game_state_changed. Args are forwarded verbatim.
-- Entries without an object (failed mods + DMF-driven mods whose run() returned
-- nil) are skipped.
function ModManager:on_game_state_changed(status, state_name, state_object)
    for _, entry in ipairs(self._mods) do
        if entry.object then
            _call_mod(entry.object, "on_game_state_changed", entry.name,
                "on_game_state_changed", status, state_name, state_object)
        end
    end
end

-- ModManager:destroy — unload each loaded mod that opted into on_unload.
--
-- DMF hooks CLASS.ModManager.destroy (dmf_loader.lua:53) to fire its
-- mods_unload_event when the mod manager is destroyed. Without a destroy()
-- here, DMF can't attach that hook — it queues a delayed hook that never
-- resolves (the live "[MOD][DMF][ERROR] (hook): trying to hook function or
-- method that doesn't exist: [ModManager.destroy]"). With destroy() present,
-- DMF's hook attaches; when destroy() is invoked (game shutdown), DMF's
-- wrapped hook fires mods_unload_event FIRST, then this body unloads the mods.
--
-- For each loaded mod (entry.object non-nil) that has an on_unload callback,
-- pcalls it. Mirrors the update/on_game_state_changed fault-isolation pattern
-- via _call_mod: one bad mod's on_unload logs + doesn't stop the others. Mods
-- without on_unload (the common case) are skipped silently — not a failure.
-- Entries without an object (failed mods + DMF-driven mods whose run() returned
-- nil) are skipped, as in update/on_game_state_changed.
function ModManager:destroy()
    for _, entry in ipairs(self._mods) do
        if entry.object then
            _call_mod(entry.object, "on_unload", entry.name, "on_unload")
        end
    end
end

return ModManager
