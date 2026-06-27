-- test_mod_manager.lua — the loader driver (enginseer/mod_loader/mod_manager.lua).
--
-- ModManager is the mod loader's driver class. These tests
-- validate the driver's CONTRACT offline by mocking class, Mods.file.* (the
-- order reader + .mod loader), and the mod objects themselves. They do NOT load
-- real DMF or the real engine — that the load succeeds end-to-end against the
-- engine is live-pending (DMF's dmf_mod_object:init() loads all its modules).
--
-- SCAN/LOAD split (Fix 2): init() scans ONLY (read order -> build _mods, no mod
-- loaded); the LOAD (per-mod run/init) fires on the first update() tick. So
-- most tests drive `:new()` (scan) then `:update(dt)` (load + drive).
--
-- Coverage (acceptance criteria from the spec):
--   - reads the order, prepends "dmf", and loads mods in order (dmf first);
--   - per mod: run() -> object stored -> object:init() called BEFORE the next
--     mod loads (verified with a sequence tracker);
--   - _state is nil until the load completes, then "done" (DMF's contract field,
--     written once); _mods_loaded is the loader's own flag;
--   - init() scans but loads NOTHING; the first update() loads + reaches done;
--   - a mod whose run() raises is skipped (logged) and the next still loads;
--   - a mod whose init() raises is skipped without killing the load;
--   - update(dt) calls each loaded mod's update(dt); mods without update ok;
--   - on_game_state_changed fans out to each mod's callback;
--   - dmf-as-first-mod + user-mod ordering: dmf returns a singleton object, the
--     user mod's run() calls the fake new_mod (proving DMF is ready first);
--   - DMF IO re-rooting: a one-shot wrapper on Mods.file.dofile re-roots
--     DMFMod:io_* mid-DMF-init (after core/io.lua, before Phase-2) so Phase-2
--     module loads + user-mod resource loads resolve from the mod root.

local mock = require("mock")

return function(runner)
    -- Build a fake `class` matching the patched engine class() contract enough
    -- for ModManager: class("Name") returns a class table whose :new(...) builds
    -- an instance and dispatches :init(...) (passing the instance as self). The
    -- module under test assigns methods via `function ModManager:init()` which
    -- lands on the class table (the `meta` here), so init dispatch works through
    -- the metatable. Returns a CALLABLE TABLE so it can both be invoked as
    -- `class("X")` and carry a `_registry` field (Lua functions can't hold
    -- fields); tests assert `class._registry.ModManager`.
    local function fake_class()
        local registry = { _order = {} }
        -- The callable class table. Stores `_instance` (the most recent
        -- ModManager) BEFORE init dispatches, so a test can observe mid-load
        -- state via `sb.class._instance` — needed because `Managers.mod` is set
        -- inside init() (so sb.Managers.mod works too), but the instance handle
        -- is handy for pre-update assertions.
        local class_tbl
        local function declare(class_name, ...)
            local meta = { name = class_name }
            meta.__index = meta
            meta.new = function(self, ...)
                local instance = setmetatable({}, meta)
                class_tbl._instance = instance
                if meta.init then meta.init(instance, ...) end
                return instance
            end
            registry[class_name] = meta
            table.insert(registry._order, class_name)
            return meta
        end
        class_tbl = setmetatable({ _registry = registry }, { __call = function(_, ...) return declare(...) end })
        return class_tbl
    end

    -- Build a sandbox with the fakes mod_manager.lua needs: `class`, `Mods.file`
    -- (read_content_to_table + exec_with_return), and `__print`. Returns the sb
    -- plus the class registry so tests can assert ModManager was declared.
    local function setup(opts)
        opts = opts or {}
        local sb = mock.new_sandbox()
        sb.class = fake_class()
        sb.__print = sb.__print or function() end

        sb.Mods = { file = {} }

        -- read_content_to_table returns the staged order (or nil to test the
        -- missing-file path). Default: a 2-user-mod order.
        sb.Mods.file.read_content_to_table = function(path, ext)
            runner.assert_eq("mod_load_order", path, "order read must target 'mod_load_order'")
            runner.assert_eq("txt", ext, "order read must use 'txt' extension")
            return opts.order ~= nil and opts.order or { "alpha", "beta" }
        end

        -- exec_with_return(name, name, "mod") returns the .mod table from a map,
        -- or nil for names in `missing` (simulates a missing/corrupt .mod file).
        -- Called during LOAD (the first update tick), NOT during init's scan.
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            runner.assert_eq(file_name, local_path, "mod load uses (name, name, 'mod')")
            runner.assert_eq("mod", ext, "mod load uses 'mod' extension")
            local mods = opts.mods or {}
            if opts.missing and opts.missing[local_path] then return nil end
            return mods[local_path]
        end

        return sb
    end

    -- Load the driver in a sandbox. Returns the ModManager class table (the
    -- module's return value) so a test can instantiate it.
    local function load_driver(sb)
        return mock.run_module("mod_manager", sb)
    end

    -- Convenience: scan (init) + load (first update) in one step. Returns the
    -- loaded ModManager instance.
    local function new_loaded(sb)
        local ModManager = load_driver(sb)
        local mm = ModManager:new()
        mm:update(0.016)
        return mm
    end

    -- A mod object that records its lifecycle events into a shared sequence
    -- tracker, with optional failure injection on a given phase. update(dt) is
    -- driven per-frame by the loader but NOT recorded into seq — sequence tests
    -- care about run/init interleaving during LOAD, and update driving happens
    -- after load (driven by the same update() call), so recording it would mix
    -- the two concerns. Tests that check update(dt) build their own objects.
    local function recording_mod(name, seq, fail_phase)
        return {
            _name = name,
            init = function(self)
                table.insert(seq, name .. ":init")
                if fail_phase == "init" then error(name .. " init boom") end
            end,
            update = function(self, dt) end,
            on_game_state_changed = function(self, status, state_name, state_object)
                table.insert(seq, name .. ":gsc:" .. status .. ":" .. tostring(state_name))
            end,
        }
    end

    -- .mod table builder: run() yields the object (optionally recording the run
    -- event into seq, optionally failing).
    local function mod_file(name, object, seq, fail_run)
        return {
            run = function()
                if seq then table.insert(seq, name .. ":run") end
                if fail_run then error(name .. " run boom") end
                return object
            end,
        }
    end

    runner.register("mod_manager: declares ModManager via class()", function()
        local sb = setup()
        local ModManager = load_driver(sb)
        runner.assert_type("table", ModManager)
        runner.assert_eq("ModManager", sb.class._registry.ModManager.name,
            "mod_manager.lua must call class('ModManager')")
    end)

    -- SCAN phase: init() builds the full _mods table up front (id/name/handle
    -- per entry), but loads NO mod (objects nil, _state unset, _mods_loaded
    -- false). The exec_with_return loader is NOT called by init.
    runner.register("mod_manager: init() scans only — builds _mods, loads no mod", function()
        local sb = setup({ order = { "usermod" } })
        local load_calls = {}
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            table.insert(load_calls, local_path)
            return nil  -- shouldn't be reached during init
        end
        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        runner.assert_eq({}, load_calls, "init must NOT call exec_with_return (scan only, no load)")
        runner.assert_eq(false, mm._mods_loaded, "_mods_loaded false after init")
        runner.assert_nil(mm._state, "_state must NOT be set by init")
        runner.assert_eq(2, #mm._mods, "dmf + usermod scanned")
        -- DMF-compat entry shape: id/name/handle match each entry's position +
        -- name (mirrors DML's _build_mod_table). DMF reads id/name/handle.
        runner.assert_eq(1, mm._mods[1].id)
        runner.assert_eq("dmf", mm._mods[1].name)
        runner.assert_eq("dmf", mm._mods[1].handle)
        runner.assert_eq("not_loaded", mm._mods[1].state, "no mod loaded yet")
        runner.assert_nil(mm._mods[1].object)
        runner.assert_eq(2, mm._mods[2].id)
        runner.assert_eq("usermod", mm._mods[2].name)
        runner.assert_eq("usermod", mm._mods[2].handle)
        runner.assert_eq("not_loaded", mm._mods[2].state)
        runner.assert_nil(mm._mods[2].object)
        runner.assert_eq(false, mm._settings.developer_mode, "_settings defaults (developer_mode)")
        runner.assert_eq(1, mm._settings.log_level, "_settings defaults (log_level)")
    end)

    runner.register("mod_manager: _state nil after init, 'done' after first update (never set mid-load)", function()
        -- _state is DMF's contract field, written ONCE ("done") when the load
        -- completes. nil before/while loading is fine — DMF only reads "done".
        -- A DMF poll mid-load (e.g. during a mod's init) must NOT see "done".
        local state_done_during_load = false
        local sb = setup({ order = {} })  -- dmf-only
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return mod_file("dmf", {
                init = function()
                    state_done_during_load = (sb.Managers.mod._state == "done")
                end,
            })
        end
        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        runner.assert_nil(mm._state, "_state must NOT be set by init (scan only)")
        runner.assert_eq(false, mm._mods_loaded, "_mods_loaded false after init")

        mm:update(0.016)

        runner.assert_eq(false, state_done_during_load,
            "_state must NOT be 'done' while the load loop is still running")
        runner.assert_eq("done", mm._state, "_state reaches 'done' once the load completes")
        runner.assert_eq(true, mm._mods_loaded, "_mods_loaded true after the load")
    end)

    runner.register("mod_manager: reads order, prepends 'dmf', loads dmf first", function()
        local sb = setup({ order = { "usermod" } })
        local loaded = {}
        local dmf_object = { init = function() end }
        local user_object = { init = function() end }
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            table.insert(loaded, local_path)
            return ({
                dmf = mod_file("dmf", dmf_object),
                usermod = mod_file("usermod", user_object),
            })[local_path]
        end
        local mm = new_loaded(sb)

        runner.assert_eq({ "dmf", "usermod" }, loaded,
            "dmf must be prepended and loaded first, then user mods in order")
        runner.assert_eq(2, #mm._mods)
        runner.assert_eq(dmf_object, mm._mods[1].object)
        runner.assert_eq("running", mm._mods[1].state)
        runner.assert_eq(user_object, mm._mods[2].object)
        runner.assert_eq("running", mm._mods[2].state)
        runner.assert_eq("done", mm._state)
    end)

    runner.register("mod_manager: run() then init() per mod, before the next mod loads", function()
        -- Sequence tracker proves interleaving: each mod's run+init completes
        -- before the next mod's run begins.
        local seq = {}
        local sb = setup({ order = { "alpha", "beta" } })
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", recording_mod("dmf", seq), seq),
                alpha = mod_file("alpha", recording_mod("alpha", seq), seq),
                beta = mod_file("beta", recording_mod("beta", seq), seq),
            })[local_path]
        end
        new_loaded(sb)

        runner.assert_eq({
            "dmf:run", "dmf:init",
            "alpha:run", "alpha:init",
            "beta:run", "beta:init",
        }, seq, "each mod's run()+init() must complete before the next mod loads")
    end)

    runner.register("mod_manager: a mod whose run() raises is skipped (logged), load continues to done", function()
        local logged = {}
        local sb = setup({ order = { "boom", "good" }, missing = {} })
        sb.__print = function(msg) table.insert(logged, msg) end
        local good_init = 0
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", { init = function() end }),
                boom = mod_file("boom", nil, nil, true),  -- run() raises
                good = mod_file("good", { init = function() good_init = good_init + 1 end }),
            })[local_path]
        end
        local mm = new_loaded(sb)

        runner.assert_eq("done", mm._state, "load still reaches done despite a run() failure")
        -- Scan built every ordered mod's entry; boom's failed entry is retained
        -- (object == nil) so the indices of later mods stay stable.
        runner.assert_eq(3, #mm._mods, "all ordered mods have entries; boom's failed entry is retained")
        runner.assert_eq("boom", mm._mods[2].name)
        runner.assert_nil(mm._mods[2].object, "boom's run() failed -> no object stored")
        runner.assert_eq("not_loaded", mm._mods[2].state, "boom's entry stays 'not_loaded'")
        runner.assert_eq("good", mm._mods[3].name)
        runner.assert_truthy(mm._mods[3].object ~= nil, "the good mod after the failing one still stores its object")
        runner.assert_eq(1, good_init, "the good mod after the failing one still inits")
        runner.assert_truthy(#logged >= 1, "the run() failure must be logged")
        runner.assert_truthy(logged[1]:find("mod 'boom' run failed") ~= nil,
            "log line must name the failing mod + phase")
    end)

    -- A mod whose run() returns nil (no error) is the BENIGN DMF-managed case:
    -- the realistic DMF-mod convention is to call new_mod() for its side effect
    -- and return nothing, so the mod is driven by DMF's inner update loop, not
    -- the loader's outer update. This is NOT a failure. The loader logs an
    -- info-level "DMF-driven" message (never "skipped"/"failed"), leaves
    -- entry.object nil (so the outer update/gsc loops skip it), keeps the
    -- scan-phase _mods entry (so _state still reaches "done", index accounting
    -- is unchanged, and DMF's new_mod can read _mods[_mod_load_index].handle
    -- during run()), and later mods still load.
    runner.register("mod_manager: a mod whose run() returns nil is DMF-driven (not skipped), load continues", function()
        local logged = {}
        local sb = setup({ order = { "dmfmod", "later" } })
        sb.__print = function(msg) table.insert(logged, msg) end

        -- The handle DMF's new_mod reads off Managers.mod during run() — mirrors
        -- dmf_mod_data.lua:39 (_mods[_mod_load_index].handle). Proves the
        -- scan-phase entry is present + indexed correctly even for a nil-return
        -- mod (DMF reads it synchronously inside new_mod).
        local handle_during_run
        local function fake_new_mod(name, resources)
            local m = sb.Managers.mod
            local entry = m._mods[m._mod_load_index]
            handle_during_run = entry and entry.handle or nil
            return { name = name, init = function() end }  -- created but NOT returned by run()
        end

        local later_init = 0
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", { init = function() end }),
                dmfmod = {
                    run = function()
                        -- Realistic DMF-mod convention: call new_mod for its
                        -- side effect (registers the mod) and return NOTHING.
                        fake_new_mod("dmfmod", { mod_script = "dmfmod/script" })
                        -- intentional: no return -> run() yields nil
                    end,
                },
                later = mod_file("later", { init = function() later_init = later_init + 1 end }),
            })[local_path]
        end
        local mm = new_loaded(sb)

        runner.assert_eq("done", mm._state, "load reaches done despite the nil-return mod")
        runner.assert_eq(3, #mm._mods, "all ordered mods have entries; dmfmod's entry retained")
        runner.assert_eq("dmfmod", mm._mods[2].name)
        runner.assert_nil(mm._mods[2].object, "DMF-driven mod has no top-level object")
        runner.assert_eq("dmf_driven", mm._mods[2].state,
            "DMF-driven mod is marked 'dmf_driven' (loaded), distinct from a failed/missing 'not_loaded' entry")
        runner.assert_eq("later", mm._mods[3].name)
        runner.assert_truthy(mm._mods[3].object ~= nil, "a mod after the nil-return mod still loads")
        runner.assert_eq(1, later_init, "the later mod still inits")
        runner.assert_eq("dmfmod", handle_during_run,
            "DMF's new_mod must read _mods[_mod_load_index].handle during the nil-return mod's run()")
        -- The log is the benign DMF-driven message, NOT "skipped"/"failed".
        local dmfmod_log
        for _, line in ipairs(logged) do
            if line:find("mod 'dmfmod'") then dmfmod_log = line break end
        end
        runner.assert_not_nil(dmfmod_log, "the nil-return mod must be logged")
        runner.assert_truthy(dmfmod_log:find("DMF%-driven") ~= nil,
            "nil-return must log the DMF-driven message: " .. tostring(dmfmod_log))
        runner.assert_truthy(dmfmod_log:find("skipped") == nil and dmfmod_log:find("failed") == nil,
            "nil-return must NOT be logged as skipped/failed: " .. tostring(dmfmod_log))
    end)

    runner.register("mod_manager: a mod whose init() raises is skipped without killing the load", function()
        local logged = {}
        local sb = setup({ order = { "boom", "good" } })
        sb.__print = function(msg) table.insert(logged, msg) end
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", { init = function() end }),
                boom = mod_file("boom", recording_mod("boom", {}, "init")),
                good = mod_file("good", { init = function() end }),
            })[local_path]
        end
        local mm = new_loaded(sb)

        -- boom's run() succeeded -> it's stored (object kept); only its init()
        -- failed. It stays in _mods (object retained) but the load continues.
        runner.assert_eq("done", mm._state, "load reaches done despite an init() failure")
        runner.assert_eq(3, #mm._mods, "dmf + boom + good all stored (run succeeded)")
        runner.assert_eq("boom", mm._mods[2].name)
        runner.assert_truthy(mm._mods[2].object ~= nil,
            "boom's run() succeeded -> object retained (only init failed)")
        runner.assert_eq("good", mm._mods[3].name, "the good mod after the failing one still loaded")
        runner.assert_truthy(logged[1]:find("mod 'boom' init failed") ~= nil,
            "log line must name the failing mod + 'init' phase")
    end)

    runner.register("mod_manager: missing .mod file is logged + skipped, load continues", function()
        local logged = {}
        local sb = setup({ order = { "ghost", "real" }, missing = { ghost = true } })
        sb.__print = function(msg) table.insert(logged, msg) end
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            if local_path == "ghost" then return nil end
            return ({
                dmf = mod_file("dmf", { init = function() end }),
                real = mod_file("real", { init = function() end }),
            })[local_path]
        end
        local mm = new_loaded(sb)

        runner.assert_eq("done", mm._state)
        runner.assert_eq(3, #mm._mods, "all ordered mods have entries; ghost's entry retained (no object)")
        runner.assert_eq("ghost", mm._mods[2].name)
        runner.assert_nil(mm._mods[2].object, "ghost has no .mod -> no object stored")
        runner.assert_eq("real", mm._mods[3].name)
        runner.assert_truthy(mm._mods[3].object ~= nil, "real's object stored")
        runner.assert_truthy(logged[1]:find("mod 'ghost'") ~= nil,
            "missing .mod must be logged naming the mod")
    end)

    runner.register("mod_manager: update(dt) calls each loaded mod's update with dt", function()
        local calls = {}
        local sb = setup({ order = {} })
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", { update = function(self, dt) table.insert(calls, { "dmf", dt }) end }),
            })[local_path]
        end
        local ModManager = load_driver(sb)
        local mm = ModManager:new()
        -- First tick loads (init runs) AND drives update(dt).
        mm:update(0.016)
        runner.assert_eq({ { "dmf", 0.016 } }, calls, "update(dt) fans out to each mod with dt")
        -- Subsequent ticks only drive update.
        mm:update(0.033)
        runner.assert_eq({ { "dmf", 0.016 }, { "dmf", 0.033 } }, calls,
            "subsequent update(dt) ticks keep fanning out")
    end)

    runner.register("mod_manager: update skips mods without an update() (no error)", function()
        local logged = {}
        local sb = setup({ order = {} })
        sb.__print = function(msg) table.insert(logged, msg) end
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return mod_file("dmf", { init = function() end })  -- no update()
        end
        local ModManager = load_driver(sb)
        local mm = ModManager:new()
        -- Must not raise and must not log (no update is fine, not a failure).
        local ok, err = pcall(function() mm:update(0.016) end)
        runner.assert_truthy(ok, "update must not raise for mods without update(): " .. tostring(err))
        runner.assert_eq(0, #logged, "a missing update() must not be logged as a failure")
    end)

    runner.register("mod_manager: update() failure in one mod is logged, others still update", function()
        local logged = {}
        local sb = setup({ order = { "boom", "good" } })
        sb.__print = function(msg) table.insert(logged, msg) end
        local good_dt
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", { init = function() end, update = function() end }),
                boom = mod_file("boom", {
                    init = function() end,
                    update = function() error("update boom") end,
                }),
                good = mod_file("good", {
                    init = function() end,
                    update = function(self, dt) good_dt = dt end,
                }),
            })[local_path]
        end
        local mm = new_loaded(sb)
        runner.assert_eq(0.016, good_dt, "the good mod's update must still run after boom's fails")
        runner.assert_truthy(logged[1]:find("mod 'boom' update failed") ~= nil,
            "boom's update failure must be logged with phase 'update'")
    end)

    runner.register("mod_manager: on_game_state_changed fans out to each mod's callback", function()
        local sb = setup({ order = {} })
        local events = {}
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", {
                    init = function() end,
                    on_game_state_changed = function(self, status, name, obj)
                        table.insert(events, { "dmf", status, name, obj })
                    end,
                }),
            })[local_path]
        end
        local mm = new_loaded(sb)
        local state_obj = { _name = "StateMainMenu" }
        mm:on_game_state_changed("enter", "StateMainMenu", state_obj)
        runner.assert_eq({ { "dmf", "enter", "StateMainMenu", state_obj } }, events,
            "on_game_state_changed forwards status + state_name + state_object verbatim")
    end)

    runner.register("mod_manager: on_game_state_changed failure in one mod doesn't block others", function()
        local logged = {}
        local sb = setup({ order = { "boom", "good" } })
        sb.__print = function(msg) table.insert(logged, msg) end
        local good_received
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", { init = function() end }),
                boom = mod_file("boom", {
                    init = function() end,
                    on_game_state_changed = function() error("gsc boom") end,
                }),
                good = mod_file("good", {
                    init = function() end,
                    on_game_state_changed = function(self, status, name, obj)
                        good_received = { status, name }
                    end,
                }),
            })[local_path]
        end
        local mm = new_loaded(sb)
        mm:on_game_state_changed("exit", "StateIngame", nil)
        runner.assert_eq({ "exit", "StateIngame" }, good_received,
            "good mod's callback still fires after boom's raises")
        runner.assert_truthy(logged[1]:find("mod 'boom' on_game_state_changed failed") ~= nil)
    end)

    -- ModManager:destroy — provides the method DMF hooks (dmf_loader.lua:53) so
    -- its unload-event hook attaches. destroy() fans on_unload out to each
    -- loaded mod that has one, pcall-guarded so one failure doesn't stop the
    -- rest (mirrors update/on_game_state_changed). Mirrors DML's
    -- ModManager:destroy -> unload intent, minimal.
    runner.register("mod_manager: destroy() calls on_unload on each loaded mod that has one (in order)", function()
        local unloaded = {}
        local sb = setup({ order = { "alpha", "beta" } })
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", {
                    init = function() end,
                    on_unload = function(self) table.insert(unloaded, "dmf") end,
                }),
                alpha = mod_file("alpha", {
                    init = function() end,
                    on_unload = function(self) table.insert(unloaded, "alpha") end,
                }),
                beta = mod_file("beta", {
                    init = function() end,
                    on_unload = function(self) table.insert(unloaded, "beta") end,
                }),
            })[local_path]
        end
        local mm = new_loaded(sb)
        mm:destroy()
        runner.assert_eq({ "dmf", "alpha", "beta" }, unloaded,
            "destroy() must call on_unload on every loaded mod that has one, in load order")
    end)

    runner.register("mod_manager: destroy() skips mods without on_unload (no error, not logged)", function()
        local logged = {}
        local sb = setup({ order = { "noul" } })
        sb.__print = function(msg) table.insert(logged, msg) end
        local dmf_unloaded = false
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", {
                    init = function() end,
                    on_unload = function() dmf_unloaded = true end,
                }),
                noul = mod_file("noul", { init = function() end }),  -- no on_unload
            })[local_path]
        end
        local mm = new_loaded(sb)
        local ok, err = pcall(function() mm:destroy() end)
        runner.assert_truthy(ok, "destroy() must not raise for mods without on_unload(): " .. tostring(err))
        runner.assert_eq(0, #logged, "a missing on_unload must not be logged as a failure")
        runner.assert_truthy(dmf_unloaded, "the mod with on_unload must still unload")
    end)

    runner.register("mod_manager: destroy() failure in one mod is logged, others still unload", function()
        local logged = {}
        local sb = setup({ order = { "boom", "good" } })
        sb.__print = function(msg) table.insert(logged, msg) end
        local good_unloaded = false
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", { init = function() end, on_unload = function() end }),
                boom = mod_file("boom", {
                    init = function() end,
                    on_unload = function() error("unload boom") end,
                }),
                good = mod_file("good", {
                    init = function() end,
                    on_unload = function() good_unloaded = true end,
                }),
            })[local_path]
        end
        local mm = new_loaded(sb)
        mm:destroy()
        runner.assert_truthy(good_unloaded, "good mod's on_unload must still run after boom's fails")
        runner.assert_truthy(logged[1]:find("mod 'boom' on_unload failed") ~= nil,
            "boom's on_unload failure must be logged with phase 'on_unload'")
    end)

    -- DMF-as-first-mod + user-mod ordering: dmf's run() returns a plain
    -- singleton object (mirrors real dmf_mod_object — NOT a class); the user
    -- mod's run() calls the global new_mod() (which DMF's init() would have
    -- defined). Proves DMF is ready (init ran) before any user mod's run().
    runner.register("mod_manager: dmf singleton + user mod's run() calls new_mod (ordering proof)", function()
        local seq = {}
        -- The fake new_mod/get_mod DMF's init() installs. Returns a mod object
        -- with an init() so the loader's object:init() step is exercised.
        local created = {}

        local sb = setup({ order = { "usermod" } })
        sb.__print = function() end

        -- DMF's init() installs new_mod/get_mod on _G (its real module does).
        local dmf_object = {
            init = function()
                table.insert(seq, "dmf:init")
                sb.new_mod = function(name, resources)
                    table.insert(seq, "new_mod(" .. name .. ")")
                    local mod = {
                        name = name,
                        init = function() table.insert(seq, name .. ":init") end,
                    }
                    created[name] = mod
                    return mod
                end
                sb.get_mod = function(name) return created[name] end
            end,
        }

        -- usermod's .mod: run() calls new_mod (DMF's API) -> returns the object.
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = {
                    run = function()
                        table.insert(seq, "dmf:run")
                        return dmf_object
                    end,
                },
                usermod = {
                    run = function()
                        table.insert(seq, "usermod:run")
                        -- This MUST work because dmf:init() already ran and
                        -- defined new_mod globally.
                        runner.assert_type("function", sb.new_mod,
                            "new_mod must exist when usermod runs (DMF init ran first)")
                        return sb.new_mod("usermod", { mod_script = "usermod/script" })
                    end,
                },
            })[local_path]
        end

        local mm = new_loaded(sb)

        runner.assert_eq({
            "dmf:run", "dmf:init",        -- dmf loads + inits first
            "usermod:run",                -- usermod's run() begins
            "new_mod(usermod)",           --   and calls DMF's new_mod (DMF init ran first)
            "usermod:init",               -- then the user mod's object:init()
        }, seq, "DMF must init (defining new_mod) before any user mod's run() calls it")

        runner.assert_eq(2, #mm._mods, "dmf + usermod loaded")
        runner.assert_eq(dmf_object, mm._mods[1].object,
            "dmf's stored object is the singleton from run() (not a :new() instance)")
        runner.assert_eq(created.usermod, mm._mods[2].object,
            "usermod's stored object is the new_mod() result")
    end)

    -- DMF-compat contract pin: the EXACT read DMF performs at
    -- dmf_mod_data.lua:39 — Managers.mod._mods[Managers.mod._mod_load_index],
    -- then .handle/.id/.name — must resolve to the currently-loading mod's entry
    -- for BOTH the first mod (DMF, index 1) and a second user mod (index 2).
    -- DMF's DMFMod:init is fired synchronously: inside object:init() for DMF
    -- (dmf_mod_manager.lua's create_mod("DMF") runs at module load) and inside
    -- run() for user mods (their .mod calls new_mod -> create_mod -> DMFMod:new).
    -- This is the read that indexed nil and died before the shape fix.
    runner.register("mod_manager: DMF read — _mods[_mod_load_index].handle resolves per mod", function()
        local seen = {}  -- phase tag -> handle read via Managers.mod
        local sb = setup({ order = { "usermod" } })

        -- Record the handle Managers.mod._mods[_mod_load_index] points at, the
        -- same indexing DMF's DMFMod:init does. Returns a mod object whose
        -- init() records (mirrors DMF reading during object:init()).
        local function reading_object(tag)
            return {
                init = function()
                    local m = sb.Managers.mod
                    local entry = m._mods[m._mod_load_index]
                    seen[tag .. ":init"] = entry and entry.handle or nil
                end,
            }
        end

        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            if local_path == "dmf" then
                -- DMF: its DMFMod:init (fired during object:init()) reads the
                -- entry. run() just yields the object.
                return { run = function() return reading_object("dmf") end }
            elseif local_path == "usermod" then
                -- User mod: new_mod() -> DMFMod:init fires INSIDE run(), so
                -- record there too, then yield the object (whose own init()
                -- also records).
                return {
                    run = function()
                        local m = sb.Managers.mod
                        local entry = m._mods[m._mod_load_index]
                        seen["usermod:run"] = entry and entry.handle or nil
                        return reading_object("usermod")
                    end,
                }
            end
        end

        local mm = new_loaded(sb)

        runner.assert_eq("dmf", seen["dmf:init"],
            "DMF (index 1) must read its own handle via _mods[_mod_load_index] during init()")
        runner.assert_eq("usermod", seen["usermod:run"],
            "user mod (index 2) must read its own handle during run() (DMF's new_mod path)")
        runner.assert_eq("usermod", seen["usermod:init"],
            "user mod (index 2) must read its own handle during init()")
        -- _mod_load_index is cleared once the load finishes (no stale index).
        runner.assert_nil(sb.Managers.mod._mod_load_index,
            "_mod_load_index must be cleared after the load loop completes")
    end)

    ---------------------------------------------------------------------------
    -- DMF IO re-rooting: the one-shot Mods.file.dofile wrapper.
    --
    -- DMF's dmf_loader captures `Mods.file.dofile` into a local `io_dofile`,
    -- loads Phase-1 modules (incl. core/io.lua, which defines DMFMod:io_*
    -- rooted at "./../mods") through that local, then loads Phase-2 modules
    -- (hooks, require, keybindings, options, …) via the DMFMod:io_dofile METHOD.
    -- init() installs a one-shot wrapper on Mods.file.dofile that re-roots
    -- DMFMod:io_* to Mods.file.* (mod-root-rooted) right after core/io.lua
    -- defines them — mid-DMF-init, before Phase-2 uses the method.
    ---------------------------------------------------------------------------

    -- Build a mock `Mods.file.dofile` whose "core/io" load simulates DMF's
    -- core/io.lua: it defines DMFMod:io_dofile (the method) rooted at the WRONG
    -- base ("./../mods"), recording any call into `wrong_base_reads`. Other
    -- paths record into `mod_root_reads` (the mod-root-rooted read). Returns the
    -- reads tables + the dofile function. Tests set this as sb.Mods.file.dofile
    -- BEFORE :new(), so init()'s wrapper wraps it.
    local function make_dmf_io_mock(sb)
        local mod_root_reads = {}
        local wrong_base_reads = {}
        local dofile = function(fp)
            -- EVERY load through this dofile is mod-root-rooted (Phase-1 via the
            -- local, Phase-2 via the re-rooted delegate). Record it up front so
            -- both the core/io load and later loads are counted.
            table.insert(mod_root_reads, fp)
            if fp == "core/io" then
                -- core/io.lua defines DMFMod:io_dofile rooted at ./../mods
                -- (the DML-heritage hardcoded local).
                sb.DMFMod = {}
                function sb.DMFMod:io_dofile(path)
                    table.insert(wrong_base_reads, "./../mods/" .. path .. ".lua")
                    return "WRONG:" .. path
                end
                return "core/io"
            end
            return "STAGED:" .. fp
        end
        return dofile, mod_root_reads, wrong_base_reads
    end

    -- Fix 1 (the gap that hid this bug): the wrapper re-roots DMFMod:io_dofile
    -- DURING DMF's init, after core/io.lua loads but BEFORE the Phase-2 module
    -- load uses the method. Without the fix, the Phase-2 load resolved against
    -- "./../mods" (the wrong base / a stale game-dir DMF); with it, it resolves
    -- against the mod root.
    runner.register("mod_manager: wrapper re-roots DMFMod:io_dofile mid-init — Phase-2 loads hit the mod root", function()
        local sb = setup({ order = {} })  -- dmf-only
        local dofile, mod_root_reads, wrong_base_reads = make_dmf_io_mock(sb)
        sb.Mods.file.dofile = dofile

        local phase2_result
        local dmf_object = {
            init = function()
                -- Mirror dmf_loader exactly: capture the local io_dofile, load
                -- Phase-1 core/io through it (this traverses the wrapper), then
                -- load a Phase-2 module via the DMFMod:io_dofile METHOD.
                local io_dofile = sb.Mods.file.dofile  -- the wrapper (installed by init)
                io_dofile("core/io")                    -- Phase-1: defines DMFMod:io_*

                -- The DMF mod object is a DMFMod instance (method dispatch via
                -- __index), so self:io_dofile resolves to DMFMod.io_dofile —
                -- the re-rooted delegate, not core/io's ./../mods method.
                local instance = setmetatable({}, { __index = sb.DMFMod })
                phase2_result = instance:io_dofile("hooks")  -- Phase-2
            end,
        }
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({ dmf = { run = function() return dmf_object end } })[local_path]
        end

        runner.assert_nil(sb.DMFMod, "precondition: DMFMod not defined until core/io loads")

        local ModManager = load_driver(sb)
        local mm = ModManager:new()   -- init: scan + install the wrapper
        runner.assert_eq(false, mm._dmf_io_adapted, "precondition: wrapper hasn't fired yet (no load)")

        mm:update(0.016)              -- first tick: load -> DMF init fires the wrapper

        runner.assert_truthy(mm._dmf_io_adapted, "the wrapper fired + re-rooted DMFMod:io_*")
        -- Phase-2 ("hooks") resolved via the re-rooted delegate -> Mods.file.dofile
        -- (mod root), NOT DMF's hardcoded "./../mods" method.
        runner.assert_eq({}, wrong_base_reads,
            "Phase-2 must NOT resolve via DMF's ./../mods method — the wrapper re-rooted it mid-init")
        runner.assert_eq("STAGED:hooks", phase2_result,
            "Phase-2 module load must return the mod-root file (re-rooted delegate -> Mods.file.dofile)")
        -- "core/io" (Phase-1) and "hooks" (Phase-2) both resolved through
        -- Mods.file.dofile (mod root): Phase-1 via the local (the wrapper in
        -- this window), Phase-2 via the re-rooted delegate.
        runner.assert_eq({ "core/io", "hooks" }, mod_root_reads,
            "both Phase-1 and Phase-2 DMF module loads resolve against the mod root")
    end)

    -- After the re-root lands (during DMF's init), a USER mod's resource load
    -- (new_mod -> resolve_resource -> mod:io_dofile_unsafe) also resolves from
    -- the mod root. This exercises io_dofile_unsafe (the resource-load surface),
    -- distinct from the Phase-2 io_dofile path above.
    runner.register("mod_manager: after re-root, a user mod's resource load (io_dofile_unsafe) hits the mod root", function()
        local sb = setup({ order = { "usermod" } })
        local dofile, mod_root_reads, wrong_base_reads = make_dmf_io_mock(sb)
        sb.Mods.file.dofile = dofile

        local dmf_object = {
            init = function()
                local io_dofile = sb.Mods.file.dofile
                io_dofile("core/io")  -- Phase-1: wrapper fires -> re-root DMFMod:io_*
                -- Install new_mod (mirrors dmf_mod_manager.create_mod +
                -- resolve_resource -> safe_call_io_dofile -> io_dofile_unsafe).
                sb.new_mod = function(name, resources)
                    local mod = setmetatable({ name = name }, { __index = sb.DMFMod })
                    if type(resources.mod_script) == "string" then
                        mod._loaded_script = mod:io_dofile_unsafe(resources.mod_script)
                    end
                    mod.init = function() end
                    return mod
                end
            end,
        }

        local usermod_object
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = { run = function() return dmf_object end },
                usermod = {
                    run = function()
                        usermod_object = sb.new_mod("usermod", { mod_script = "usermod/script" })
                        return usermod_object
                    end,
                },
            })[local_path]
        end

        runner.assert_nil(sb.DMFMod, "precondition: DMFMod not defined until DMF's init")
        local mm = new_loaded(sb)

        runner.assert_truthy(mm._dmf_io_adapted, "wrapper fired during DMF's init")
        runner.assert_eq({}, wrong_base_reads,
            "user-mod resource load must NOT use DMF's ./../mods method")
        runner.assert_eq("STAGED:usermod/script", usermod_object._loaded_script,
            "the resource load must return the mod-root file's content")
    end)

    -- One-shot + unwrap: the wrapper fires exactly once (the first dofile after
    -- DMFMod.io_dofile appears) and restores Mods.file.dofile to the original —
    -- no permanent wrap. A second Phase-1 dofile (still going through the
    -- captured local) is a benign pass-through.
    runner.register("mod_manager: wrapper fires once + unwraps (no permanent wrap on Mods.file.dofile)", function()
        local sb = setup({ order = {} })
        local dofile, mod_root_reads = make_dmf_io_mock(sb)
        local original = dofile
        sb.Mods.file.dofile = original

        local dmf_object = {
            init = function()
                local io_dofile = sb.Mods.file.dofile  -- the wrapper
                io_dofile("core/io")    -- wrapper fires (re-root) + unwraps
                io_dofile("core/other") -- now a benign pass-through
            end,
        }
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({ dmf = { run = function() return dmf_object end } })[local_path]
        end

        local ModManager = load_driver(sb)
        local mm = ModManager:new()
        runner.assert_truthy(sb.Mods.file.dofile ~= original,
            "precondition: after init, dofile is the wrapper (not the original)")

        mm:update(0.016)

        runner.assert_truthy(mm._dmf_io_adapted, "wrapper fired + re-rooted")
        runner.assert_eq(original, sb.Mods.file.dofile,
            "wrapper must unwrap — Mods.file.dofile restored to the original dofile")
        -- Both Phase-1 loads hit the original (first via wrapper->original,
        -- second via the captured local through the now-pass-through wrapper).
        runner.assert_eq({ "core/io", "core/other" }, mod_root_reads,
            "both Phase-1 loads resolved through the original dofile")
    end)

    -- No-op when DMFMod never surfaces: if DMF isn't present or its core/io.lua
    -- never loads (so DMFMod.io_dofile never appears), the wrapper stays
    -- installed as a thin pass-through and never fires — flag stays false, the
    -- load completes normally. (DMF is always prepended, so in practice the
    -- wrapper always fires; this covers the degenerate case.)
    runner.register("mod_manager: wrapper is a no-op when DMFMod never surfaces", function()
        local sb = setup({ order = {} })
        sb.Mods.file.dofile = function(fp) return "STAGED:" .. fp end
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            -- dmf loads but its init never loads core/io / never defines DMFMod
            return ({ dmf = mod_file("dmf", { init = function() end }) })[local_path]
        end
        runner.assert_nil(sb.DMFMod, "precondition: no DMFMod in this sandbox")

        local mm = new_loaded(sb)

        runner.assert_nil(sb.DMFMod, "adaptation must not fabricate a DMFMod")
        runner.assert_eq(false, mm._dmf_io_adapted,
            "flag stays false when there's no DMFMod to adapt (wrapper never fired)")
        runner.assert_eq("done", mm._state, "load still completes normally")
    end)

    -- Fix 2: init() scans but loads NOTHING — a marker mod's run() is NOT called
    -- by init(); the first update() calls run()/init() and reaches _state="done".
    -- (The Managers.input-ready aspect is inherently live-only; the scan/load
    -- SPLIT is what's offline-testable.)
    runner.register("mod_manager: init() does not load; first update() loads (scan/load split)", function()
        local run_called = false
        local init_called = false
        local sb = setup({ order = { "usermod" } })
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = mod_file("dmf", { init = function() end }),
                usermod = {
                    run = function()
                        run_called = true
                        return { init = function() init_called = true end }
                    end,
                },
            })[local_path]
        end

        local ModManager = load_driver(sb)
        local mm = ModManager:new()  -- scan only

        runner.assert_eq(false, run_called, "init() must NOT call a mod's run() (scan only)")
        runner.assert_eq(false, init_called, "init() must NOT call a mod's init()")
        runner.assert_eq(false, mm._mods_loaded, "_mods_loaded false after init")
        runner.assert_nil(mm._state, "_state unset after init")
        runner.assert_eq("not_loaded", mm._mods[2].state, "usermod still 'not_loaded' after init")

        mm:update(0.016)  -- first tick: load

        runner.assert_eq(true, run_called, "the first update() must call the mod's run()")
        runner.assert_eq(true, init_called, "the first update() must call the mod's init()")
        runner.assert_eq(true, mm._mods_loaded, "_mods_loaded true after the first update")
        runner.assert_eq("done", mm._state, "_state reaches 'done' after the first update")
        runner.assert_eq("running", mm._mods[2].state, "usermod 'running' after the first update")
    end)
end
