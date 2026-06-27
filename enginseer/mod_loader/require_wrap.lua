-- require_wrap.lua — exposes Mods.install_require_wrap().
--
-- Called by the entry at pcall#1 (BEFORE main.lua executes). Adapts DML's
-- mods/base/function/require.lua (cache require results in Mods.require_store,
-- trigger enable_by_file for set_on_file hooks) and adds the deferred bridge
-- (our novel mechanism, since we run BEFORE main.lua):
--
--   after each require:
--     (a) one-shot: install the class patch once _G.class is a function. The
--         patch internally no-ops until then, so we just call it every require;
--         it self-disables after the first effective install.
--     (b) flush the deferred-hook queue: each pending hook whose target now
--         resolves gets installed via Mods.hook.set and dequeued. This is how
--         the bootstrap lifecycle hook attaches to
--         CLASS.BootStateRequireGameScripts._state_update once that class is
--         created (late in the boot state sequence).
--
-- LIVE-VALIDATE: that the engine's require calls route through our wrapped
-- global require. DML relies on this same global-require indirection and it's
-- proven for the community loader, but our injection path is new.

local table = table
local type = type

Mods.install_require_wrap = function()
    -- Idempotent: never double-wrap (would nest require stores + flushes).
    if Mods._require_wrapped then return end
    Mods._require_wrapped = true

    -- Capture the engine's real require lazily so any test-time tweak to
    -- Mods.original_require after the wrap is installed is honored.
    local function _original(filepath, ...)
        return Mods.original_require(filepath, ...)
    end

    require = function(filepath, ...)
        local result = _original(filepath, ...)

        -- Cache require results (mirror DML): table results go into
        -- Mods.require_store[filepath]; identical consecutive results are
        -- de-duplicated. set_on_file hooks replay against new instances.
        if result and type(result) == "table" then
            local store = Mods.require_store[filepath]
            local can_insert = (not store) or (#store == 0) or (store[#store] ~= result)
            if can_insert then
                Mods.require_store[filepath] = Mods.require_store[filepath] or {}
                table.insert(Mods.require_store[filepath], result)
                if Mods.hook and Mods.hook.enable_by_file then
                    Mods.hook.enable_by_file(filepath, #Mods.require_store[filepath])
                end
            end
        end

        -- Deferred bridge (loader-specific; not in DML).
        if Mods.install_class_patch then
            Mods.install_class_patch()
        end
        if Mods.flush_deferred_hooks then
            Mods.flush_deferred_hooks()
        end

        return result
    end
end
