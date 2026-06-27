-- test_require_wrap.lua — the wrapped global require.
--
-- Coverage (acceptance criteria from the spec):
--   - The wrapped require stores table results in Mods.require_store[path].
--   - The one-shot class patch is installed the first time a fake require causes
--     _G.class to appear (simulate class.lua loading).
--   - The deferred queue flush runs after each require.

local mock = require("mock")

return function(runner)
    -- Set up a sandbox with hook + class_patch + lifecycle + require_wrap loaded
    -- (require_wrap depends on all three at runtime, even though its module
    -- load only references Mods.original_require).
    local function setup(engine_require)
        local sb = mock.new_sandbox()
        sb.Mods = {
            file = {},
            lua = { loadstring = sb.loadstring },
            require_store = {},
        }
        -- The engine's real require, captured by the entry as
        -- Mods.original_require before the wrap is installed.
        sb.Mods.original_require = engine_require or function(path) return {} end
        -- require must be a global at wrap-install time (the wrap assigns to it).
        sb.require = sb.Mods.original_require

        mock.run_module("hook", sb)
        mock.run_module("class_patch", sb)
        mock.run_module("lifecycle", sb)
        mock.run_module("require_wrap", sb)
        return sb
    end

    runner.register("require_wrap: install wraps the global require exactly once", function()
        local calls = 0
        local sb = setup(function(path) calls = calls + 1; return {} end)
        local original_require_ref = sb.require
        sb.Mods.install_require_wrap()
        local wrapped = sb.require
        runner.assert_truthy(wrapped ~= original_require_ref, "require should be replaced")
        -- Second install is a no-op.
        sb.Mods.install_require_wrap()
        runner.assert_eq(wrapped, sb.require, "second install must not re-wrap")
    end)

    runner.register("require_wrap: table results are cached in Mods.require_store[path]", function()
        local t_util = { _name = "table_util" }
        local sb = setup(function(path)
            if path == "scripts/foundation/utilities/table" then return t_util end
            return {}
        end)
        sb.Mods.install_require_wrap()
        local r = sb.require("scripts/foundation/utilities/table")
        runner.assert_eq(t_util, r)
        local store = sb.Mods.require_store["scripts/foundation/utilities/table"]
        runner.assert_type("table", store)
        runner.assert_eq(1, #store)
        runner.assert_eq(t_util, store[1])
    end)

    runner.register("require_wrap: non-table results are not stored", function()
        local sb = setup(function(path)
            if path == "bool_mod" then return true end
            return nil
        end)
        sb.Mods.install_require_wrap()
        sb.require("bool_mod")
        runner.assert_nil(sb.Mods.require_store["bool_mod"])
    end)

    runner.register("require_wrap: identical consecutive results are de-duplicated", function()
        local shared = { tag = "shared" }
        local sb = setup(function(path) return shared end)
        sb.Mods.install_require_wrap()
        sb.require("same")
        sb.require("same")
        runner.assert_eq(1, #sb.Mods.require_store["same"],
            "identical consecutive results should not stack")
    end)

    runner.register("require_wrap: require forwards varargs to the engine require", function()
        local received
        local sb = setup(function(path, ...) received = { ... } return {} end)
        sb.Mods.install_require_wrap()
        sb.require("path", "a", "b", "c")
        runner.assert_eq({ "a", "b", "c" }, received)
    end)

    runner.register("require_wrap: one-shot class patch fires once _G.class appears", function()
        -- Simulate the engine loading foundation/utilities/class.lua: when that
        -- path is require'd, expose _G.class. The require-wrap's install_class_patch
        -- call should then wrap it.
        --
        -- Forward-declare sb so the engine_require closure can capture it (Lua's
        -- `local x = f(function() x = ... end)` would otherwise treat the inner
        -- x as a global — the local's scope starts AFTER the statement).
        local sb
        sb = setup(function(path)
            if path == "scripts/foundation/utilities/class" then
                sb.class = function(name) return { _classname = name } end
                return { _loaded = "class.lua" }
            end
            return {}
        end)
        sb.Mods.install_require_wrap()
        runner.assert_nil(sb.Mods._class_patch_installed,
            "class patch must not fire before class.lua is require'd")
        sb.require("scripts/foundation/utilities/class")
        runner.assert_truthy(sb.Mods._class_patch_installed,
            "class patch must fire after class.lua is require'd")
        runner.assert_type("table", sb.CLASS)
        -- The require'd class.lua result (a table) is also cached.
        runner.assert_eq(1, #sb.Mods.require_store["scripts/foundation/utilities/class"])
    end)

    runner.register("require_wrap: a class() call after the patch registers in CLASS", function()
        local sb
        sb = setup(function(path)
            if path == "scripts/foundation/utilities/class" then
                sb.class = function(name) return { _classname = name } end
                return {}
            end
            return {}
        end)
        sb.Mods.install_require_wrap()
        sb.require("scripts/foundation/utilities/class")
        -- Now simulate the engine creating a class via the (now-wrapped) class().
        sb.class("StateFoo")
        runner.assert_eq("StateFoo", sb.CLASS.StateFoo._classname)
        runner.assert_eq(sb.CLASS.StateFoo, sb.StateFoo)
    end)

    runner.register("require_wrap: deferred hooks flush after each require", function()
        -- Queue a deferred hook whose target doesn't exist yet; verify it stays
        -- queued across requires that don't materialize the target, then
        -- installs once the target is added.
        local sb = setup(function(path) return {} end)
        sb.Mods.install_require_wrap()

        sb.Mods.queue_deferred_hook("MissingTarget.fn", function(prev, ...)
            return "hooked:" .. prev(...)
        end, "TestMod")
        runner.assert_eq(1, #sb.Mods._deferred_hooks,
            "hook must stay queued while its target is missing")

        sb.require("scripts/anything")
        runner.assert_eq(1, #sb.Mods._deferred_hooks,
            "still queued — target still missing")

        -- Materialize the target and require again; the wrap's flush should
        -- install + dequeue.
        sb.MissingTarget = { fn = function(...) return "orig" end }
        sb.require("scripts/anything_else")
        runner.assert_eq(0, #sb.Mods._deferred_hooks, "hook must dequeue once target resolves")
        runner.assert_eq("hooked:orig", sb.MissingTarget.fn())
    end)
end
