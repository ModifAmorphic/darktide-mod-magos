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

    -- #3: enable(value, nil, func_name) must toggle EVERY mod's hook on that
    -- func_name (the doc contract). Previously the inner guard required
    -- hook.name == mod_name, and since hook names are strings, nil matched
    -- nothing — a silent no-op.
    runner.register("hook: enable(false, nil, fn) disables ALL mods on that func_name", function()
        local sb = setup()
        sb.fn = function(...) return "orig" end
        sb.Mods.hook.set("ModA", "fn", function(prev, ...) return "A[" .. prev(...) .. "]" end)
        sb.Mods.hook.set("ModB", "fn", function(prev, ...) return "B[" .. prev(...) .. "]" end)
        -- Both mods hook fn; nil mod_name must disable BOTH -> calling the
        -- target runs the original, no wrappers.
        sb.Mods.hook.enable(false, nil, "fn")
        runner.assert_eq("orig", sb.fn())
    end)

    runner.register("hook: enable(true, nil, fn) re-enables all mods on that func_name", function()
        local sb = setup()
        sb.fn = function(...) return "orig" end
        sb.Mods.hook.set("ModA", "fn", function(prev, ...) return "A[" .. prev(...) .. "]" end)
        sb.Mods.hook.set("ModB", "fn", function(prev, ...) return "B[" .. prev(...) .. "]" end)
        sb.Mods.hook.enable(false, nil, "fn")
        runner.assert_eq("orig", sb.fn(), "precondition: both disabled")
        sb.Mods.hook.enable(true, nil, "fn")
        runner.assert_eq("A[B[orig]]", sb.fn(), "both mods must be back in the chain")
    end)

    -- #4: set_on_file + enable_by_file (DMF integrates these via its
    -- hook_require). The require-wrap calls enable_by_file; set_on_file must
    -- replay its hook-create function against every existing require_store
    -- instance via the dynamic Mods.require_store[path][N].func_name loadstring.
    runner.register("hook: set_on_file replays against existing require_store instances", function()
        local sb = setup()
        -- Seed two already-require'd instances of "fake/path", each with an
        -- .update method (the dynamic loadstring path targets these).
        sb.Mods.require_store["fake/path"] = {
            { update = function() return "orig1" end },
            { update = function() return "orig2" end },
        }
        sb.Mods.hook.set_on_file("ModA", "fake/path", "update",
            function(prev, ...) return "A[" .. prev(...) .. "]" end)

        -- Both instances must be hooked: the dynamic per-index loadstring path
        -- resolved and attached a chain to each.
        runner.assert_eq("A[orig1]", sb.Mods.require_store["fake/path"][1].update(),
            "instance 1 must run the hook chain")
        runner.assert_eq("A[orig2]", sb.Mods.require_store["fake/path"][2].update(),
            "instance 2 must run the hook chain")
    end)

    runner.register("hook: enable_by_file attaches a stored hook to a newly-required instance", function()
        local sb = setup()
        -- Register a set_on_file hook BEFORE any instance is require'd.
        sb.Mods.hook.set_on_file("ModA", "fake/path", "update",
            function(prev, ...) return "A[" .. prev(...) .. "]" end)
        runner.assert_eq(1, #sb.MODS_HOOKS_BY_FILE["fake/path"],
            "set_on_file must store the hook-create function")

        -- Simulate a require: append a fresh instance, then call enable_by_file
        -- (mirrors what the require wrap does on each require).
        sb.Mods.require_store["fake/path"] = {
            { update = function() return "orig" end },
        }
        sb.Mods.hook.enable_by_file("fake/path", 1)

        runner.assert_eq("A[orig]", sb.Mods.require_store["fake/path"][1].update(),
            "enable_by_file must replay the stored hook against the new instance")
    end)
end
