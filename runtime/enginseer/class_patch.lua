-- class_patch.lua — exposes Mods.install_class_patch().
--
-- Called by the require-wrap (Mods.install_require_wrap) on every require until
-- it takes effect: once _G.class appears (during pcall#1, when main.lua require's
-- scripts/foundation/utilities/class.lua), wrap it so every subsequent class()
-- call registers its result in CLASS and force-globalizes it.
--
-- The engine's state classes (StateGame, GameStateMachine, BootStateRequireGameScripts,
-- etc.) are NEVER bare _G globals — they're module-local. The only handle we can
-- get is via this monkey-patch: each class() call goes through our wrapper, which
-- records the result in CLASS. Then the deferred-hook queue can resolve
-- "CLASS.BootStateRequireGameScripts._state_update" once that class is created.
--
-- Adapted from DML's mods/base/function/class.lua with:
--   - explicit guard against double-install (the require-wrap checks on every
--     require; we want the patch to apply exactly once);
--   - deferred guard: no-op while _G.class is not yet a function (so the
--     require-wrap can call this unconditionally without pre-checking).

local _G = _G
local rawget = rawget
local rawset = rawset
local type = type

Mods.install_class_patch = function()
    -- Idempotent: never patch twice. The require-wrap may invoke this many times.
    if Mods._class_patch_installed then return end

    -- Deferred: no-op until the engine's class.lua has run and exposed _G.class.
    -- Returning without setting the flag lets the next require retry.
    -- LIVE-VALIDATE: that _G.class appears during pcall#1 (as the recon timeline
    -- predicts) so this fires before any engine class() call. If class.lua loads
    -- LATER than the first state-class definition, those classes won't be in CLASS.
    if type(_G.class) ~= "function" then return end

    Mods._class_patch_installed = true

    -- Capture the engine's real class() exactly once.
    Mods.original_class = Mods.original_class or _G.class

    -- CLASS is a global registry of every class() result. Its __index returns
    -- the key string when the class isn't registered yet — DML's quirk for
    -- legacy lookups (CLASS.Foo == "Foo"). In our deferred-hook resolution this
    -- means "return CLASS.NotYetDefined.method" indexes a string (returns nil
    -- via the string metatable) instead of raising, so the flush can detect
    -- not-yet-ready targets. Once class() runs for a name, the real class table
    -- is rawset over the slot.
    if not rawget(_G, "CLASS") then
        _G.CLASS = setmetatable({}, {
            __index = function(_, key)
                return key
            end,
        })
    end

    -- The wrapping class(). Mirrors DML: call the original, then register the
    -- result in CLASS and (if not already) as a _G global.
    _G.class = function(class_name, super_name, ...)
        local result = Mods.original_class(class_name, super_name, ...)
        if not rawget(_G, class_name) then
            rawset(_G, class_name, result)
        end
        if not rawget(_G.CLASS, class_name) then
            rawset(_G.CLASS, class_name, result)
        end
        return result
    end
end
