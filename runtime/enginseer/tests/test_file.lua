-- test_file.lua — Mods.file.* behavior.
--
-- Coverage (acceptance criteria from the spec):
--   - dofile reads + runs a staged file from the mock io map.
--   - path join uses forward slashes and the staging root (Windows + Proton).
--   - exec_with_return returns the chunk's return value.
--   - read_content_to_table parses lines (skips blanks/comments).

local mock = require("mock")

return function(runner)
    -- Common setup: a fresh sandbox with Mods.lua.io/loadstring wired to the
    -- in-memory io map and the sandbox-aware loadstring.
    local function setup(files)
        local sb = mock.new_sandbox()
        sb.MAGOS_STAGING = "/staging"
        local io = mock.make_io(files)
        sb.Mods = {
            lua = { io = io, loadstring = sb.loadstring },
            file = {},
            _staging_base = sb.MAGOS_STAGING,
        }
        sb.io = io  -- in case a chunk falls back to bare io
        mock.run_module("file", sb)
        return sb
    end

    runner.register("file: dofile reads + runs a staged file (returns chunk value)", function()
        local sb = setup({ ["/staging/foo.lua"] = "return 42" })
        runner.assert_eq(42, sb.Mods.file.dofile("foo"), "dofile should return the chunk's value")
    end)

    runner.register("file: dofile appends .lua (DMF dmf_loader path)", function()
        local seen_path
        local files = {}
        -- Capture the path the module asks for so we can assert the join.
        local real_io = mock.make_io(files)
        local sb = mock.new_sandbox()
        sb.MAGOS_STAGING = "/staging"
        local capturing_io = {
            open = function(path, mode)
                seen_path = path
                return real_io.open(path, mode)
            end,
            close = real_io.close,
            lines = real_io.lines,
        }
        sb.Mods = {
            lua = { io = capturing_io, loadstring = sb.loadstring },
            file = {},
            _staging_base = "/staging",
        }
        mock.run_module("file", sb)
        -- Returns false because the file doesn't exist in the io map.
        runner.assert_eq(false, sb.Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader"))
        runner.assert_eq("/staging/dmf/scripts/mods/dmf/dmf_loader.lua", seen_path,
            "dofile must forward-slash-join staging + path + .lua")
    end)

    runner.register("file: backslash staging base is normalized to forward slash", function()
        local seen_path
        local sb = mock.new_sandbox()
        sb.MAGOS_STAGING = "C:\\users\\me\\staging"
        local real_io = mock.make_io({})
        sb.Mods = {
            lua = {
                io = {
                    open = function(p, m) seen_path = p; return real_io.open(p, m) end,
                    close = real_io.close, lines = real_io.lines,
                },
                loadstring = sb.loadstring,
            },
            file = {},
            _staging_base = "C:\\users\\me\\staging",
        }
        mock.run_module("file", sb)
        sb.Mods.file.dofile("mod_x/init")
        runner.assert_eq("C:/users/me/staging/mod_x/init.lua", seen_path,
            "backslashes in staging base must be normalized to forward slashes")
    end)

    runner.register("file: exec_with_return(local_path, name, ext) returns chunk value", function()
        local sb = setup({ ["/staging/dmf/scripts/dmf/dmf_mod.lua"] = "return { value = 'dmf-mm' }" })
        local r = sb.Mods.file.exec_with_return("dmf/scripts/dmf", "dmf_mod", "lua")
        runner.assert_type("table", r)
        runner.assert_eq("dmf-mm", r.value)
    end)

    runner.register("file: exec_with_return uses .lua default when ext is nil", function()
        local sb = setup({ ["/staging/mymod/mymod.lua"] = "return 7" })
        runner.assert_eq(7, sb.Mods.file.exec_with_return("mymod", "mymod"))
    end)

    runner.register("file: read_content_to_table skips blanks and comment lines", function()
        local content = table.concat({
            "alpha",
            "",
            "   ",
            "-- a comment",
            "bravo",
            "   -- indented comment",
            "charlie",
        }, "\n")
        local sb = setup({ ["/staging/list.txt"] = content })
        local t = sb.Mods.file.read_content_to_table("list", "txt")
        runner.assert_eq({ "alpha", "bravo", "charlie" }, t)
    end)

    runner.register("file: read_content returns the raw content", function()
        local sb = setup({ ["/staging/data.cfg"] = "key=value\n" })
        runner.assert_eq("key=value\n", sb.Mods.file.read_content("data", "cfg"))
    end)

    runner.register("file: exists returns true/false", function()
        local sb = setup({ ["/staging/there.lua"] = "return 0" })
        runner.assert_truthy(sb.Mods.file.exists("/staging/there.lua"))
        runner.assert_eq(false, sb.Mods.file.exists("/staging/missing.lua"))
    end)

    runner.register("file: dofile on a missing file returns false (safe_call contract)", function()
        local sb = setup({})
        runner.assert_eq(false, sb.Mods.file.dofile("nope"))
    end)

    runner.register("file: exec with a runtime error returns false under safe_call", function()
        local sb = setup({ ["/staging/broken.lua"] = "error('boom')" })
        -- exec is the safe_call + boolean variant; an error inside the chunk is
        -- caught (pcall) and surfaces as false rather than raising.
        runner.assert_eq(false, sb.Mods.file.exec("", "broken"))
    end)
end
