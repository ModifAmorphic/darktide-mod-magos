-- test_entry.lua — end-to-end smoke test of the Enginseer v2 entry.
--
-- Stages all 5 helper modules + the entry in an in-memory io map (mirroring the
-- deployment contract: <staging>/{file,hook,class_patch,require_wrap,lifecycle,
-- enginseer}.lua), runs the entry in a sandbox, and verifies it bootstraps
-- every surface, wraps require, and queues the bootstrap lifecycle hook. The
-- per-module contracts are covered by the other test files; this test covers
-- the entry's assembly + ordering + the deferred-bridge end-to-end behavior
-- (require-wrap fires the class patch + flushes the bootstrap once targets
-- materialize).

local mock = require("mock")

return function(runner)
    -- Build a sandbox with the staging layout populated from real module
    -- sources. `engine_require` is the fake engine require used to simulate
    -- main.lua loading modules (class.lua, etc.).
    local function build(engine_require)
        local sb = mock.new_sandbox()
        sb.MAGOS_STAGING = "/staging"

        local files = {}
        for _, name in ipairs({ "file", "hook", "class_patch", "require_wrap", "lifecycle" }) do
            files["/staging/" .. name .. ".lua"] = mock.read_module(name)
        end
        files["/staging/enginseer.lua"] = mock.read_module("enginseer")

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
        local fn = mock.load_module("enginseer", sb)
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
    end)

    runner.register("entry: wraps global require + marks _require_wrapped", function()
        local sb = build()
        local original = sb.require
        mock.load_module("enginseer", sb)()
        runner.assert_truthy(sb.require ~= original, "global require must be wrapped")
        runner.assert_truthy(sb.Mods._require_wrapped)
        runner.assert_eq(original, sb.Mods.original_require)
    end)

    runner.register("entry: queues the bootstrap lifecycle hook", function()
        local sb = build()
        mock.load_module("enginseer", sb)()
        runner.assert_eq(1, #sb.Mods._deferred_hooks)
        runner.assert_eq(
            "CLASS.BootStateRequireGameScripts._state_update",
            sb.Mods._deferred_hooks[1].func_name
        )
    end)

    runner.register("entry: __print logs the v2 loaded message; MAGOS_STAGING captured", function()
        local sb = build()
        local logged = {}
        sb.__print = function(msg) table.insert(logged, msg) end
        -- __print is captured by the entry AFTER this override is in place; but
        -- the entry assigns __print = print. We need to capture the engine's
        -- print instead. Override sb.print (the entry's __print = print grabs it).
        sb.print = function(msg) table.insert(logged, msg) end
        mock.load_module("enginseer", sb)()
        runner.assert_truthy(#logged >= 1, "entry should log at least the v2-loaded line")
        runner.assert_eq("/staging", sb.Mods._staging_base)
    end)

    runner.register("entry: bootstrap aborts cleanly when a module file is missing", function()
        local sb = mock.new_sandbox()
        sb.MAGOS_STAGING = "/staging"
        -- Only provide the entry; no helper modules staged.
        local files = { ["/staging/enginseer.lua"] = mock.read_module("enginseer") }
        sb.io = mock.make_io(files)
        sb.require = function() return {} end
        local fn = mock.load_module("enginseer", sb)
        local ok, result = pcall(fn)
        runner.assert_truthy(ok)
        runner.assert_eq(false, result, "missing module should abort and return false")
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
        mock.load_module("enginseer", sb)()

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
end
