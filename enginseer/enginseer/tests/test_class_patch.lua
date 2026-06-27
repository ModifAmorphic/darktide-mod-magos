-- test_class_patch.lua — Mods.install_class_patch behavior.
--
-- Coverage (acceptance criteria from the spec):
--   - After install, calling _G.class("Foo") registers CLASS.Foo (a table) and
--     globalizes _G.Foo.
--   - Mods.original_class is the pre-patch class.
--   - Double-install is a no-op.
--   - CLASS's __index returns the key string for unregistered names (DML quirk
--     relied on by the deferred-hook flush's not-ready check).

local mock = require("mock")

return function(runner)
    -- A fake engine class() that builds a class table from a name. The real
    -- engine's class() is much richer (inheritance, etc.) — for the patch test
    -- we only need it to be a function that returns something taggable.
    local function fake_engine_class()
        return function(class_name, super_name, ...)
            return { _classname = class_name }
        end
    end

    local function setup(with_class)
        local sb = mock.new_sandbox()
        sb.Mods = { file = {}, lua = {} }
        if with_class then
            sb.class = fake_engine_class()
        end
        mock.run_module("class_patch", sb)
        return sb
    end

    runner.register("class_patch: install_class_patch is exposed", function()
        local sb = setup(false)
        runner.assert_type("function", sb.Mods.install_class_patch)
    end)

    runner.register("class_patch: deferred no-op while _G.class is not a function", function()
        local sb = setup(false)
        sb.Mods.install_class_patch()
        runner.assert_nil(sb.Mods._class_patch_installed, "should not mark installed without _G.class")
        runner.assert_nil(sb.Mods.original_class)
        runner.assert_nil(sb.CLASS)
    end)

    runner.register("class_patch: captures original_class + installs when class is present", function()
        local sb = setup(true)
        local original = sb.class
        sb.Mods.install_class_patch()
        runner.assert_eq(original, sb.Mods.original_class, "original_class must be the pre-patch class")
        runner.assert_type("table", sb.CLASS)
        runner.assert_type("function", sb.class, "class must still be a function after patching")
        runner.assert_truthy(sb.class ~= original, "class must be the WRAPPED function, not the original")
        runner.assert_truthy(sb.Mods._class_patch_installed)
    end)

    runner.register("class_patch: class('Foo') registers CLASS.Foo and globalizes _G.Foo", function()
        local sb = setup(true)
        sb.Mods.install_class_patch()
        local result = sb.class("Foo")
        runner.assert_type("table", result)
        runner.assert_eq("Foo", result._classname)
        runner.assert_eq(result, sb.CLASS.Foo, "CLASS.Foo must hold the result")
        runner.assert_eq(result, sb.Foo, "_G.Foo must be the result")
    end)

    runner.register("class_patch: does not overwrite an existing _G global of the same name", function()
        local sb = setup(true)
        sb.Mods.install_class_patch()
        local pre_existing = { preset = true }
        sb.PreSet = pre_existing
        local result = sb.class("PreSet")
        runner.assert_eq(pre_existing, sb.PreSet, "must not overwrite an existing _G global")
        runner.assert_eq(result, sb.CLASS.PreSet, "must still register in CLASS")
    end)

    runner.register("class_patch: double-install is a no-op (does not re-wrap)", function()
        local sb = setup(true)
        sb.Mods.install_class_patch()
        local first_wrap = sb.class
        sb.Mods.install_class_patch()
        runner.assert_eq(first_wrap, sb.class, "second install must not replace the wrapper")
    end)

    runner.register("class_patch: CLASS __index returns the key string for unregistered names", function()
        local sb = setup(true)
        sb.Mods.install_class_patch()
        runner.assert_eq("NotYetDefined", sb.CLASS.NotYetDefined,
            "DML quirk: CLASS.__index returns the key string when not registered")
    end)

    runner.register("class_patch: subsequent require-wrap calls are cheap no-ops", function()
        -- The require-wrap calls install_class_patch on every require; verify
        -- that many calls don't stack wraps or mutate state.
        local sb = setup(true)
        sb.Mods.install_class_patch()
        local wrapped = sb.class
        for _ = 1, 5 do
            sb.Mods.install_class_patch()
        end
        runner.assert_eq(wrapped, sb.class)
        sb.class("Repeated")
        runner.assert_eq("Repeated", sb.CLASS.Repeated._classname)
    end)

    runner.register("class_patch: after install, class() forwards extra args to original", function()
        local sb = mock.new_sandbox()
        sb.Mods = { file = {}, lua = {} }
        -- Track the args the original class() receives.
        local received
        sb.class = function(name, super, ...)
            received = { name = name, super = super, vararg = { ... } }
            return { _classname = name }
        end
        mock.run_module("class_patch", sb)
        sb.Mods.install_class_patch()
        sb.class("Child", "Parent", "extra1", "extra2")
        runner.assert_eq("Child", received.name)
        runner.assert_eq("Parent", received.super)
        runner.assert_eq({ "extra1", "extra2" }, received.vararg)
    end)
end
