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
        sb.MAGOS_MOD_PATH = "/staging"
        local io = mock.make_io(files)
        sb.Mods = {
            lua = { io = io, loadstring = sb.loadstring },
            file = {},
            _staging_base = sb.MAGOS_MOD_PATH,
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
        sb.MAGOS_MOD_PATH = "/staging"
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
        sb.MAGOS_MOD_PATH = "C:\\users\\me\\staging"
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

    -- #9: a trailing separator on the staging base must not produce a doubled
    -- separator in the joined path.
    runner.register("file: trailing slash on staging base is stripped (no doubled separator)", function()
        local seen_path
        local real_io = mock.make_io({})
        local sb = mock.new_sandbox()
        sb.MAGOS_MOD_PATH = "/staging/"
        sb.Mods = {
            lua = {
                io = {
                    open = function(p, m) seen_path = p; return real_io.open(p, m) end,
                    close = real_io.close, lines = real_io.lines,
                },
                loadstring = sb.loadstring,
            },
            file = {},
            _staging_base = "/staging/",
        }
        mock.run_module("file", sb)
        sb.Mods.file.dofile("mod_x/init")
        runner.assert_eq("/staging/mod_x/init.lua", seen_path,
            "trailing '/' on base must not form '/staging//mod_x/init.lua'")
    end)

    runner.register("file: trailing backslash on staging base is stripped", function()
        local seen_path
        local real_io = mock.make_io({})
        local sb = mock.new_sandbox()
        sb.MAGOS_MOD_PATH = "C:\\staging\\"
        sb.Mods = {
            lua = {
                io = {
                    open = function(p, m) seen_path = p; return real_io.open(p, m) end,
                    close = real_io.close, lines = real_io.lines,
                },
                loadstring = sb.loadstring,
            },
            file = {},
            _staging_base = "C:\\staging\\",
        }
        mock.run_module("file", sb)
        sb.Mods.file.dofile("mod/init")
        runner.assert_eq("C:/staging/mod/init.lua", seen_path,
            "trailing '\\' on base must be stripped then normalized to '/'")
    end)

    -- #2: staging-confinement — path traversal must be rejected.
    runner.register("file: dofile rejects path traversal (../) and returns false", function()
        -- Stage a would-be escape target so the only thing that can return
        -- non-false is a real traversal. The check must refuse before io.open.
        local sb = setup({
            ["etc/passwd"] = "SECRET",
            ["/staging/../../../etc/passwd"] = "SECRET",
            ["/staging/etc/passwd"] = "SECRET",
        })
        runner.assert_eq(false, sb.Mods.file.dofile("../../../etc/passwd"),
            "traversal in dofile path must be confined to staging — no escape")
    end)

    runner.register("file: exec_with_return rejects traversal in local_path or file_name", function()
        local sb = setup({})
        runner.assert_eq(false, sb.Mods.file.exec_with_return("..", "win.ini", "lua"),
            "traversal in local_path must be rejected")
        runner.assert_eq(false, sb.Mods.file.exec_with_return("mods", "../../win", "ini"),
            "traversal in file_name must be rejected")
    end)

    -- The extension is appended as "." .. file_extension, so a traversal
    -- extension (e.g. "../../etc/passwd") would otherwise yield a path with
    -- resolvable ".." segments and bypass the local_path/file_name confinement.
    -- Covers all three exec variants that take an explicit extension.
    runner.register("file: exec rejects traversal in file_extension (append-bypass hole)", function()
        local sb = setup({})
        runner.assert_eq(false, sb.Mods.file.exec("x", "y", "../../evil"),
            "exec must reject traversal in file_extension (not append it)")
        runner.assert_eq(false, sb.Mods.file.exec_with_return("x", "y", "../../etc/passwd"),
            "exec_with_return must reject traversal in file_extension")
    end)

    runner.register("file: legitimate nested path still resolves after traversal check", function()
        -- The DMF bootstrap caller path must NOT regress.
        local sb = setup({ ["/staging/dmf/scripts/mods/dmf/dmf_loader.lua"] = "return 42" })
        runner.assert_eq(42, sb.Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader"),
            "the real DMF loader path (no ..) must still resolve")
    end)

    -- #5: the unsafe variants propagate loadstring/runtime errors (they're the
    -- un-pcall'd counterparts to exec / exec_with_return).
    runner.register("file: exec_unsafe propagates a syntax error (no pcall swallow)", function()
        local sb = setup({ ["/staging/broken.lua"] = "this is not valid lua (" })
        local ok, err = pcall(sb.Mods.file.exec_unsafe, "", "broken")
        runner.assert_eq(false, ok, "exec_unsafe must propagate the loadstring error, not swallow it")
        runner.assert_not_nil(err, "the error must be surfaced to the caller")
    end)

    runner.register("file: exec_unsafe_with_return returns the chunk value", function()
        local sb = setup({ ["/staging/val.lua"] = "return 'unsafe-value'" })
        runner.assert_eq("unsafe-value", sb.Mods.file.exec_unsafe_with_return("", "val"),
            "exec_unsafe_with_return must return the chunk's return value")
    end)
end
