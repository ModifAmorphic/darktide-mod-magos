using Modificus.Curator.Profiles;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Game-argument emission via Relay's bare-<c>--</c> contract, exercised through
/// the pure <see cref="LinuxLaunchStrategy.BuildLauncherArgs"/> /
/// <see cref="WindowsLaunchStrategy.BuildLauncherArgs"/> seams (no process is
/// spawned). The two strategies share <see cref="LinuxLaunchStrategy.AppendGameArguments"/>,
/// so the contract is identical on both; the tests pin the exact argv layout:
/// empty game args emit no <c>--</c>, non-empty emit one <c>--</c> then each arg
/// as its own element in order, and values containing spaces or quotes stay a
/// single element (Relay owns the final <c>CreateProcess</c> quoting).
/// </summary>
public sealed class GameArgumentsTests
{
    private const string GameBinary = "/opt/Darktide.exe";
    private const string ModPath = "/curator/profile/mods";
    private const string LogFile = "/curator/curator.log";

    // ---- empty game args: no -- (legacy launch) ---------------------------

    [Fact]
    public void Linux_empty_game_args_emit_no_separator()
    {
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, Array.Empty<string>());

        // The six launcher flags only; no -- anywhere.
        Assert.DoesNotContain("--", args);
        Assert.Equal(6, args.Count);
    }

    [Fact]
    public void Windows_empty_game_args_emit_no_separator()
    {
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, Array.Empty<string>());

        Assert.DoesNotContain("--", args);
        Assert.Equal(6, args.Count);
    }

    [Fact]
    public void Null_game_args_emit_no_separator()
    {
        // Defense: a null list (LaunchSettings stores non-null, but the seam is
        // robust) is treated as empty.
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, null!);

        Assert.DoesNotContain("--", args);
    }

    // ---- non-empty: one -- then each arg as its own element, in order ------

    [Fact]
    public void Linux_multiple_args_emit_one_separator_then_each_arg_in_order()
    {
        var gameArgs = new[] { "-windowed", "-borderless", "-width" };

        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, gameArgs);

        // [6 launcher flags] then "--" then the three args, in order.
        Assert.Equal("--", args[6]);
        Assert.Equal(new[] { "-windowed", "-borderless", "-width" }, args.Skip(7).ToArray());
        // Exactly one -- separator (not one per arg).
        Assert.Single(args, a => a == "--");
    }

    [Fact]
    public void Windows_multiple_args_emit_one_separator_then_each_arg_in_order()
    {
        var gameArgs = new[] { "-one", "-two", "-three" };

        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, gameArgs);

        Assert.Equal("--", args[6]);
        Assert.Equal(gameArgs, args.Skip(7).ToArray());
        Assert.Single(args, a => a == "--");
    }

    [Fact]
    public void A_single_arg_emits_one_separator_then_the_arg()
    {
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new[] { "-solo" });

        Assert.Equal("--", args[6]);
        Assert.Equal("-solo", args[7]);
        Assert.Equal(8, args.Count);
    }

    // ---- duplicate game args preserved -------------------------------------

    [Fact]
    public void Duplicate_game_args_are_each_emitted_as_their_own_element()
    {
        // Each entry is a distinct argv value; duplicates are not collapsed.
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new[] { "-x", "-x", "-y" });

        Assert.Equal(new[] { "-x", "-x", "-y" }, args.Skip(7).ToArray());
    }

    // ---- values with spaces + quotes stay one element ----------------------

    [Fact]
    public void Values_with_spaces_stay_one_element()
    {
        // Relay owns the final CreateProcess quoting. Curator adds each arg
        // verbatim to ArgumentList; a value with spaces survives as a single
        // argv entry (no prequoting / joining on Curator's side).
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile,
            new[] { "an arg with spaces", "-plain" });

        Assert.Equal("an arg with spaces", args[7]);
        Assert.Equal("-plain", args[8]);
    }

    [Fact]
    public void Values_with_quotes_stay_one_element()
    {
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile,
            new[] { """a "quoted" arg""", "-plain" });

        Assert.Equal("""a "quoted" arg""", args[7]);
        Assert.Equal("-plain", args[8]);
    }

    [Fact]
    public void An_empty_string_arg_is_emitted_as_an_empty_element()
    {
        // An empty game arg is a distinct (empty) argv entry, not dropped; the
        // element count matches the profile list length.
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile,
            new[] { "-a", "", "-b" });

        Assert.Equal(new[] { "-a", "", "-b" }, args.Skip(7).ToArray());
        Assert.Equal(10, args.Count); // 6 flags + -- + 3 args
    }

    // ---- launcher flags precede -- -----------------------------------------

    [Fact]
    public void Launcher_flags_precede_the_separator_on_linux()
    {
        // The launcher's own flags (--game-binary, --mod-path, --log-file) come
        // before the -- separator; Relay flags precede --, game args follow.
        var args = LinuxLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new[] { "-g" });

        Assert.Equal("--game-binary", args[0]);
        Assert.Equal(WinePath.ToWine(GameBinary), args[1]);
        Assert.Equal("--mod-path", args[2]);
        Assert.Equal(WinePath.ToWine(ModPath), args[3]);
        Assert.Equal("--log-file", args[4]);
        Assert.Equal(WinePath.ToWine(LogFile), args[5]);
        Assert.Equal("--", args[6]);
        Assert.Equal("-g", args[7]);
    }

    [Fact]
    public void Launcher_flags_precede_the_separator_on_windows()
    {
        var args = WindowsLaunchStrategy.BuildLauncherArgs(GameBinary, ModPath, LogFile, new[] { "-g" });

        Assert.Equal("--game-binary", args[0]);
        Assert.Equal(GameBinary, args[1]); // no Z:\ translation on Windows
        Assert.Equal("--mod-path", args[2]);
        Assert.Equal(ModPath, args[3]);
        Assert.Equal("--log-file", args[4]);
        Assert.Equal(LogFile, args[5]);
        Assert.Equal("--", args[6]);
        Assert.Equal("-g", args[7]);
    }
}
