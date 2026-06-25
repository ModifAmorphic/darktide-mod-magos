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
-- Ports the ROLE of DML's ModManager (read order -> load each `.mod` -> run() ->
-- init() -> drive update/on_game_state_changed + `_state`). Drops DML's
-- GUI/reload/network/settings/one-mod-per-frame machinery: we load
-- synchronously in `init` (DMF only needs `_state == "done"` eventually, which
-- we reach as soon as the loop finishes).
--
-- Loaded via Mods.file.dofile("mod_manager") (rooted at MAGOS_STAGING), NOT via
-- the entry's bootstrap_load, because `class("ModManager")` only exists after
-- the class patch installs at boot time (pcall#1's require-wrap), not at the
-- entry's pcall#1. Runs in the real `_G` env (loadstring chunk default), so the
-- patched `class`, `Mods`, `CLASS`, `__print` globals all resolve.
--
-- LIVE-VALIDATE: the full rite against the real engine — that DMF's
-- dmf_mod_object:init() succeeds end-to-end (loads all its modules) and user
-- mods' run()/init() work. Offline, the harness mocks class/Mods.file/the mod
-- objects and validates the driver's ordering + fault isolation only.

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
-- State machine: "scanning" (read order) -> "loading" (load each mod) ->
-- "done". DMF is always prepended to the order so it loads first; its `init()`
-- installs new_mod/get_mod that user mods' run() relies on.
function ModManager:init()
    self._mods = {}
    self._state = "scanning"

    -- Read the user's load order. nil (missing file) is treated as empty so a
    -- bare DMF-only bootstrap still works.
    local order = Mods.file.read_content_to_table("mod_load_order", "txt") or {}
    table.insert(order, 1, "dmf")

    self._state = "loading"

    for _, name in ipairs(order) do
        -- Load the `.mod` file -> the mod_data table (with .run). exec_with_return
        -- returns false on missing file / parse error; log + skip.
        local mod_data = Mods.file.exec_with_return(name, name, "mod")
        if not mod_data then
            _log(("[Enginseer] mod '%s' load failed: .mod file missing or unreadable"):format(name))
        elseif type(mod_data.run) ~= "function" then
            _log(("[Enginseer] mod '%s' load failed: .mod has no run() function"):format(name))
        else
            -- run() yields the mod object. A run() error logs + skips this mod
            -- but must not abort the rite.
            local ok, object = pcall(mod_data.run)
            if not ok then
                _log(("[Enginseer] mod '%s' run failed: %s"):format(name, tostring(object)))
            elseif object == nil then
                _log(("[Enginseer] mod '%s' run returned no object; skipped"):format(name))
            else
                local entry = { name = name, object = object }
                table.insert(self._mods, entry)
                -- init() runs BEFORE the next mod loads — that ordering is what
                -- lets DMF's init() define new_mod/get_mod before user mods run.
                _call_mod(object, "init", name, "init")
            end
        end
    end

    self._state = "done"
end

-- ModManager:update — pump every loaded mod's per-frame update. Mods without an
-- update() (and ones whose update() errors) are skipped silently/in-log; one
-- failure never stops the others.
function ModManager:update(dt)
    for _, entry in ipairs(self._mods) do
        _call_mod(entry.object, "update", entry.name, "update", dt)
    end
end

-- ModManager:on_game_state_changed — fan enter/exit notifications out to every
-- loaded mod that opted into on_game_state_changed. Args are forwarded verbatim.
function ModManager:on_game_state_changed(status, state_name, state_object)
    for _, entry in ipairs(self._mods) do
        _call_mod(entry.object, "on_game_state_changed", entry.name,
            "on_game_state_changed", status, state_name, state_object)
    end
end

return ModManager
