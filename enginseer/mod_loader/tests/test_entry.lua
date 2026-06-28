-- test_entry.lua — end-to-end smoke test of the mod loader entry.
--
-- Stages all 5 helper modules + the entry in an in-memory io map under the
-- loader root (mirroring the deployment contract: <loader-root>/{file,hook,
-- class_patch,require_wrap,lifecycle}.lua + init.lua), with a SEPARATE mod root
-- holding DMF/mods/mods.lst. Runs the entry in a sandbox and verifies it
-- bootstraps every surface, wraps require, and queues the bootstrap lifecycle
-- hook. The per-module contracts are covered by the other test files; this test
-- covers the entry's assembly + ordering + the deferred-bridge end-to-end
-- behavior (require-wrap fires the class patch + flushes the bootstrap once
-- targets materialize).

local mock = require("mock")

return function(runner)
    -- Build a sandbox with the two-root staging layout populated from real
    -- module sources. The loader root (MOD_LOADER_DIR) holds the entry +
    -- modules; the mod root (MAGOS_MOD_PATH) is where DMF/mods/mods.lst
    -- live (Mods.file.* roots there via Mods._staging_base).
    -- `engine_require` is the fake engine require used to simulate main.lua
    -- loading modules (class.lua, etc.).
    local function build(engine_require)
        local sb = mock.new_sandbox()
        sb.MOD_LOADER_DIR = mock.MOD_LOADER_ROOT
        sb.MAGOS_MOD_PATH = mock.MOD_ROOT

        local files = mock.stage_mod_loader()

        local io = mock.make_io(files)
        sb.io = io
        sb.Mods = nil  -- entry creates Mods

        -- The engine's real require. The entry captures Mods.original_require
        -- from this BEFORE wrapping.
        sb.require = engine_require or function(path) return {} end

        -- Wire Mods.lua.io / loadstring lazily once the entry has created Mods.
        -- Easiest: run a tiny inline chunk first that hooks the env's __index
        -- so any `Mods.lua.io` access falls back to our mock. Simpler still:
        -- just provide bare io + loadstring in the sandbox (the entry uses
        -- bare io + loadstring for its own bootstrap_load; helper modules use
        -- Mods.lua.io/loadstring which the entry populates from these).
        -- The entry sets Mods.lua.io = io and Mods.lua.loadstring = loadstring,
        -- so the helper modules get the sandbox-aware versions automatically.
        return sb, files
    end

    runner.register("entry: bootstrap loads all surfaces onto Mods and returns true", function()
        local sb = build()
        local fn = mock.load_module("init", sb)
        local ok, result = pcall(fn)
        runner.assert_truthy(ok, "entry should not error: " .. tostring(result))
        runner.assert_eq(true, result)
        runner.assert_type("table", sb.Mods)
        runner.assert_type("table", sb.Mods.file)
        runner.assert_type("function", sb.Mods.file.dofile)
        runner.assert_type("table", sb.Mods.hook)
        runner.assert_type("function", sb.Mods.hook.set)
        runner.assert_type("function", sb.Mods.install_class_patch)
        runner.assert_type("function", sb.Mods.install_require_wrap)
        runner.assert_type("function", sb.Mods.install_lifecycle_hooks)
        runner.assert_type("function", sb.Mods.flush_deferred_hooks)
        runner.assert_type("function", sb.Mods.queue_deferred_hook)
        runner.assert_type("function", sb.Mods.load_module,
            "entry must expose bootstrap_load as Mods.load_module")
    end)

    runner.register("entry: wraps global require + marks _require_wrapped", function()
        local sb = build()
        local original = sb.require
        mock.load_module("init", sb)()
        runner.assert_truthy(sb.require ~= original, "global require must be wrapped")
        runner.assert_truthy(sb.Mods._require_wrapped)
        runner.assert_eq(original, sb.Mods.original_require)
    end)

    runner.register("entry: queues the bootstrap lifecycle hook", function()
        local sb = build()
        mock.load_module("init", sb)()
        runner.assert_eq(1, #sb.Mods._deferred_hooks)
        runner.assert_eq(
            "CLASS.BootStateRequireGameScripts._state_update",
            sb.Mods._deferred_hooks[1].func_name
        )
    end)

    runner.register("entry: __print logs the loaded message; mod root captured into _staging_base", function()
        local sb = build()
        local logged = {}
        sb.__print = function(msg) table.insert(logged, msg) end
        -- __print is captured by the entry AFTER this override is in place; but
        -- the entry assigns __print = print. We need to capture the engine's
        -- print instead. Override sb.print (the entry's __print = print grabs it).
        sb.print = function(msg) table.insert(logged, msg) end
        mock.load_module("init", sb)()
        runner.assert_truthy(#logged >= 1, "entry should log at least the v2-loaded line")
        -- _staging_base roots at the MOD root (Mods.file.* surface), NOT the
        -- loader root the entry's own modules load from.
        runner.assert_eq(mock.MOD_ROOT, sb.Mods._staging_base)
    end)

    runner.register("entry: bootstrap aborts cleanly when a module file is missing", function()
        local sb = mock.new_sandbox()
        sb.MOD_LOADER_DIR = mock.MOD_LOADER_ROOT
        sb.MAGOS_MOD_PATH = mock.MOD_ROOT
        -- Only provide the entry at the loader root; no helper modules staged.
        local files = { [mock.MOD_LOADER_ROOT .. "/init.lua"] = mock.read_module("init") }
        sb.io = mock.make_io(files)
        sb.require = function() return {} end
        local fn = mock.load_module("init", sb)
        local ok, result = pcall(fn)
        runner.assert_truthy(ok)
        runner.assert_eq(false, result, "missing module should abort and return false")
    end)

    -- #6: double-load must short-circuit before Mods.original_require is
    -- recaptured (which would clobber the saved original with the now-wrapped
    -- require -> infinite recursion on the next require).
    runner.register("entry: double-load is a no-op (original_require not clobbered)", function()
        local sb = build()
        mock.load_module("init", sb)()
        runner.assert_truthy(sb.Mods._v2_loaded, "first load sets _v2_loaded")
        local saved_require = sb.Mods.original_require
        runner.assert_truthy(saved_require)

        -- Run the entry a second time. global require is now wrapped; without
        -- the guard, `Mods.original_require = require` would capture the wrapper.
        local ok, result = pcall(function() return mock.load_module("init", sb)() end)
        runner.assert_truthy(ok, "second load must not error: " .. tostring(result))
        runner.assert_eq(true, result, "second load returns true (short-circuit)")
        runner.assert_eq(saved_require, sb.Mods.original_require,
            "second load must NOT clobber original_require with the wrapped require")
    end)

    -- End-to-end deferred bridge: simulate main.lua require'ing class.lua and a
    -- later materialization of the boot target. Verify the class patch fires
    -- and the bootstrap hook installs through the require-wrap's flush.
    runner.register("entry: deferred bridge fires class patch + installs bootstrap through require", function()
        -- Forward-declare sb so the engine_require closure can capture it.
        local sb
        sb = build(function(path)
            if path == "scripts/foundation/utilities/class" then
                sb.class = function(name) return { _classname = name } end
                return {}
            end
            return {}
        end)
        mock.load_module("init", sb)()

        -- Pre-conditions: class patch not yet installed; bootstrap queued.
        runner.assert_nil(sb.Mods._class_patch_installed)
        runner.assert_eq(1, #sb.Mods._deferred_hooks)

        -- Simulate main.lua require'ing class.lua. The require-wrap fires the
        -- class patch (CLASS now exists) and flushes deferred hooks (target
        -- still missing -> bootstrap stays queued).
        sb.require("scripts/foundation/utilities/class")
        runner.assert_truthy(sb.Mods._class_patch_installed)
        runner.assert_type("table", sb.CLASS)
        runner.assert_eq(1, #sb.Mods._deferred_hooks,
            "bootstrap must stay queued until its target resolves")

        -- Now simulate the boot state sequence creating the target. The next
        -- require (any path) flushes again and installs the bootstrap.
        sb.CLASS.BootStateRequireGameScripts = setmetatable(
            { _state_update = function() return "booted" end },
            { __index = sb.CLASS.__index }
        )
        sb.require("scripts/anything")
        runner.assert_eq(0, #sb.Mods._deferred_hooks,
            "bootstrap must dequeue once BootStateRequireGameScripts._state_update resolves")
        runner.assert_eq(1, #sb.MODS_HOOKS,
            "bootstrap hook must be installed into MODS_HOOKS")
    end)

    -- Mods.load_module must return the loaded chunk's RETURN VALUE
    -- (dofile-style), NOT a boolean. Regression: it was previously aliased
    -- directly to bootstrap_load (true/false), so lifecycle.lua's
    -- `local ModManager = Mods.load_module("mod_manager")` got `true`
    -- and `ModManager:new()` raised "attempt to index local 'ModManager'
    -- (a boolean value)". These exercise the REAL loader (no mock) against
    -- staged fake modules at the loader root.
    local function build_with_extra(extra_files)
        local sb = mock.new_sandbox()
        sb.MOD_LOADER_DIR = mock.MOD_LOADER_ROOT
        sb.MAGOS_MOD_PATH = mock.MOD_ROOT
        local files = mock.stage_mod_loader()
        for path, content in pairs(extra_files or {}) do
            files[path] = content
        end
        sb.io = mock.make_io(files)
        sb.require = function() return {} end
        sb.print = function() end  -- silence the entry's loaded-message chatter
        mock.load_module("init", sb)()
        return sb
    end

    runner.register("entry: load_module returns the chunk's return value (not a boolean)", function()
        local sb = build_with_extra({
            [mock.MOD_LOADER_ROOT .. "/returns_table.lua"] = "return { hello = 'world' }",
        })
        local result = sb.Mods.load_module("returns_table")
        runner.assert_type("table", result,
            "must return the chunk's table, not a boolean")
        runner.assert_eq("world", result.hello,
            "must return the chunk's actual content")
        runner.assert_truthy(result ~= true and result ~= false,
            "regression: must NOT be a boolean (was aliased to bootstrap_load)")
    end)

    runner.register("entry: load_module returns nil for a missing module (no raise)", function()
        local sb = build_with_extra()
        local ok, result = pcall(sb.Mods.load_module, "does_not_exist")
        runner.assert_truthy(ok, "missing module must not raise")
        runner.assert_nil(result, "missing module must return nil")
    end)

    runner.register("entry: load_module returns nil for a syntax-error module (no raise)", function()
        local sb = build_with_extra({
            [mock.MOD_LOADER_ROOT .. "/broken.lua"] = "this is not valid lua",
        })
        local ok, result = pcall(sb.Mods.load_module, "broken")
        runner.assert_truthy(ok,
            "syntax error must not raise — loader catches it internally and returns nil")
        runner.assert_nil(result, "broken module must return nil")
    end)

    -- bootstrap_load (the entry's internal install-only loader for the 5 helper
    -- modules) keeps its true/false contract: it reports success even when the
    -- chunk returns nil. Several staged modules (e.g. lifecycle.lua) end without
    -- a return statement, so a successful entry bootstrap here proves the loop's
    -- `if not bootstrap_load(mod)` did NOT see the chunk's nil result.
    runner.register("entry: bootstrap_load keeps true/false contract (entry bootstraps nil-returning modules)", function()
        local sb = build_with_extra()
        runner.assert_truthy(sb.Mods._v2_loaded,
            "entry must complete bootstrap (lifecycle.lua returns nil; if bootstrap_load returned the chunk's nil, the loop would abort)")
        runner.assert_type("function", sb.Mods.install_lifecycle_hooks,
            "lifecycle loaded despite returning nil -> bootstrap_load reported true")
    end)
end
