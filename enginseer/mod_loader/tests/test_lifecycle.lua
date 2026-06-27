-- test_lifecycle.lua — the deferred-hook queue + bootstrap lifecycle hook.
--
-- Coverage (acceptance criteria from the spec):
--   - queue_deferred_hook + flush_deferred_hooks: a hook whose target doesn't
--     resolve yet stays queued; once the target is added to the env, the next
--     flush installs it (via Mods.hook.set) and dequeues.
--   - Simulate BootStateRequireGameScripts._state_update appearing late and
--     verify the bootstrap hook attaches; then exercise the bootstrap body to
--     confirm Managers.mod assignment + the StateGame.update / GameStateMachine
--     _change_state inner hooks install and fire.
--
-- IMPORTANT: the bootstrap path exercises the REAL Mods.load_module (the
-- dofile-style loader the entry exposes), NOT a mock. A fake mod_manager.lua is
-- staged at the loader mock root that returns whatever the sandbox global
-- __FAKE_MOD_MANAGER points to at load time — so each test can drive the real
-- loader -> real return value -> ModManager:new() path (the exact path the
-- boolean-vs-value regression broke) by setting __FAKE_MOD_MANAGER, without
-- mocking the loader itself. (Regression: load_module was aliased to
-- bootstrap_load and returned true, so ModManager:new() raised "attempt to index
-- local 'ModManager' (a boolean value)".)

local mock = require("mock")

return function(runner)
    -- Build a sandbox with the REAL entry loaded (so Mods.load_module is the
    -- real dofile-style loader, not a mock). The entry also wires hook/lifecycle/
    -- require-wrap; we clear the bootstrap hook it queues so each test starts
    -- clean. A fake mod_manager.lua is staged at the loader root returning
    -- __FAKE_MOD_MANAGER, so the bootstrap tests drive the real loader's
    -- return-value path by setting that global. The default is a benign
    -- ModManager; tests override it (or nil it out) per case.
    local function setup()
        local sb = mock.new_sandbox()
        sb.MOD_LOADER_DIR = mock.MOD_LOADER_ROOT
        sb.MAGOS_MOD_PATH = mock.MOD_ROOT

        local files = mock.stage_mod_loader()
        -- Stage a fake loader at the loader root. The real loader opens + runs
        -- this when the bootstrap fires; it yields __FAKE_MOD_MANAGER (set
        -- per-test) so the real return-value path is exercised, not mocked over.
        files[mock.MOD_LOADER_ROOT .. "/mod_manager.lua"] = "return __FAKE_MOD_MANAGER"

        sb.io = mock.make_io(files)
        sb.require = function() return {} end
        sb.print = function() end  -- silence the entry's v2-loaded chatter
        -- Default fake: a benign ModManager whose :new() returns empty callbacks.
        sb.__FAKE_MOD_MANAGER = {
            new = function(self)
                return { update = function() end, on_game_state_changed = function() end }
            end,
        }

        mock.load_module("init", sb)()
        -- The entry queues the bootstrap lifecycle hook; clear so each test
        -- starts from an empty deferred-hook queue.
        sb.Mods._deferred_hooks = {}
        return sb
    end

    runner.register("lifecycle: queue_deferred_hook + flush_deferred_hooks exposed", function()
        local sb = setup()
        runner.assert_type("function", sb.Mods.queue_deferred_hook)
        runner.assert_type("function", sb.Mods.flush_deferred_hooks)
        runner.assert_type("function", sb.Mods.install_lifecycle_hooks)
        runner.assert_type("table", sb.Mods._deferred_hooks)
    end)

    runner.register("lifecycle: queued hook with missing target stays queued across flushes", function()
        local sb = setup()
        sb.Mods.queue_deferred_hook("Nope.fn", function(prev, ...) return "h" end, "M")
        sb.Mods.flush_deferred_hooks()
        runner.assert_eq(1, #sb.Mods._deferred_hooks, "missing target -> stays queued")
        sb.Mods.flush_deferred_hooks()
        runner.assert_eq(1, #sb.Mods._deferred_hooks)
    end)

    runner.register("lifecycle: hook installs + dequeues once the target resolves", function()
        local sb = setup()
        sb.Mods.queue_deferred_hook("Target.go", function(prev, ...)
            return "hooked:" .. prev(...)
        end, "TestMod")
        sb.Mods.flush_deferred_hooks()
        runner.assert_eq(1, #sb.Mods._deferred_hooks)

        sb.Target = { go = function(...) return "orig" end }
        sb.Mods.flush_deferred_hooks()
        runner.assert_eq(0, #sb.Mods._deferred_hooks, "target resolved -> dequeued")
        runner.assert_eq("hooked:orig", sb.Target.go(),
            "calling the target must invoke the installed chain")
    end)

    runner.register("lifecycle: dotted path with a missing middle node stays queued (no error)", function()
        -- CLASS is set up but BootStateRequireGameScripts is not yet defined.
        -- The __index-on-CLASS returns the string key, then indexing the string
        -- for ._state_update returns nil (string metatable), so the flush sees
        -- a non-function and leaves the hook queued.
        local sb = setup()
        sb.CLASS = setmetatable({}, { __index = function(_, k) return k end })
        sb.Mods.queue_deferred_hook("CLASS.BootStateRequireGameScripts._state_update",
            function() end, "mod_loader")
        sb.Mods.flush_deferred_hooks()
        runner.assert_eq(1, #sb.Mods._deferred_hooks,
            "half-materialized path must stay queued, not error")
    end)

    runner.register("lifecycle: install_lifecycle_hooks queues the bootstrap hook on the right target", function()
        local sb = setup()
        sb.Mods.install_lifecycle_hooks()
        runner.assert_eq(1, #sb.Mods._deferred_hooks)
        runner.assert_eq(
            "CLASS.BootStateRequireGameScripts._state_update",
            sb.Mods._deferred_hooks[1].func_name
        )
        runner.assert_type("function", sb.Mods._deferred_hooks[1].hook_func)
        runner.assert_eq("mod_loader", sb.Mods._deferred_hooks[1].mod_name)
    end)

    runner.register("lifecycle: flush keeps the bootstrap hook queued until the target exists", function()
        local sb = setup()
        sb.Mods.install_lifecycle_hooks()
        sb.Mods.flush_deferred_hooks()
        runner.assert_eq(1, #sb.Mods._deferred_hooks, "must stay queued pre-target")
    end)

    -- Full bootstrap simulation: stand up the boot target + the loader driver +
    -- the inner hook targets, then verify the bootstrap hook installs, runs,
    -- assigns Managers.mod, and installs the StateGame.update / GameStateMachine
    -- _change_state inner hooks. The loader loads via the REAL loader from the
    -- staged fake mod_manager.lua (returns sb.__FAKE_MOD_MANAGER), exercising
    -- the real return-value path.
    runner.register("lifecycle: bootstrap installs + runs original first + assigns Managers.mod", function()
        local sb = setup()

        local dmf_calls = 0
        sb.__FAKE_MOD_MANAGER = {
            new = function(self)
                dmf_calls = dmf_calls + 1
                return { update = function() end, on_game_state_changed = function() end }
            end,
        }

        local state_update_ran = 0
        sb.CLASS = {
            BootStateRequireGameScripts = {
                _state_update = function(self, ...)
                    state_update_ran = state_update_ran + 1
                    return "state-result"
                end,
            },
            StateGame = { update = function(self, dt, ...) return "update-result" end },
            GameStateMachine = { _change_state = function(self, ...) return "change-result" end },
        }

        sb.Mods.install_lifecycle_hooks()
        sb.Mods.flush_deferred_hooks()
        runner.assert_eq(0, #sb.Mods._deferred_hooks, "bootstrap must dequeue once target resolves")

        -- Invoke the boot target: the chain runs original first, then the
        -- bootstrap body (real mod_manager load via the real loader, Managers.mod
        -- assignment, inner hook install).
        local r = sb.CLASS.BootStateRequireGameScripts._state_update(sb.CLASS.BootStateRequireGameScripts)
        runner.assert_eq("state-result", r, "bootstrap must return the original's result")
        runner.assert_eq(1, state_update_ran, "original _state_update must run exactly once")
        runner.assert_eq(1, dmf_calls,
            "FakeModManager:new() must be called once — proves the real loader returned the class, not a boolean")
        runner.assert_type("table", sb.Managers, "Managers must be created")
        runner.assert_not_nil(sb.Managers.mod, "Managers.mod must be assigned")
    end)

    runner.register("lifecycle: StateGame.update per-frame hook fires Managers.mod:update", function()
        local sb = setup()
        local update_calls = {}
        sb.__FAKE_MOD_MANAGER = {
            new = function(self)
                return {
                    update = function(inner, dt) table.insert(update_calls, dt) end,
                    on_game_state_changed = function() end,
                }
            end,
        }

        sb.CLASS = {
            BootStateRequireGameScripts = { _state_update = function() return "s" end },
            StateGame = { update = function(self, dt, ...) return "u" end },
            GameStateMachine = { _change_state = function(self, ...) return "c" end },
        }

        sb.Mods.install_lifecycle_hooks()
        sb.Mods.flush_deferred_hooks()
        sb.CLASS.BootStateRequireGameScripts._state_update(sb.CLASS.BootStateRequireGameScripts)

        runner.assert_eq({}, update_calls, "no per-frame tick yet")
        sb.CLASS.StateGame.update(sb.CLASS.StateGame, 0.016)
        runner.assert_eq({ 0.016 }, update_calls,
            "Managers.mod:update(dt) must run inside the StateGame.update hook")
    end)

    runner.register("lifecycle: GameStateMachine._change_state hook fires on_game_state_changed", function()
        local sb = setup()
        local notifications = {}
        sb.__FAKE_MOD_MANAGER = {
            new = function(self)
                return {
                    update = function() end,
                    on_game_state_changed = function(inner, status, name, state)
                        table.insert(notifications, { status = status, name = name })
                    end,
                }
            end,
        }

        -- The engine's _change_state flips self._state from old to new across
        -- the call (we model that: func flips it to NewState).
        local sm = {}
        sb.CLASS = {
            BootStateRequireGameScripts = { _state_update = function() return "s" end },
            StateGame = { update = function() return "u" end },
            GameStateMachine = {
                _change_state = function(self, ...)
                    self._state = { _name = "NewState" }
                    return "c"
                end,
            },
        }
        -- current_state_name is the engine API the hook calls on the SM.
        function sm:current_state_name() return self._state and self._state._name end

        sb.Mods.install_lifecycle_hooks()
        sb.Mods.flush_deferred_hooks()
        -- Bootstrap body runs (installs the inner hooks).
        sb.CLASS.BootStateRequireGameScripts._state_update(sb.CLASS.BootStateRequireGameScripts)

        -- Exercise _change_state with a pre-existing old state.
        sm._state = { _name = "OldState" }
        local result = sb.CLASS.GameStateMachine._change_state(sm)
        runner.assert_eq("c", result)
        runner.assert_eq(2, #notifications,
            "state-change hook must dispatch an exit + an enter notification")
        runner.assert_eq("exit", notifications[1].status)
        runner.assert_eq("OldState", notifications[1].name)
        runner.assert_eq("enter", notifications[2].status)
        runner.assert_eq("NewState", notifications[2].name)
    end)

    -- Degradation contract: a bootstrap failure (mod_manager load yielding nil,
    -- ModManager:new() raising, etc.) must NOT crash the game. The hook returns
    -- the original state_update result, Managers.mod stays nil (vanilla), and
    -- __print logs the failure. The original _state_update still runs (unguarded).
    --
    -- `fake_mod_manager` is set onto sb.__FAKE_MOD_MANAGER so the staged fake
    -- mod_manager.lua returns it via the REAL loader (nil -> the loader returns
    -- nil -> ModManager:new() on nil raises, caught by the bootstrap pcall).
    local function bootstrap_failure_setup(sb, fake_mod_manager)
        local logged = {}
        sb.__print = function(msg) table.insert(logged, msg) end
        sb.__FAKE_MOD_MANAGER = fake_mod_manager
        sb.CLASS = {
            BootStateRequireGameScripts = {
                _state_update = function(self, ...) return "state-result" end,
            },
            StateGame = { update = function() return "u" end },
            GameStateMachine = { _change_state = function() return "c" end },
        }
        sb.Mods.install_lifecycle_hooks()
        sb.Mods.flush_deferred_hooks()
        return logged
    end

    runner.register("lifecycle: bootstrap degrades cleanly when mod_manager returns nil", function()
        local sb = setup()
        -- __FAKE_MOD_MANAGER = nil -> staged mod_manager.lua returns nil -> real
        -- loader returns nil -> `local ModManager = nil` -> ModManager:new() raises
        -- -> bootstrap pcall catches it -> degrades to vanilla.
        local logged = bootstrap_failure_setup(sb, nil)

        local r = sb.CLASS.BootStateRequireGameScripts._state_update(sb.CLASS.BootStateRequireGameScripts)
        runner.assert_eq("state-result", r, "must still return the original state_update result, not raise")
        runner.assert_nil(sb.Managers.mod, "Managers.mod must stay nil when mod_manager yields nil")
        runner.assert_truthy(logged[1] and logged[1]:find("lifecycle bootstrap failed") ~= nil,
            "the failure must be logged via __print")
    end)

    runner.register("lifecycle: bootstrap degrades cleanly when ModManager:new() raises", function()
        local sb = setup()
        -- Real loader returns the class; :new() raises (simulates DMF init blowup).
        local BoomModManager = { new = function(self) error("dmf init exploded") end }
        local logged = bootstrap_failure_setup(sb, BoomModManager)

        local r = sb.CLASS.BootStateRequireGameScripts._state_update(sb.CLASS.BootStateRequireGameScripts)
        runner.assert_eq("state-result", r, "must return the original result, not raise")
        runner.assert_nil(sb.Managers.mod, "Managers.mod must stay nil when :new() raises")
        runner.assert_truthy(logged[1] and logged[1]:find("lifecycle bootstrap failed") ~= nil,
            "the failure must be logged via __print")
    end)
end
