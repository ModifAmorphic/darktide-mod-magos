-- mock.lua — mocked engine environment for the offline LuaJIT harness.
--
-- Provides per-test isolation via sandboxing (setfenv): each module under test
-- runs in a fresh sandbox that has the real stdlib, a sandbox-aware loadstring
-- (so DML's loadstring-driven hook chain resolves against the sandbox, not the
-- real _G), and whatever mocks the test injects (Mods, io, require, class).
--
-- Module sources are read from the real filesystem (../<name>.lua relative to
-- this tests/ dir) and loaded into a sandbox with mock.load_module.

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
