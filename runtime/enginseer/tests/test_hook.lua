-- test_hook.lua — Mods.hook chain behavior.
--
-- Coverage (acceptance criteria from the spec):
--   - Mods.hook.set chains two hooks on a mock global function; calling the
--     target invokes the chain in install order, each hook receiving the
--     previous link as its first arg.
--   - enable(false) disables one (the chain skips its .func, calls through).
--   - The loadstring path resolution works for "SomeGlobal.method".

local mock = require("mock")

return function(runner)
    local function setup()
        local sb = mock.new_sandbox()
        sb.Mods = {
            lua = { loadstring = sb.loadstring },
            file = {},
            require_store = {},
        }
        mock.run_module("hook", sb)
        return sb
    end

    runner.register("hook: MODS_HOOKS + MODS_HOOKS_BY_FILE globals are installed", function()
        local sb = setup()
        runner.assert_type("table", sb.MODS_HOOKS)
        runner.assert_type("table", sb.MODS_HOOKS_BY_FILE)
        runner.assert_type("table", sb.Mods.hook)
    end)

    runner.register("hook: _get_func resolves a dotted global path via loadstring", function()
        local sb = setup()
        sb.SomeGlobal = { method = function() return "orig" end }
        local f = sb.Mods.hook._get_func("SomeGlobal.method")
        runner.assert_type("function", f)
        runner.assert_eq("orig", f())
    end)

    runner.register("hook: set wraps a top-level global; original is called when prev invoked", function()
        local sb = setup()
        sb.target_fn = function(...) return "orig" end
        sb.Mods.hook.set("ModA", "target_fn", function(prev, ...)
            return "A:" .. prev(...)
        end)
        runner.assert_eq("A:orig", sb.target_fn())
    end)

    runner.register("hook: two hooks chain in install order; each receives the previous link", function()
        local sb = setup()
        sb.fn = function(...) return "orig" end
        sb.Mods.hook.set("ModA", "fn", function(prev, ...)
            return "A[" .. prev(...) .. "]"
        end)
        sb.Mods.hook.set("ModB", "fn", function(prev, ...)
            return "B[" .. prev(...) .. "]"
        end)
        -- A was set first; A runs first, then B (install order).
        runner.assert_eq("A[B[orig]]", sb.fn())
    end)

    runner.register("hook: enable(false) disables one hook; chain skips its func", function()
        local sb = setup()
        sb.fn = function(...) return "orig" end
        sb.Mods.hook.set("ModA", "fn", function(prev, ...) return "A[" .. prev(...) .. "]" end)
        sb.Mods.hook.set("ModB", "fn", function(prev, ...) return "B[" .. prev(...) .. "]" end)
        -- Disable A: chain passes through to B without invoking A's func.
        sb.Mods.hook.enable(false, "ModA", "fn")
        runner.assert_eq("B[orig]", sb.fn())
    end)

    runner.register("hook: enable(true) re-enables a disabled hook", function()
        local sb = setup()
        sb.fn = function(...) return "orig" end
        sb.Mods.hook.set("ModA", "fn", function(prev, ...) return "A[" .. prev(...) .. "]" end)
        sb.Mods.hook.enable(false, "ModA", "fn")
        runner.assert_eq("orig", sb.fn())
        sb.Mods.hook.enable(true, "ModA", "fn")
        runner.assert_eq("A[orig]", sb.fn())
    end)

    runner.register("hook: dotted-path target (SomeGlobal.method) gets hooked", function()
        local sb = setup()
        sb.SomeGlobal = { method = function(self, x) return "orig:" .. x end }
        sb.Mods.hook.set("ModA", "SomeGlobal.method", function(prev, self, x)
            return "hooked:" .. prev(self, x)
        end)
        runner.assert_eq("hooked:orig:hi", sb.SomeGlobal.method(sb.SomeGlobal, "hi"))
    end)

    runner.register("hook: remove(func_name, mod_name) drops one mod from the chain", function()
        local sb = setup()
        sb.fn = function(...) return "orig" end
        sb.Mods.hook.set("ModA", "fn", function(prev, ...) return "A[" .. prev(...) .. "]" end)
        sb.Mods.hook.set("ModB", "fn", function(prev, ...) return "B[" .. prev(...) .. "]" end)
        sb.Mods.hook.remove("fn", "ModB")
        runner.assert_eq("A[orig]", sb.fn())
    end)

    runner.register("hook: remove(func_name) restores the original and clears the item", function()
        local sb = setup()
        local orig = function() return "orig" end
        sb.fn = orig
        sb.Mods.hook.set("ModA", "fn", function(prev, ...) return "A[" .. prev(...) .. "]" end)
        sb.Mods.hook.remove("fn")
        runner.assert_eq("orig", sb.fn())
        runner.assert_eq(0, #sb.MODS_HOOKS, "MODS_HOOKS should be empty after full remove")
    end)

    runner.register("hook: set on the same func_name twice (same mod) replaces, not stacks", function()
        local sb = setup()
        sb.fn = function(...) return "orig" end
        sb.Mods.hook.set("ModA", "fn", function(prev, ...) return "first[" .. prev(...) .. "]" end)
        sb.Mods.hook.set("ModA", "fn", function(prev, ...) return "second[" .. prev(...) .. "]" end)
        runner.assert_eq("second[orig]", sb.fn())
        -- Only one mod entry in the chain.
        local item = sb.MODS_HOOKS[1]
        runner.assert_eq(1, #item.hooks)
    end)
end
