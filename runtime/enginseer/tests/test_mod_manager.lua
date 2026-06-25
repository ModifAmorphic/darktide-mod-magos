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
end
