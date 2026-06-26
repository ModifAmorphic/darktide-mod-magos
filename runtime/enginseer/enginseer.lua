-- enginseer.lua — Enginseer v2 (the Mod Loader) entry.
--
-- Runs at pcall#1 in engine-context (injected by the runtime trampoline), BEFORE
-- main.lua executes. Captures the engine's Lua facilities (before they're
-- stripped from globals ~pcall#6), bootstrap-loads the helper modules from the
-- Enginseer root, wraps global require, and queues the bootstrap lifecycle hook.
-- The class patch + bootstrap hook fire LATER, deferred via the require-wrap as
-- main.lua executes.
--
-- Two roots (set as globals by the C trampoline before opening this entry):
--   - MAGOS_ENGINSEER_PATH — the Enginseer dir (runtime-controlled). THIS file
--     + its helper modules (file/hook/class_patch/require_wrap/lifecycle +
--     mod_manager) live here. bootstrap_load roots here.
--   - MAGOS_MOD_PATH — the mod dir (user/mod-manager-controlled). DMF + user
--     mods + mod_load_order live here. Mods.file.* roots here (via
--     Mods._staging_base, set below).
--
-- Supersedes enginseer.v1.lua (which only captured the stdlib into Mods). The
-- v1 file is kept alongside for recovery.
--
-- Module bootstrap order matters: file -> hook -> class_patch -> require_wrap
-- -> lifecycle. Each module assumes its dependencies are already on Mods/_G.
--
-- LIVE-VALIDATE: that the engine's require calls route through our wrapped
-- global require (high confidence — DML relies on the same global-require
-- indirection — but unconfirmed for our injection).
-- LIVE-VALIDATE: that _G.class appears during pcall#1 (the timeline predicts it
-- appears when main.lua requires foundation/utilities/class.lua), so the
-- require-wrap fires the class patch before any engine class() call.

-- 0. Idempotency guard. The C trampoline is one-shot, but if this entry ever
-- re-ran after global require is wrapped, the `Mods.original_require = require`
-- below would capture the WRAPPED function and clobber the saved original ->
-- infinite recursion on the next require. Bail if we've already loaded.
if Mods and Mods._v2_loaded then return true end

-- 1. Capture the engine's real facilities (present at pcall#1).
Mods = Mods or {}
Mods.original_require = require
Mods.require_store = {}
Mods.lua = Mods.lua or {}
Mods.lua.loadstring = loadstring
Mods.lua.io = io
-- os + ffi are captured here (before the engine strips stdlib ~pcall#6) because
-- DMF's debug modules (table_dump uses Mods.lua.os; dev_console uses
-- Mods.lua.ffi) deep-copy them at dmf init. `or` so they're nil-safe if the
-- global isn't present in this engine build (ffi is LuaJIT-only).
Mods.lua.os = Mods.lua.os or os
Mods.lua.ffi = Mods.lua.ffi or ffi
Mods.lua.pcall = pcall
Mods.lua.print = print
Mods.lua.pairs = pairs
Mods.lua.ipairs = ipairs
Mods.lua.tostring = tostring
Mods.lua.tonumber = tonumber
Mods.lua.type = type
Mods.lua.table = table
Mods.lua.string = string
Mods.file = Mods.file or {}
Mods._deferred_hooks = {}
-- The MOD root (DMF + user mods + mod_load_order). Mods.file.* roots here
-- (file.lua reads Mods._staging_base, falling back to MAGOS_MOD_PATH). Kept
-- distinct from the Enginseer root (below) so the loader's own modules load
-- from the runtime root regardless of where mods are staged.
Mods._staging_base = MAGOS_MOD_PATH
__print = print

-- 2. Bootstrap-load the helper modules from the Enginseer root. We use io.open +
-- loadstring (NOT the engine's require) since these files live in the Enginseer
-- dir (runtime-controlled, via MAGOS_ENGINSEER_PATH), not the bundle search path
-- or the mod dir. setfenv(fn, getfenv(1)) gives each loaded chunk the entry's
-- env so the modules share _G with the entry (in production this is the
-- engine's globals; in tests it's the test sandbox).
local _loadstring = loadstring
local _io = io
local _pcall = pcall

local function bootstrap_load(name)
    -- Forward-slash join: <MAGOS_ENGINSEER_PATH>/<name>.lua (works on Windows + Proton).
    local base = MAGOS_ENGINSEER_PATH or ""
    local path = base .. "/" .. name .. ".lua"

    local f, err = _io.open(path, "r")
    if not f then
        __print("[Enginseer] FATAL: cannot open " .. path .. ": " .. tostring(err))
        return false
    end
    local data = f:read("*all")
    f:close()

    local fn, lerr = _loadstring(data, path)
    if not fn then
        __print("[Enginseer] FATAL: cannot parse " .. path .. ": " .. tostring(lerr))
        return false
    end
    setfenv(fn, getfenv(1))  -- share the entry's env with the loaded module

    local ok, rerr = _pcall(fn)
    if not ok then
        __print("[Enginseer] FATAL: error running " .. path .. ": " .. tostring(rerr))
        return false
    end
    return true
end

-- Expose bootstrap_load so lifecycle.lua can load mod_manager from the Enginseer
-- root AFTER class() exists (mod_manager calls class("ModManager"), which only
-- appears once the class patch installs at boot — not at this entry's pcall#1).
Mods.load_enginseer_module = bootstrap_load

-- Dependency order: file -> hook -> class_patch -> require_wrap -> lifecycle.
local modules = { "file", "hook", "class_patch", "require_wrap", "lifecycle" }
for _, mod in ipairs(modules) do
    if not bootstrap_load(mod) then
        __print("[Enginseer] bootstrap aborted at module '" .. mod .. "'")
        return false
    end
end

-- 3. Wrap global require (the deferred bridge runs inside the wrap after each
-- require call: one-shot class patch + deferred-hook flush).
Mods.install_require_wrap()

-- 4. Queue the bootstrap lifecycle hook. It installs (deferred) once
-- CLASS.BootStateRequireGameScripts._state_update exists, then loads DMF + mods.
Mods.install_lifecycle_hooks()

-- 5. Done. The class patch and bootstrap hook fire later, deferred via the
-- require-wrap as main.lua executes.
__print("[Enginseer] v2 loaded at pcall#1")
Mods._v2_loaded = true
return true
