-- file.lua — Mods.file.* helpers, rooted at MAGOS_MOD_PATH.
--
-- Adapts DML's mods/base/function file IO helpers (read_or_execute, handle_io,
-- get_file_path) but roots every path at MAGOS_MOD_PATH (set by the C trampoline
-- and captured into Mods._staging_base by the mod loader entry) instead of
-- DML's hardcoded "./../mods". Uses Mods.lua.io + Mods.lua.loadstring for all
-- file access (the engine's real io/loadstring, captured before they're
-- stripped from globals ~pcall#6).
--
-- DMF's expected entry surface (called from DMF's mod_manager / loader):
--   Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")
--     -> <MAGOS_MOD_PATH>/dmf/scripts/mods/dmf/dmf_loader.lua  (.lua appended)
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
    -- Mods._staging_base is set by the entry; fall back to MAGOS_MOD_PATH (which
    -- the trampoline sets as a real global before the entry runs).
    return Mods._staging_base or MAGOS_MOD_PATH
end

-- Normalize backslashes to forward slashes (Windows/Proton path-compat).
local function _to_forward_slashes(p)
    return (p:gsub("\\", "/"))
end

-- Build a staging-rooted path. Mirrors DML's get_file_path but with
-- MAGOS_MOD_PATH as the base and always-forward-slash output.
--   get_file_path(local_path, file_name, file_extension)
--     -> "<staging>/<local_path>/<file_name>.<file_extension or 'lua'>"
-- nil/empty components are skipped. file_extension defaults to "lua".
--
-- Staging-confinement check: if any path segment in local_path, file_name, OR
-- file_extension is exactly "..", the request escapes the staging root (e.g.
-- Mods.file.dofile("../../etc/passwd"), or Mods.file.exec("x","y","../../evil")
-- — the extension is appended as "." .. file_extension, so an unchecked ".."
-- there yields a path with resolvable traversal segments) and is rejected ->
-- returns nil so the caller's io.open fails cleanly (handle_io short-circuits
-- to false). This is the future-sandbox boundary; today a malicious mod already
-- has full Lua via Mods.lua.io, so this grants no new capability, but the
-- surface is locked down now while it's cheap.
--
-- A single trailing "/" or "\" on the staging base is stripped so the join
-- never produces a doubled separator ("<base>//foo").
local function has_traversal(p)
    if not p or p == "" then return false end
    local norm = _to_forward_slashes(p)
    for seg in norm:gmatch("[^/]+") do
        if seg == ".." then return true end
    end
    return false
end

local function get_file_path(local_path, file_name, file_extension)
    -- Strip a single trailing separator from the base (forward-proofing: real
    -- filesystems tolerate "//", but the mock io and brittle tests don't).
    local base = _staging_base() or ""
    if #base > 0 then
        base = base:gsub("[/\\]$", "")
    end

    -- Reject any path that would escape the staging root. All three components
    -- are checked: file_extension is appended as "." .. file_extension, so an
    -- unchecked traversal there (e.g. "../../etc/passwd") would bypass the
    -- local_path/file_name confinement.
    if has_traversal(local_path) or has_traversal(file_name) or has_traversal(file_extension) then
        return nil
    end

    local file_path = base
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
--
-- The file handle is closed BEFORE the exec (loadstring + fn(args)). The exec
-- can raise (malformed chunk -> loadstring throws; chunk body errors at call),
-- and in the safe_call path that raise propagates to handle_io's pcall — so
-- closing first guarantees f:close() runs on every path rather than leaking
-- past the throw (inherited DML bug; low impact — GC finalizes, mods load once
-- at boot — but cheap to fix). Behavior for the success + safe-call-failure
-- paths is unchanged.
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
                -- Skip blank lines and line comments ("--"). NOTE:
                -- line-comment-only; Lua block comments ("--[[ ]]") are NOT
                -- recognized — a "--[[" opener is dropped as a comment but the
                -- lines inside the block are kept as content. Matches DML's
                -- behavior (these files are mod_load_order.txt-style, no block
                -- comments).
                if line ~= "" and line:sub(1, 2) ~= "--" then
                    table.insert(result, line)
                end
            end
        end
        f:close()
    else
        result = f:read("*all")
        f:close()
        if return_type == "exec_result" or return_type == "exec_boolean" then
            local fn = assert(loadstring(result, file_path))
            result = fn(args)
        end
    end

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
    if file_path == nil then
        -- Staging-confinement check rejected the path (contains "..").
        return false
    end

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

-- exists(name): probe a path with io.open. NOTE: unlike the other Mods.file.*
-- helpers, `name` is NOT rooted at MAGOS_MOD_PATH — it's opened as-is (absolute
-- or engine-relative). This matches DML's surface; callers pass fully-qualified
-- paths. Use dofile/exec/read_content for staging-relative access (those route
-- through get_file_path and its confinement check).
Mods.file.exists = function(name)
    local io = _lua().io
    local f = io.open(name, "r")
    if f ~= nil then
        f:close()
        return true
    end
    return false
end
