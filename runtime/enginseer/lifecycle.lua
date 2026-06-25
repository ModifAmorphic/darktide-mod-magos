-- lifecycle.lua — the deferred-hook queue + the bootstrap lifecycle hook.
--
-- The deferred mechanism bridges pcall#1 (where the Enginseer runs, before
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
-- (which requires game scripts -> StateGame created), then loads DMF, assigns
-- Managers.mod, and installs the per-frame + state-change hooks. DMF then reads
-- mod_load_order.txt and loads mods.
--
-- LIVE-VALIDATE: that BootStateRequireGameScripts._state_update resolves + fires
-- at the right boot point and that the DMF load succeeds.

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
        mod_name = mod_name or "Enginseer",
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

            -- Everything below is Enginseer's bootstrap. Guard it so a failure
            -- (missing/mis-pathed DMF, ModManager:new() raising, an inner hook
            -- not resolving) degrades cleanly to vanilla + a log line instead
            -- of propagating through the engine's _state_update and crashing
            -- the game at boot. Always return the original's result regardless.
            local ok, err = pcall(function()
                -- Load DMF's ModManager and assign Managers.mod. DMF then reads
                -- mod_load_order.txt and loads mods.
                -- LIVE-VALIDATE: the dmf_loader path resolves in our staging
                -- layout and returns DMF's ModManager class.
                local ModManager = Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")
                Managers = Managers or {}
                Managers.mod = Managers.mod or ModManager:new()

                -- Per-frame update hook on StateGame: pump the ModManager each frame.
                Mods.hook.set("Enginseer", "CLASS.StateGame.update", function(func, self_obj, dt, ...)
                    Managers.mod:update(dt)
                    return func(self_obj, dt, ...)
                end)

                -- State-change hook on GameStateMachine: fan out enter/exit
                -- events to mods (on_game_state_changed).
                Mods.hook.set("Enginseer", "CLASS.GameStateMachine._change_state", function(func, self_obj, ...)
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
                __print("[Enginseer] lifecycle bootstrap failed: " .. tostring(err))
            end
            return state_update_result
        end,
        "Enginseer"
    )
end
