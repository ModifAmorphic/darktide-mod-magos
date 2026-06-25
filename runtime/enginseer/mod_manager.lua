-- mod_manager.lua — the Enginseer's rite: loading mods.
--
-- The Enginseer IS the mod loader. This module is the driver that, in the
-- deferred bootstrap hook (lifecycle.lua), reads the user's mod_load_order,
-- prepends "dmf" (DMF is just the first mod — a bag of helper APIs other mods
-- opt into), and loads each mod synchronously: exec its `.mod` file, call its
-- `run()` to get the mod object, store it, then call `object:init()`. DMF loads
-- first; its `init()` defines `new_mod`/`get_mod` that the subsequent user
-- mods' `run()` calls — so the per-mod ordering (run+init before the next mod
-- loads) is what makes that dependency work.
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
-- So the rite builds _mods entries shaped {id,name,handle,...}, sets
-- _mod_load_index to the currently-loading mod's index for the duration of the
-- load loop, and defaults _settings. Managers.mod is assigned HERE (not by the
-- lifecycle) because DMF reads it synchronously during init(), before :new()
-- returns and the lifecycle's `Managers.mod = ModManager:new()` completes.
--
-- Two-phase rite: SCAN (read order -> build every _mods entry up front) then
-- LOAD (drive each entry's run()/init()). Pre-building the full table means
-- every entry exists before any mod's run()/init() reads it — DMF reads
-- _mods[_mod_load_index] for the current mod, and a user mod may walk _mods.
--
-- Ports the ROLE of DML's ModManager (read order -> load each `.mod` -> run() ->
-- init() -> drive update/on_game_state_changed + `_state`). Drops DML's
-- GUI/reload/network/one-mod-per-frame machinery: we load synchronously in
-- `init` (DMF only needs `_state == "done"` eventually, which we reach as soon
-- as the loop finishes). _settings is defaulted (NOT loaded from
-- Application.user_setting — DMF owns persisting it via dmf_options.lua).
--
-- Loaded via Mods.file.dofile("mod_manager") (rooted at MAGOS_STAGING), NOT via
-- the entry's bootstrap_load, because `class("ModManager")` only exists after
-- the class patch installs at boot time (pcall#1's require-wrap), not at the
-- entry's pcall#1. Runs in the real `_G` env (loadstring chunk default), so the
-- patched `class`, `Mods`, `CLASS`, `__print`, `Managers` globals all resolve.
--
-- LIVE-VALIDATE: the full rite against the real engine — that DMF's
-- dmf_mod_object:init() succeeds end-to-end (loads all its modules) and user
-- mods' run()/init() work. Offline, the harness mocks class/Mods.file/the mod
-- objects and validates the driver's ordering + fault isolation + DMF shape.

local ModManager = class("ModManager")

local pcall = pcall
local table = table
local tostring = tostring
local type = type

-- One bad mod must not break the others or the engine. Each guarded call
-- returns true on success; on failure it logs via __print and returns false so
-- the rite continues to the next mod. __print may be nil in exotic sandboxes;
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
        _log(("[Enginseer] mod '%s' %s failed: %s"):format(name, phase, tostring(err)))
        return false
    end
    return true
end

-- ModManager:init — the rite.
--
-- State machine: "scanning" (read order + build _mods) -> "loading" (load each
-- mod) -> "done". DMF is always prepended to the order so it loads first; its
-- `init()` installs new_mod/get_mod that user mods' run() relies on.
--
-- Managers.mod is assigned here (not by the lifecycle) because DMF reads it
-- synchronously during this init(), before :new() returns and the lifecycle's
-- `Managers.mod = ModManager:new()` assignment completes. Idempotent: on re-entry
-- the lifecycle's `Managers.mod or ModManager:new()` short-circuits (already set).
function ModManager:init()
    Managers = Managers or {}
    Managers.mod = self

    self._mods = {}
    self._settings = { log_level = 1, developer_mode = false }
    self._state = "scanning"

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

    -- LOAD: drive each mod's run() -> object -> init() in order. _mod_load_index
    -- is set to the entry's index BEFORE run() so DMF's new_mod -> DMFMod:init
    -- (fired synchronously inside a mod's run() for user mods, inside
    -- object:init() for DMF) reads the right _mods entry.
    self._state = "loading"
    for i, entry in ipairs(self._mods) do
        self._mod_load_index = i

        -- Load the `.mod` file -> the mod_data table (with .run). exec_with_return
        -- returns false/nil on missing file / parse error; log + skip.
        local mod_data = Mods.file.exec_with_return(entry.name, entry.name, "mod")
        if not mod_data then
            _log(("[Enginseer] mod '%s' load failed: .mod file missing or unreadable"):format(entry.name))
        elseif type(mod_data.run) ~= "function" then
            _log(("[Enginseer] mod '%s' load failed: .mod has no run() function"):format(entry.name))
        else
            -- run() yields the mod object. A run() error (or nil result) logs +
            -- skips this mod but must not abort the rite.
            local ok, object = pcall(mod_data.run)
            if not ok then
                _log(("[Enginseer] mod '%s' run failed: %s"):format(entry.name, tostring(object)))
            elseif object == nil then
                _log(("[Enginseer] mod '%s' run returned no object; skipped"):format(entry.name))
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
    self._state = "done"
end

-- ModManager:update — pump every loaded mod's per-frame update. Mods without an
-- update() (and ones whose update() errors) are skipped silently/in-log; one
-- failure never stops the others. Entries without an object (failed/skipped
-- mods) are skipped.
function ModManager:update(dt)
    for _, entry in ipairs(self._mods) do
        if entry.object then
            _call_mod(entry.object, "update", entry.name, "update", dt)
        end
    end
end

-- ModManager:on_game_state_changed — fan enter/exit notifications out to every
-- loaded mod that opted into on_game_state_changed. Args are forwarded verbatim.
-- Entries without an object (failed/skipped mods) are skipped.
function ModManager:on_game_state_changed(status, state_name, state_object)
    for _, entry in ipairs(self._mods) do
        if entry.object then
            _call_mod(entry.object, "on_game_state_changed", entry.name,
                "on_game_state_changed", status, state_name, state_object)
        end
    end
end

return ModManager
