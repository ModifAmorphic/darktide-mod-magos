-- lifecycle.lua — the deferred-hook queue + the bootstrap lifecycle hook.
--
-- The deferred mechanism bridges pcall#1 (where the mod loader runs, before
-- main.lua) to the engine's late boot (where the targets we need to hook
-- finally appear). We can't hook Main.init — it's a plain-table method defined
-- and invoked once during pcall#2, after we've already returned. Instead, the
-- require-wrap (installed at pcall#1) calls Mods.flush_deferred_hooks() after
-- every require: each pending hook whose target now resolves (loadstring
-- evaluation returns a function) gets installed via Mods.hook.set and dequeued.
-- This retries on every require until the target class/method exists — no
-- polling, no specific require-string matching.
--
-- The bootstrap hook (Mods.install_lifecycle_hooks) mirrors DML's
-- init_mod_framework: a deferred hook on
-- CLASS.BootStateRequireGameScripts._state_update that runs AFTER the original
-- (which requires game scripts -> StateGame created), then loads the mod loader's
-- mod_manager (the loader driver), assigns Managers.mod (whose :init() reads
-- mod_load_order, prepends "dmf", and builds the _mods table — the SCAN), and
-- installs the per-frame + state-change hooks. The LOAD itself (per-mod
-- run()/init()) does NOT happen here: it is deferred to the first
-- StateGame.update tick (driven by the per-frame hook below), where
-- boot-complete globals like Managers.input exist — loading inside
-- _state_update was too early and broke mods whose init reads Managers.input.
-- DMF loads as the first mod on that first tick; it is NOT loaded here directly.
--
-- LIVE-VALIDATE: that BootStateRequireGameScripts._state_update resolves + fires
-- at the right boot point, that StateGame.update drives the load on its first
-- tick (after boot, Managers.input present), and that the DMF load succeeds.

local _loadstring = Mods.lua.loadstring
local pcall = pcall
local type = type
local tostring = tostring
local table = table

Mods._deferred_hooks = Mods._deferred_hooks or {}

--
-- Queue a hook for deferred install. The hook attaches once `func_name`
-- resolves to a function (checked on every Mods.flush_deferred_hooks call from
-- the require wrap). Until then it stays in the queue.
--
Mods.queue_deferred_hook = function(func_name, hook_func, mod_name)
    table.insert(Mods._deferred_hooks, {
        func_name = func_name,
        hook_func = hook_func,
        mod_name = mod_name or "mod_loader",
    })
end

--
-- Try to install every pending deferred hook. For each, resolve func_name via
-- the same loadstring trick Mods.hook._get_func uses; if the result is a
-- function, the target exists -> install via Mods.hook.set and dequeue. Errors
-- (e.g. CLASS or an intermediate is nil) are caught by pcall and treated as
-- "not ready" so the hook stays queued.
--
-- PERF NOTE: Mods.hook._patch re-patches ALL installed hooks on each dequeue
-- (it's called via Mods.hook.set once per resolved deferred hook). Re-patches
-- all hooks on each dequeue; measure during live boot, batch if hot.
-- (LIVE-VALIDATE / measure-later.)
Mods.flush_deferred_hooks = function()
    if not Mods.hook then return end
    local i = 1
    while i <= #Mods._deferred_hooks do
        local entry = Mods._deferred_hooks[i]
        local ok, func = pcall(function()
            return _loadstring("return " .. entry.func_name)()
        end)
        if ok and type(func) == "function" then
            Mods.hook.set(entry.mod_name, entry.func_name, entry.hook_func)
            table.remove(Mods._deferred_hooks, i)
            -- Don't increment i: the next entry shifted into this slot.
        else
            i = i + 1
        end
    end
end

--
-- Queue the bootstrap lifecycle hook. Mirrors DML's init_mod_framework body.
-- Once CLASS.BootStateRequireGameScripts._state_update exists (created late in
-- the boot state sequence), the deferred flush installs this wrapper:
--   1. call the original _state_update (requires game scripts -> StateGame
--      created and registered in CLASS);
--   2. load DMF's ModManager via Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")
--      and assign Managers.mod;
--   3. install the per-frame CLASS.StateGame.update hook (drives Managers.mod:update);
--   4. install the CLASS.GameStateMachine._change_state hook (drives
--      on_game_state_changed enter/exit notifications).
-- LIVE-VALIDATE: that StateGame + GameStateMachine are in CLASS by the time the
-- hook body runs (they're created during _state_update's script requires). If
-- not, the inner Mods.hook.set calls will fail in _get_func and the bootstrap
-- errors out — DML has the same constraint and it's proven there.
--
Mods.install_lifecycle_hooks = function()
    Mods.queue_deferred_hook(
        "CLASS.BootStateRequireGameScripts._state_update",
        function(state_update_func, self, ...)
            -- Run the original first (UNGUARDED — it's the engine's function):
            -- this requires the game scripts, which creates StateGame and
            -- registers it in CLASS.
            local state_update_result = state_update_func(self, ...)

            -- Everything below is the mod loader's bootstrap. Guard it so a failure
            -- (missing/mis-pathed mod_manager, ModManager:new() raising, an inner
            -- hook not resolving) degrades cleanly to vanilla + a log line instead
            -- of propagating through the engine's _state_update and crashing
            -- the game at boot. Always return the original's result regardless.
            local ok, err = pcall(function()
                -- Load the loader driver (the mod loader's mod manager).
                -- mod_manager.lua is a loader module, so it loads from the
                -- loader root (MOD_LOADER_DIR) via Mods.load_module, NOT
                -- Mods.file.dofile (which is mod-rooted). It must load here —
                -- not at the entry's bootstrap_load — because it calls
                -- class("ModManager"), which only exists after the class patch
                -- installs at boot (the require-wrap), not at the entry's
                -- pcall#1. :init() reads mod_load_order, prepends "dmf", and
                -- builds the _mods table (the SCAN); it loads NO mod. The LOAD
                -- runs on the first StateGame.update tick (the per-frame hook
                -- installed below), where Managers.input exists. DMF/mods/
                -- mod_load_order root at the MOD dir via Mods.file.*
                -- (MAGOS_MOD_PATH); _state reaches "done" once the load
                -- completes.
                -- LIVE-VALIDATE: the full load end-to-end (DMF init loads all
                -- its Phase-1 + Phase-2 modules from the mod root; user mods'
                -- run/init work with Managers.input ready) against the real
                -- engine.
                local ModManager = Mods.load_module("mod_manager")
                Managers = Managers or {}
                Managers.mod = Managers.mod or ModManager:new()

                -- Per-frame update hook on StateGame: drive the ModManager each
                -- frame. The FIRST tick runs the LOAD (Managers.mod:update
                -- loads DMF + every user mod, then sets _state="done"); every
                -- tick (including the first) pumps each loaded mod's update(dt).
                Mods.hook.set("mod_loader", "CLASS.StateGame.update", function(func, self_obj, dt, ...)
                    Managers.mod:update(dt)
                    return func(self_obj, dt, ...)
                end)

                -- State-change hook on GameStateMachine: fan out enter/exit
                -- events to mods (on_game_state_changed).
                Mods.hook.set("mod_loader", "CLASS.GameStateMachine._change_state", function(func, self_obj, ...)
                    local old_state = self_obj._state
                    local old_state_name = old_state and self_obj:current_state_name()
                    if old_state_name then
                        Managers.mod:on_game_state_changed("exit", old_state_name, old_state)
                    end

                    local result = func(self_obj, ...)

                    local new_state = self_obj._state
                    local new_state_name = new_state and self_obj:current_state_name()
                    if new_state_name then
                        Managers.mod:on_game_state_changed("enter", new_state_name, new_state)
                    end

                    return result
                end)
            end)
            if not ok then
                __print("[mod_loader] lifecycle bootstrap failed: " .. tostring(err))
            end
            return state_update_result
        end,
        "mod_loader"
    )
end
