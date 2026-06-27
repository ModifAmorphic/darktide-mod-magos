-- mock.lua — mocked engine environment for the offline LuaJIT harness.
--
-- Provides per-test isolation via sandboxing (setfenv): each module under test
-- runs in a fresh sandbox that has the real stdlib, a sandbox-aware loadstring
-- (so DML's loadstring-driven hook chain resolves against the sandbox, not the
-- real _G), and whatever mocks the test injects (Mods, io, require, class).
--
-- Module sources are read from the real filesystem (../<name>.lua relative to
-- this tests/ dir) and loaded into a sandbox with mock.load_module.
--
-- TWO-ROOT MODEL (mirrors the production split the C trampoline sets up):
--   - Enginseer root  (MAGOS_ENGINSEER_PATH) — runtime-controlled; holds
--     enginseer.lua + its active modules. bootstrap_load roots here. The mock
--     default is mock.ENGINSEER_ROOT ("/enginseer"); mock.stage_enginseer()
--     builds the files map a test io mock serves for it.
--   - Mod root        (MAGOS_MOD_PATH) — user/mod-manager-controlled; holds
--     mod_load_order.txt + dmf/ + user mods. Mods.file.* roots here. The mock
--     default is mock.MOD_ROOT ("/mods"); each test stages its own mods/DMF.

local _G = _G
local assert = assert
local error = error
local getinfo = debug.getinfo
local ipairs = ipairs
local io = io
local loadstring = loadstring
local setfenv = setfenv
local tostring = tostring

local M = {}

-- The two mock roots (production split). Tests set these onto the sandbox as
-- MAGOS_ENGINSEER_PATH / MAGOS_MOD_PATH; the entry's bootstrap_load roots at
-- the Enginseer root, Mods.file.* at the mod root.
M.ENGINSEER_ROOT = "/enginseer"
M.MOD_ROOT = "/mods"

-- The Enginseer's active modules (the entry bootstraps them in this order).
-- Excludes enginseer.v1.lua (fallback) + tests/ (harness) — neither is runtime.
M.ENGINSEER_MODULES = { "file", "hook", "class_patch", "require_wrap", "lifecycle" }

-- stdlib globals every sandbox starts with (so the modules' locals like
-- `local pairs = pairs` resolve).
local STDLIB = {
    "print", "pairs", "ipairs", "tostring", "tonumber", "type",
    "assert", "error", "pcall", "xpcall", "select",
    "setmetatable", "getmetatable", "rawget", "rawset", "rawequal", "rawlen",
    "unpack", "setfenv", "getfenv", "loadstring", "load", "loadfile", "dofile",
    "next", "string", "table", "math", "os", "coroutine", "debug",
    "_VERSION",
}

-- Build a fresh sandbox: real stdlib + sandbox-aware loadstring + _G = sandbox.
-- The sandbox-aware loadstring is what makes DML's loadstring-driven hook chain
-- (Mods.hook._patch's eval'd statements, Mods.hook._get_func, deferred flush)
-- resolve against the sandbox instead of the real _G.
function M.new_sandbox()
    local sb = {}
    for _, k in ipairs(STDLIB) do
        sb[k] = _G[k]
    end
    local real_loadstring = _G.loadstring
    sb.loadstring = function(src, name)
        local fn, err = real_loadstring(src, name)
        if fn then
            setfenv(fn, sb)
        end
        return fn, err
    end
    sb._G = sb
    return sb
end

-- In-memory io mock. `files` is a map of absolute path -> content string.
-- Returned table has .open(path, mode) -> file object with :read/:lines/:close,
-- plus .close(f) for parity with the real io API.
function M.make_io(files)
    local iot = {}

    local function make_file(content)
        local f = {}
        local pos = 1
        function f:read(opt)
            if opt == nil or opt == "*all" or opt == "a" then
                local r = content:sub(pos)
                pos = #content + 1
                return r
            elseif opt == "*l" or opt == "l" then
                local nl = content:find("\n", pos, true)
                local line
                if nl then
                    line = content:sub(pos, nl - 1)
                    pos = nl + 1
                else
                    line = content:sub(pos)
                    pos = #content + 1
                end
                return line
            elseif opt == "L" then
                local nl = content:find("\n", pos, true)
                local line
                if nl then
                    line = content:sub(pos, nl)
                    pos = nl + 1
                else
                    line = content:sub(pos)
                    pos = #content + 1
                end
                return line
            elseif opt == "*n" or opt == "n" then
                local s, e, num = content:find("^%s*([-%d.]+)", pos)
                if num then
                    pos = e + 1
                    return tonumber(num)
                end
                return nil
            end
            error("mock io: unsupported read opt " .. tostring(opt))
        end
        function f:lines(...)
            -- DML's read_or_execute uses the no-arg iterator form.
            return function()
                if pos > #content then return nil end
                local nl = content:find("\n", pos, true)
                local line
                if nl then
                    line = content:sub(pos, nl - 1)
                    pos = nl + 1
                else
                    line = content:sub(pos)
                    pos = #content + 1
                end
                return line
            end
        end
        function f:close() end
        return f
    end

    function iot.open(path, mode)
        local content = files[path]
        if content == nil then
            return nil, path .. ": mock not found"
        end
        return make_file(content)
    end
    function iot.close(f)
        if f and f.close then f:close() end
    end
    function iot.lines(path)
        local content = files[path]
        if content == nil then error("mock io: not found " .. path) end
        local f = make_file(content)
        return f:lines()
    end
    return iot
end

-- Resolve the directory of this file so test files can find sibling modules.
local this_dir = getinfo(1, "S").source:sub(2):match("(.*/)") or "./"
M.module_dir = this_dir .. "../"

-- Read a real file from disk (for loading module sources under test).
function M.read_file(path)
    local f = assert(io.open(path, "r"))
    local data = f:read("*all")
    f:close()
    return data
end

-- Read a module source from runtime/enginseer/<name>.lua.
function M.read_module(name)
    return M.read_file(M.module_dir .. name .. ".lua")
end

-- Build the Enginseer-root files map (the runtime-controlled root): enginseer.lua
-- + every active module, keyed at <ENGINSEER_ROOT>/<name>.lua. Mirrors the
-- deployment contract (bin/enginseer/) and what bootstrap_load expects to open.
-- A test merges this into its io-mock files map (and adds its own mod-root
-- files under MOD_ROOT for DMF/mods/mod_load_order).
function M.stage_enginseer()
    local files = {}
    for _, name in ipairs(M.ENGINSEER_MODULES) do
        files[M.ENGINSEER_ROOT .. "/" .. name .. ".lua"] = M.read_module(name)
    end
    files[M.ENGINSEER_ROOT .. "/enginseer.lua"] = M.read_module("enginseer")
    return files
end

-- Compile + load a module source into a sandbox. Returns the chunk (caller runs
-- it). Equivalent to the entry's bootstrap_load minus the io step.
function M.load_module(name, sb)
    local src = M.read_module(name)
    local fn = assert(loadstring(src, name .. ".lua"))
    setfenv(fn, sb)
    return fn
end

-- Convenience: load + run a module into a sandbox.
function M.run_module(name, sb)
    return M.load_module(name, sb)()
end

return M
