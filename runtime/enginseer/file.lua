-- file.lua — Mods.file.* helpers, rooted at MAGOS_STAGING.
--
-- Adapts DML's mods/base/function file IO helpers (read_or_execute, handle_io,
-- get_file_path) but roots every path at MAGOS_STAGING (set by the C trampoline
-- and captured into Mods._staging_base by the Enginseer entry) instead of
-- DML's hardcoded "./../mods". Uses Mods.lua.io + Mods.lua.loadstring for all
-- file access (the engine's real io/loadstring, captured before they're
-- stripped from globals ~pcall#6).
--
-- DMF's expected entry surface (called from DMF's mod_manager / loader):
--   Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")
--     -> <MAGOS_STAGING>/dmf/scripts/mods/dmf/dmf_loader.lua  (.lua appended)
--
-- All produced paths use forward slashes (works on Windows + Proton).

local assert = assert
local ipairs = ipairs
local pairs = pairs
local pcall = pcall
local print = print
local string = string
local table = table
local tostring = tostring

Mods.file = Mods.file or {}

-- Lazy accessors so a test/late-binding tweak to Mods.lua / Mods._staging_base
-- is honored at call time, not at module load.
local function _lua()
    return Mods.lua
end

local function _staging_base()
    -- Mods._staging_base is set by the entry; fall back to MAGOS_STAGING (which
    -- the trampoline sets as a real global before the entry runs).
    return Mods._staging_base or MAGOS_STAGING
end

-- Normalize backslashes to forward slashes (Windows/Proton path-compat).
local function _to_forward_slashes(p)
    return (p:gsub("\\", "/"))
end

-- Build a staging-rooted path. Mirrors DML's get_file_path but with
-- MAGOS_STAGING as the base and always-forward-slash output.
--   get_file_path(local_path, file_name, file_extension)
--     -> "<staging>/<local_path>/<file_name>.<file_extension or 'lua'>"
-- nil/empty components are skipped. file_extension defaults to "lua".
local function get_file_path(local_path, file_name, file_extension)
    local file_path = _staging_base() or ""
    if local_path and local_path ~= "" then
        file_path = file_path .. "/" .. local_path
    end
    if file_name and file_name ~= "" then
        file_path = file_path .. "/" .. file_name
    end
    if file_extension and file_extension ~= "" then
        file_path = file_path .. "." .. file_extension
    else
        file_path = file_path .. ".lua"
    end
    return _to_forward_slashes(file_path)
end
Mods.file.get_file_path = get_file_path

-- Read a file's content and either return it raw, parse it line-by-line, or
-- loadstring+execute it. Mirrors DML's read_or_execute. The caller must have
-- already confirmed the file exists (handle_io does that).
local function read_or_execute(file_path, args, return_type)
    local io = _lua().io
    local loadstring = _lua().loadstring
    local f = io.open(file_path, "r")

    local result
    if return_type == "lines" then
        result = {}
        for line in f:lines() do
            if line then
                -- Trim leading/trailing whitespace.
                line = line:gsub("^%s*(.-)%s*$", "%1")
                -- Skip blank lines and single-line comments.
                if line ~= "" and line:sub(1, 2) ~= "--" then
                    table.insert(result, line)
                end
            end
        end
    else
        result = f:read("*all")
        if return_type == "exec_result" or return_type == "exec_boolean" then
            local fn = assert(loadstring(result, file_path))
            result = fn(args)
        end
    end

    f:close()
    if return_type == "exec_boolean" then
        return true
    else
        return result
    end
end

-- Open-or-fail wrapper. Returns false on missing file or (in safe_call mode)
-- on exec error; otherwise the read_or_execute result. Mirrors DML's handle_io.
local function handle_io(local_path, file_name, file_extension, args, safe_call, return_type)
    local io = _lua().io
    local file_path = get_file_path(local_path, file_name, file_extension)

    -- Existence probe (mirrors DML): the read_or_execute path assumes f ~= nil.
    local ff, err_io = io.open(file_path, "r")
    if ff == nil then
        return false
    end
    ff:close()

    if safe_call then
        local status, result = pcall(function()
            return read_or_execute(file_path, args, return_type)
        end)
        if not status then
            -- DML notifies here; we keep the surface silent (the caller decides
            -- what to do with `false`). The error is dropped to mirror DML's
            -- "return false on safe_call failure" contract.
            return false
        end
        return result
    else
        return read_or_execute(file_path, args, return_type)
    end
end

-- Public surface — names match DML's Mods.file.* exactly so DMF (unmodified)
-- resolves them.
Mods.file.exec = function(local_path, file_name, file_extension, args)
    return handle_io(local_path, file_name, file_extension, args, true, "exec_boolean")
end

Mods.file.exec_unsafe = function(local_path, file_name, file_extension, args)
    return handle_io(local_path, file_name, file_extension, args, false, "exec_boolean")
end

Mods.file.exec_with_return = function(local_path, file_name, file_extension, args)
    return handle_io(local_path, file_name, file_extension, args, true, "exec_result")
end

Mods.file.exec_unsafe_with_return = function(local_path, file_name, file_extension, args)
    return handle_io(local_path, file_name, file_extension, args, false, "exec_result")
end

-- dofile(path): path is relative to staging; ".lua" appended (DML contract).
-- DMF calls Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader").
Mods.file.dofile = function(file_path, args)
    return handle_io(file_path, nil, nil, args, true, "exec_result")
end

Mods.file.read_content = function(file_path, file_extension)
    return handle_io(file_path, nil, file_extension, nil, true, "data")
end

Mods.file.read_content_to_table = function(file_path, file_extension)
    return handle_io(file_path, nil, file_extension, nil, true, "lines")
end

Mods.file.exists = function(name)
    local io = _lua().io
    local f = io.open(name, "r")
    if f ~= nil then
        f:close()
        return true
    end
    return false
end
