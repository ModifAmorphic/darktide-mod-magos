-- hook.lua — Mods.hook + globals MODS_HOOKS, MODS_HOOKS_BY_FILE.
--
-- Adapted nearly verbatim from DML's mods/base/function/hook.lua. The hook chain
-- is loadstring-driven: hook.set rewrites the target function to call through a
-- chain of per-mod wrappers, each of which receives the previous link (or the
-- original) as its first argument. Depends on Mods.lua.loadstring (captured by
-- the entry before the engine strips loadstring from globals ~pcall#6).
--
-- Differences vs DML:
--   - drops set_on_file / front (not needed for the v2 surface; require_wrap's
--     enable_by_file path is retained for DMF's require-hook integration).
--   - inlines EMPTY_FUNC + per-item/per-hook constructors (DML used table.clone
--     over templates, an engine extension we don't have at pcall#1).
--   - log helpers fall back to print when DMF's Log isn't present yet (early
--     boot — Log appears mid-init).

local _loadstring = Mods.lua.loadstring
local ipairs = ipairs
local print = print
local rawget = rawget
local table = table
local tostring = tostring

MODS_HOOKS = MODS_HOOKS or {}
MODS_HOOKS_BY_FILE = MODS_HOOKS_BY_FILE or {}

local EMPTY_FUNC = function() end

local function log_info(mod_name, message)
    local Log = rawget(_G, "Log")
    if Log and Log._info then
        Log._info(mod_name, message)
    else
        print("[" .. mod_name .. "] " .. message)
    end
end

local function new_hook_item(func_name, orig_func)
    return {
        name = func_name,
        func = orig_func,
        hooks = {},
    }
end

local function new_hook_entry(mod_name)
    return {
        name = mod_name,
        func = EMPTY_FUNC,
        enable = false,
        exec = EMPTY_FUNC,
    }
end

Mods.hook = Mods.hook or {}

--
-- Set hook: register (or replace) the named mod's hook on func_name and rebuild
-- the chain. func_name is a loadstring-resolvable path like
-- "CLASS.StateGame.update" or "SomeGlobal.method".
--
Mods.hook.set = function(mod_name, func_name, hook_func)
    local item = Mods.hook._get_item(func_name)
    local item_hook = Mods.hook._get_item_hook(item, mod_name)

    log_info(mod_name, "Hooking " .. func_name)

    item_hook.enable = true
    item_hook.func = hook_func

    Mods.hook._patch()
end

--
-- Set hook on every instance of a require'd file (DMF integrates this via
-- Mods.hook.enable_by_file from the require wrap). The hook_create_func is
-- stored against the filepath and replayed for every existing + future instance.
--
Mods.hook.set_on_file = function(mod_name, filepath, func_name, hook_func)
    MODS_HOOKS_BY_FILE[filepath] = MODS_HOOKS_BY_FILE[filepath] or {}
    local hook_create_func = function(this_filepath, this_index)
        local dynamic_func_name = "Mods.require_store[\"" .. this_filepath .. "\"]["
            .. tostring(this_index) .. "]." .. func_name
        Mods.hook.set(mod_name, dynamic_func_name, hook_func)
    end
    table.insert(MODS_HOOKS_BY_FILE[filepath], hook_create_func)

    local all_file_instances = Mods.require_store[filepath]
    if all_file_instances then
        for i, item in ipairs(all_file_instances) do
            if item then
                hook_create_func(filepath, i)
            end
        end
    end
end

--
-- Enable/disable a hook. With mod_name: toggle that mod's hook on func_name.
-- With mod_name == nil: toggle EVERY mod's hook on func_name (matches DML —
-- nil mod_name = all mods). Rebuilds the chain once per enable call (not once
-- per matching hook) so the change takes effect immediately.
--
Mods.hook.enable = function(value, mod_name, func_name)
    for _, item in ipairs(MODS_HOOKS) do
        if item.name == func_name or func_name == nil then
            for _, hook in ipairs(item.hooks) do
                if mod_name == nil or hook.name == mod_name then
                    hook.enable = value
                end
            end
        end
    end
    -- Rebuild the chain once per enable call (hoisted out of the inner loop so
    -- a multi-match enable doesn't _patch N times).
    Mods.hook._patch()
end

--
-- Replay every set_on_file hook against a newly-require'd file instance. Called
-- by the require wrap when a require result is appended to Mods.require_store.
--
Mods.hook.enable_by_file = function(filepath, store_index)
    local all_file_instances = Mods.require_store[filepath]
    local file_instance = all_file_instances and all_file_instances[store_index]
    local all_file_hooks = MODS_HOOKS_BY_FILE[filepath]

    if all_file_hooks and file_instance then
        for _, hook_create_func in ipairs(all_file_hooks) do
            hook_create_func(filepath, store_index)
        end
    end
end

--
-- Remove a hook. With mod_name: remove just that mod's hook from the chain.
-- Without mod_name: restore the original function and drop the whole item.
--
Mods.hook["remove"] = function(func_name, mod_name)
    for i, item in ipairs(MODS_HOOKS) do
        if item.name == func_name then
            if mod_name ~= nil then
                for j, hook in ipairs(item.hooks) do
                    if hook.name == mod_name then
                        table.remove(item.hooks, j)
                        Mods.hook._patch()
                    end
                end
            else
                local item_name = "MODS_HOOKS[" .. tostring(i) .. "]"
                -- Restore original.
                assert(_loadstring(item.name .. " = " .. item_name .. ".func"))()
                table.remove(MODS_HOOKS, i)
                return
            end
        end
    end
end

-- Resolve a func_name to its current value via loadstring. Used both internally
-- (to capture the original at hook-set time) and by the deferred-hook flush to
-- test whether a target exists yet.
Mods.hook._get_func = function(func_name)
    return assert(_loadstring("return " .. func_name))()
end

-- Find or create the MODS_HOOKS entry for func_name. Captures the original
-- function once (the first set); subsequent sets reuse it so the chain can be
-- torn down to the real original.
Mods.hook._get_item = function(func_name)
    for _, item in ipairs(MODS_HOOKS) do
        if item.name == func_name then
            return item
        end
    end
    local item = new_hook_item(func_name, Mods.hook._get_func(func_name))
    table.insert(MODS_HOOKS, item)
    return item
end

-- Find or create the per-mod hook entry inside an item. New hooks are inserted
-- at index 1, so the LAST set is at the FRONT of the chain (the chain itself
-- runs first-installed-first; see _patch).
Mods.hook._get_item_hook = function(item, mod_name)
    for _, hook in ipairs(item.hooks) do
        if hook.name == mod_name then
            return hook
        end
    end
    local item_hook = new_hook_entry(mod_name)
    table.insert(item.hooks, 1, item_hook)
    return item_hook
end

-- Rebuild the exec chain for every item and reassign each target. This is the
-- loadstring-driven core: each hook's .exec is compiled to call .func with the
-- previous link's .exec (or, for the front of the chain, the item's original
-- .func) as the first arg. Disabled hooks pass through to the previous link
-- without invoking their own .func. The target global is then rewritten to the
-- tail hook's .exec (the outermost wrapper).
Mods.hook._patch = function()
    for i, item in ipairs(MODS_HOOKS) do
        local item_name = "MODS_HOOKS[" .. tostring(i) .. "]"
        local last_j = 1
        for j, hook in ipairs(item.hooks) do
            local hook_name = item_name .. ".hooks[" .. tostring(j) .. "]"
            local before_hook_name = item_name .. ".hooks[" .. tostring(j - 1) .. "]"
            if j == 1 then
                if hook.enable then
                    assert(_loadstring(
                        hook_name .. ".exec = function(...) "
                        .. "return " .. hook_name .. ".func(" .. item_name .. ".func, ...) "
                        .. "end"
                    ))()
                else
                    assert(_loadstring(
                        hook_name .. ".exec = function(...) "
                        .. "return " .. item_name .. ".func(...) "
                        .. "end"
                    ))()
                end
            else
                if hook.enable then
                    assert(_loadstring(
                        hook_name .. ".exec = function(...) "
                        .. "return " .. hook_name .. ".func(" .. before_hook_name .. ".exec, ...) "
                        .. "end"
                    ))()
                else
                    assert(_loadstring(
                        hook_name .. ".exec = function(...) "
                        .. "return " .. before_hook_name .. ".exec(...) "
                        .. "end"
                    ))()
                end
            end
            last_j = j
        end
        -- Reassign the target global to the outermost wrapper.
        assert(_loadstring(item.name .. " = " .. item_name .. ".hooks[" .. tostring(last_j) .. "].exec"))()
    end
end
