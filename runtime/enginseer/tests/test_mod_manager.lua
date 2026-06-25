-- test_mod_manager.lua — the rite (runtime/enginseer/mod_manager.lua).
--
-- The Enginseer IS the mod loader; ModManager is the driver class. These tests
-- validate the rite's CONTRACT offline by mocking class, Mods.file.* (the order
-- reader + .mod loader), and the mod objects themselves. They do NOT load real
-- DMF or the real engine — that the rite succeeds end-to-end against the engine
-- is live-pending (the dmf_mod_object:init() path loads all DMF modules).
--
-- Coverage (acceptance criteria from the spec):
--   - reads the order, prepends "dmf", and loads mods in order (dmf first);
--   - per mod: run() -> object stored -> object:init() called BEFORE the next
--     mod loads (verified with a sequence tracker);
--   - _state goes scanning -> loading -> done;
--   - a mod whose run() raises is skipped (logged) and the next still loads;
--   - a mod whose init() raises is skipped without killing the rite;
--   - update(dt) calls each loaded mod's update(dt); mods without update ok;
--   - on_game_state_changed fans out to each mod's callback;
--   - dmf-as-first-mod + user-mod ordering: dmf returns a singleton object, the
--     user mod's run() calls the fake new_mod (proving DMF is ready first).

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
        -- ModManager) BEFORE init dispatches, so a test can observe mid-rite
        -- `_state` via `sb.class._instance._state` — needed because
        -- `Managers.mod` isn't assigned until `:new()` returns (after init).
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
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            runner.assert_eq(file_name, local_path, "mod load uses (name, name, 'mod')")
            runner.assert_eq("mod", ext, "mod load uses 'mod' extension")
            local mods = opts.mods or {}
            if opts.missing and opts.missing[local_path] then return nil end
            return mods[local_path]
        end

        return sb
    end

    -- Load + run the rite in a sandbox. Returns the ModManager class table (the
    -- module's return value) so a test can instantiate it.
    local function load_driver(sb)
        return mock.run_module("mod_manager", sb)
    end

    -- A mod object that records its lifecycle events into a shared sequence
    -- tracker, with optional failure injection on a given phase.
    local function recording_mod(name, seq, fail_phase)
        return {
            _name = name,
            init = function(self)
                table.insert(seq, name .. ":init")
                if fail_phase == "init" then error(name .. " init boom") end
            end,
            update = function(self, dt)
                table.insert(seq, name .. ":update:" .. tostring(dt))
            end,
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
        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        runner.assert_eq({ "dmf", "usermod" }, loaded,
            "dmf must be prepended and loaded first, then user mods in order")
        runner.assert_eq(2, #mm._mods)
        -- DMF-compat entry shape: id/name/handle match each entry's position +
        -- name (mirrors DML's _build_mod_table). DMF reads id/name/handle.
        runner.assert_eq(1, mm._mods[1].id)
        runner.assert_eq("dmf", mm._mods[1].name)
        runner.assert_eq("dmf", mm._mods[1].handle)
        runner.assert_eq(dmf_object, mm._mods[1].object)
        runner.assert_eq("running", mm._mods[1].state)
        runner.assert_eq(2, mm._mods[2].id)
        runner.assert_eq("usermod", mm._mods[2].name)
        runner.assert_eq("usermod", mm._mods[2].handle)
        runner.assert_eq(user_object, mm._mods[2].object)
        runner.assert_eq("running", mm._mods[2].state)
        -- DMF-compat top-level fields: _state reaches done; _settings defaults
        -- (developer_mode == false is what dmf_options.lua:253 overwrites).
        runner.assert_eq("done", mm._state)
        runner.assert_eq(false, mm._settings.developer_mode)
        runner.assert_eq(1, mm._settings.log_level)
    end)

    runner.register("mod_manager: _state transitions scanning -> loading -> done", function()
        -- Observe mid-rite `_state` via the class instance (Managers.mod isn't
        -- assigned until :new() returns, so we read sb.class._instance instead,
        -- which fake_class exposes the moment the instance is built, pre-init).
        --   - "scanning": captured inside read_content_to_table (called between
        --     _state="scanning" and _state="loading");
        --   - "loading": captured inside the first mod's init (mid-loop);
        --   - "done": the instance's final state.
        local seen = {}
        local sb = setup({ order = {} })  -- dmf-only
        sb.Mods.file.read_content_to_table = function(path, ext)
            runner.assert_eq("mod_load_order", path)
            runner.assert_eq("txt", ext)
            table.insert(seen, sb.class._instance._state)  -- expect "scanning"
            return {}
        end
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return mod_file("dmf", {
                init = function() table.insert(seen, sb.class._instance._state) end,  -- expect "loading"
            })
        end
        local ModManager = load_driver(sb)
        local mm = ModManager:new()
        table.insert(seen, mm._state)  -- expect "done"

        runner.assert_eq({ "scanning", "loading", "done" }, seen,
            "_state must pass scanning -> loading -> done, in order")
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
        local ModManager = load_driver(sb)
        ModManager:new()

        runner.assert_eq({
            "dmf:run", "dmf:init",
            "alpha:run", "alpha:init",
            "beta:run", "beta:init",
        }, seq, "each mod's run()+init() must complete before the next mod loads")
    end)

    runner.register("mod_manager: a mod whose run() raises is skipped (logged), rite continues to done", function()
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
        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        runner.assert_eq("done", mm._state, "rite still reaches done despite a run() failure")
        -- Two-phase scan: every ordered mod has an entry; boom's failed entry
        -- is retained (object == nil) so the indices of later mods stay stable.
        runner.assert_eq(3, #mm._mods, "all ordered mods have entries; boom's failed entry is retained")
        runner.assert_eq("dmf", mm._mods[1].name)
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
    -- this rite's outer update. This is NOT a failure. The rite logs an
    -- info-level "DMF-driven" message (never "skipped"/"failed"), leaves
    -- entry.object nil (so the outer update/gsc loops skip it), keeps the
    -- scan-phase _mods entry (so _state still reaches "done", index accounting
    -- is unchanged, and DMF's new_mod can read _mods[_mod_load_index].handle
    -- during run()), and later mods still load.
    runner.register("mod_manager: a mod whose run() returns nil is DMF-driven (not skipped), rite continues", function()
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

        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        -- _state reaches done; the nil-return didn't abort the rite.
        runner.assert_eq("done", mm._state, "rite reaches done despite the nil-return mod")
        -- The entry is retained (scan phase) with id/name/handle; object stays nil.
        runner.assert_eq(3, #mm._mods, "all ordered mods have entries; dmfmod's entry retained")
        runner.assert_eq("dmfmod", mm._mods[2].name)
        runner.assert_nil(mm._mods[2].object, "DMF-driven mod has no top-level object")
        runner.assert_eq("dmf_driven", mm._mods[2].state,
            "DMF-driven mod is marked 'dmf_driven' (loaded), distinct from a failed/missing 'not_loaded' entry")
        -- The later mod still loads (nil-return doesn't abort).
        runner.assert_eq("later", mm._mods[3].name)
        runner.assert_truthy(mm._mods[3].object ~= nil, "a mod after the nil-return mod still loads")
        runner.assert_eq(1, later_init, "the later mod still inits")
        -- DMF's new_mod (inside run()) read the scan-phase _mods entry's handle.
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

    runner.register("mod_manager: a mod whose init() raises is skipped without killing the rite", function()
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
        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        -- boom's run() succeeded -> it's stored (object kept); only its init()
        -- failed. It stays in _mods (object retained) but the rite continues.
        runner.assert_eq("done", mm._state, "rite reaches done despite an init() failure")
        runner.assert_eq(3, #mm._mods, "dmf + boom + good all stored (run succeeded)")
        runner.assert_eq("boom", mm._mods[2].name)
        runner.assert_truthy(mm._mods[2].object ~= nil,
            "boom's run() succeeded -> object retained (only init failed)")
        runner.assert_eq("good", mm._mods[3].name, "the good mod after the failing one still loaded")
        runner.assert_truthy(logged[1]:find("mod 'boom' init failed") ~= nil,
            "log line must name the failing mod + 'init' phase")
    end)

    runner.register("mod_manager: missing .mod file is logged + skipped, rite continues", function()
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
        local ModManager = load_driver(sb)
        local mm = ModManager:new()

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
        mm:update(0.016)
        runner.assert_eq({ { "dmf", 0.016 } }, calls, "update(dt) fans out to each mod with dt")
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
        local ModManager = load_driver(sb)
        local mm = ModManager:new()
        mm:update(0.033)
        runner.assert_eq(0.033, good_dt, "the good mod's update must still run after boom's fails")
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
        local ModManager = load_driver(sb)
        local mm = ModManager:new()
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
        local ModManager = load_driver(sb)
        local mm = ModManager:new()
        mm:on_game_state_changed("exit", "StateIngame", nil)
        runner.assert_eq({ "exit", "StateIngame" }, good_received,
            "good mod's callback still fires after boom's raises")
        runner.assert_truthy(logged[1]:find("mod 'boom' on_game_state_changed failed") ~= nil)
    end)

    -- DMF-as-first-mod + user-mod ordering: dmf's run() returns a plain
    -- singleton object (mirrors real dmf_mod_object — NOT a class); the user
    -- mod's run() calls the global new_mod() (which DMF's init() would have
    -- defined). Proves DMF is ready (init ran) before any user mod's run().
    runner.register("mod_manager: dmf singleton + user mod's run() calls new_mod (ordering proof)", function()
        local seq = {}
        -- The fake new_mod/get_mod DMF's init() installs. Returns a mod object
        -- with an init() so the rite's object:init() step is exercised.
        local created = {}

        local sb = setup({ order = { "usermod" } })
        sb.__print = function() end

        -- DMF's init() installs new_mod/get_mod on _G (its real module does).
        local dmf_object = {
            init = function()
                table.insert(seq, "dmf:init")
                -- DMF defines new_mod/get_mod globally during its init.
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

        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        runner.assert_eq({
            "dmf:run", "dmf:init",        -- dmf loads + inits first
            "usermod:run",                -- usermod's run() begins
            "new_mod(usermod)",           --   and calls DMF's new_mod (DMF init ran first)
            "usermod:init",               -- then the user mod's object:init()
        }, seq, "DMF must init (defining new_mod) before any user mod's run() calls it")

        runner.assert_eq(2, #mm._mods, "dmf + usermod loaded")
        runner.assert_eq(1, mm._mods[1].id)
        runner.assert_eq("dmf", mm._mods[1].name)
        runner.assert_eq("dmf", mm._mods[1].handle)
        runner.assert_eq(2, mm._mods[2].id)
        runner.assert_eq("usermod", mm._mods[2].name)
        runner.assert_eq("usermod", mm._mods[2].handle)
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

        local ModManager = load_driver(sb)
        ModManager:new()

        runner.assert_eq("dmf", seen["dmf:init"],
            "DMF (index 1) must read its own handle via _mods[_mod_load_index] during init()")
        runner.assert_eq("usermod", seen["usermod:run"],
            "user mod (index 2) must read its own handle during run() (DMF's new_mod path)")
        runner.assert_eq("usermod", seen["usermod:init"],
            "user mod (index 2) must read its own handle during init()")
        -- _mod_load_index is cleared once the rite finishes (no stale index).
        runner.assert_nil(sb.Managers.mod._mod_load_index,
            "_mod_load_index must be cleared after the load loop completes")
    end)

    -- DMF io_* re-rooting: DMF hardcodes "./../mods" (DML heritage) for its
    -- mod-facing IO surface; Magos stages mods under MAGOS_MOD_PATH. The
    -- Enginseer overrides DMFMod:io_* to delegate to Mods.file.* (staging-
    -- rooted) right after DMF's init(), so a user mod's resource load
    -- (new_mod -> resolve_resource -> safe_call_io_dofile -> mod:io_dofile_unsafe)
    -- resolves from staging instead of ./../mods. This was the last gap: the
    -- whole chain reached StateMainMenu but the user mod's script missed
    -- ("Error opening './../mods/<mod>/script.lua'").
    runner.register("mod_manager: DMF io_dofile_unsafe override routes mod-resource loads to Mods.file (staging)", function()
        local staging_reads = {}     -- paths Mods.file.dofile opened
        local dmf_orig_reads = {}    -- paths DMF's original ./../mods method opened

        local sb = setup({ order = { "usermod" } })
        -- The staging-rooted delegate target. Records the read + returns a
        -- sentinel proving the load hit staging (not ./../mods).
        sb.Mods.file.dofile = function(fp)
            table.insert(staging_reads, fp)
            return "STAGED:" .. fp
        end

        -- Fake DMF: init() defines DMFMod + the io_* surface (mirrors
        -- dmf/modules/core/io.lua loaded during DMF's init, rooted at ./../mods)
        -- AND installs new_mod (mirrors dmf_mod_manager.create_mod +
        -- load_mod_resource -> resolve_resource -> safe_call_io_dofile ->
        -- mod:io_dofile_unsafe).
        local dmf_object = {
            init = function()
                sb.DMFMod = {}
                function sb.DMFMod:io_dofile_unsafe(fp)
                    table.insert(dmf_orig_reads, "./../mods/" .. fp .. ".lua")
                    return "DMF_ORIG:" .. fp
                end
                sb.new_mod = function(name, resources)
                    local mod = setmetatable({ name = name }, { __index = sb.DMFMod })
                    if type(resources.mod_script) == "string" then
                        -- THE call that failed pre-fix. With the override it
                        -- routes through Mods.file.dofile (staging).
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

        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        -- The override routed the resource load through Mods.file.dofile
        -- (staging-rooted), NOT DMF's hardcoded "./../mods".
        runner.assert_eq({ "usermod/script" }, staging_reads,
            "mod-resource load must route through Mods.file.dofile (staging-rooted)")
        runner.assert_eq(0, #dmf_orig_reads,
            "DMF's original ./../mods io_dofile_unsafe must NOT be called after the override")
        runner.assert_eq("STAGED:usermod/script", usermod_object._loaded_script,
            "the resource load must return the staging file's content")
        runner.assert_truthy(mm._dmf_io_adapted,
            "the DMF IO adaptation flag must be set once DMF defined DMFMod")
    end)

    -- One-shot: the override lands once (after the first mod whose init surfaces
    -- a DMFMod) and is never re-applied within the same rite, even if a later
    -- mod also surfaces DMFMod. Verified by tracking the io_dofile_unsafe
    -- function identity across the two inits + post-rite.
    runner.register("mod_manager: DMF IO adaptation is one-shot — two DMF-like mods don't double-apply", function()
        local sb = setup({ order = { "dmf2" } })  -- dmf + dmf2 both surface DMFMod
        sb.Mods.file.dofile = function(fp) return "STAGED:" .. fp end

        -- Capture io_dofile_unsafe's identity during each mod's init. dmf's init
        -- captures the ORIGINAL (adapt runs AFTER init); dmf2's init captures
        -- whatever is current by then (the delegate). If the one-shot holds,
        -- dmf2's init sees the delegate AND post-rite the ref is unchanged.
        local seen_refs = {}
        local function make_dmf_like()
            return {
                init = function()
                    -- dmf creates DMFMod; dmf2 re-surfaces the same one.
                    sb.DMFMod = sb.DMFMod or {
                        io_dofile_unsafe = function(self, fp) return "DMF_ORIG" end,
                    }
                    table.insert(seen_refs, sb.DMFMod.io_dofile_unsafe)
                end,
            }
        end

        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({
                dmf = { run = function() return make_dmf_like() end },
                dmf2 = { run = function() return make_dmf_like() end },
            })[local_path]
        end

        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        runner.assert_eq(2, #seen_refs, "both DMF-like mods ran init")
        -- seen_refs[1]: original, captured during dmf's init (adapt runs after).
        -- seen_refs[2]: delegate, captured during dmf2's init (already adapted).
        runner.assert_truthy(seen_refs[1] ~= sb.DMFMod.io_dofile_unsafe,
            "after dmf's init, the original must have been replaced by the delegate")
        runner.assert_eq(seen_refs[2], sb.DMFMod.io_dofile_unsafe,
            "after dmf2's init, adaptation must NOT re-run (one-shot): delegate ref unchanged")
        runner.assert_truthy(mm._dmf_io_adapted, "flag set after the rite")
    end)

    -- No-op when DMFMod is absent: a rite whose mods never surface a DMFMod
    -- (every other test in this file, and any non-DMF bootstrap) must be
    -- undisturbed — _adapt_dmf_io no-ops, fabricates nothing, flag stays false.
    runner.register("mod_manager: DMF IO adaptation is a no-op when DMFMod is absent", function()
        local sb = setup({ order = {} })  -- dmf-only, but dmf doesn't surface DMFMod
        sb.Mods.file.dofile = function(fp) return "STAGED" end
        sb.Mods.file.exec_with_return = function(local_path, file_name, ext)
            return ({ dmf = mod_file("dmf", { init = function() end }) })[local_path]
        end
        runner.assert_nil(sb.DMFMod, "precondition: no DMFMod in this sandbox")

        local ModManager = load_driver(sb)
        local mm = ModManager:new()

        runner.assert_nil(sb.DMFMod, "adaptation must not fabricate a DMFMod")
        runner.assert_eq(false, mm._dmf_io_adapted,
            "flag stays false when there's no DMFMod to adapt (no-op never landed)")
        runner.assert_eq("done", mm._state, "rite still completes normally")
    end)
end
