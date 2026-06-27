-- enginseer.lua — Enginseer (the Mod Loader) — minimal v1
-- Runs at pcall#1 in engine-context (injected by the runtime trampoline).
-- Captures the engine's real Lua facilities into the Mods table BEFORE
-- the engine removes io/loadstring (~pcall#10).

-- 1. Capture the engine's real facilities (they're present at pcall#1).
Mods = Mods or {}
Mods.original_require = require       -- the engine's real require
Mods.require_store = {}               -- DMF's require tracking table
Mods.lua = Mods.lua or {}
Mods.lua.loadstring = loadstring      -- the engine's real loadstring
Mods.lua.io = io                      -- the engine's real io table
Mods.file = Mods.file or {}
-- Mods.file.dofile will be added later (needs the staging path).
__print = print                       -- backup of the engine's print

-- 2. Log success (using the engine's print, which is still available).
print("[Enginseer] Mod Loader v1 — running in engine-context.")
print("[Enginseer] Captured: require=" .. tostring(Mods.original_require)
  .. " io=" .. tostring(Mods.lua.io)
  .. " loadstring=" .. tostring(Mods.lua.loadstring)
  .. " print=" .. tostring(__print))

-- 3. Return success (the trampoline's pcall will get this).
return true
