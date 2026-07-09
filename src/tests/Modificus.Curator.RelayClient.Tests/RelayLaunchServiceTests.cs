using Modificus.Curator.Steam;

namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Launch-path tests for <see cref="RelayLaunchService"/>. All via the fakes
/// in <see cref="RelayFixture"/>: no real process is spawned and no game is
/// required. The concrete Windows/Linux <see cref="IPlatformLaunchStrategy"/>
/// (driven by the fixture's fake <see cref="IProcessLauncher"/>) is injected so
/// both code paths are exercised on any CI OS.
/// </summary>
public sealed class RelayLaunchServiceTests
{
    // ---- Windows ------------------------------------------------------------

    [Fact]
    public void Windows_assembles_correct_args_and_invokes_launcher_directly()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
        fx.Profiles.PrepareModRootResult = @"C:\curator\profiles\abc\mods";
        var profileId = Guid.NewGuid();
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(profileId);

        Assert.Equal(LaunchStatus.Launched, result.Status);

        // Invoked the launcher directly -- not proton, no "run" prefix, no env.
        Assert.Equal(fx.LauncherPath, fx.Launcher.FilePath);
        Assert.Null(fx.Launcher.Environment);
        Assert.DoesNotContain("run", fx.Launcher.Arguments!);

        Assert.Equal(
            new[] { "--game-binary", FakeDiscovery.WindowsGameBinary,
                    "--mod-path",    @"C:\curator\profiles\abc\mods",
                    "--log-file",    fx.Config.Logging.LogFile },
            fx.Launcher.Arguments);

        // --log-level is intentionally NOT emitted: the shell's level vocabulary
        // (error/warn/info/debug/trace) differs from Serilog's, so the launcher's
        // info default is used (the two logs are decoupled).
        Assert.DoesNotContain("--log-level", fx.Launcher.Arguments!);
    }

    [Fact]
    public void Windows_paths_are_not_z_translated()
    {
        // Guard: every path-valued flag must pass through unchanged on Windows
        // (no Z:\ prefix) -- translation is a Linux-only concern.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
        const string LogFile = @"C:\curator\logs\curator.log";
        fx.Config.Logging.LogFile = LogFile;
        var svc = fx.BuildWindowsService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        var game = args[IndexOf(args, "--game-binary") + 1];
        var log = args[IndexOf(args, "--log-file") + 1];
        Assert.Equal(FakeDiscovery.WindowsGameBinary, game);
        Assert.Equal(LogFile, log);
        Assert.DoesNotContain("Z:", game);
        Assert.DoesNotContain("Z:", log);
    }

    [Fact]
    public void Windows_launch_returns_launched_when_process_starts()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteWindows;
        fx.Launcher.Returns = true;
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
        Assert.Null(result.Message);
    }

    // ---- Linux --------------------------------------------------------------

    [Fact]
    public void Linux_translates_mod_path_and_game_binary_to_wine_paths()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.Profiles.PrepareModRootResult = "/home/u/.local/share/Modificus Curator/profiles/abc/mods";
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        // The launcher's own flags start after "run" + launcherPath.
        var launcherFlags = args.Skip(2).ToList();

        var game = launcherFlags[IndexOf(launcherFlags, "--game-binary") + 1];
        var mod = launcherFlags[IndexOf(launcherFlags, "--mod-path") + 1];

        Assert.Equal(@"Z:\home\u\.local\share\Modificus Curator\profiles\abc\mods", mod);
        Assert.Equal(
            @"Z:\home\u\.steam\steam\steamapps\common\Warhammer 40,000 DARKTIDE\binaries\Darktide.exe",
            game);
    }

    [Fact]
    public void Linux_translates_log_file_to_wine_path()
    {
        // The launcher runs under Wine and opens --log-file itself, so it must
        // be Z:\-translated on Linux (else the Relay shell log can't be written
        // where Curator expects).
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        const string LogFile = "/home/u/.local/share/Modificus Curator/logs/curator.log";
        fx.Config.Logging.LogFile = LogFile;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var args = fx.Launcher.Arguments!;
        var log = args[IndexOf(args, "--log-file") + 1];
        Assert.Equal(@"Z:\home\u\.local\share\Modificus Curator\logs\curator.log", log);
    }

    [Fact]
    public void Linux_sets_both_steam_compat_env_vars_from_discovery()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        var env = fx.Launcher.Environment;
        Assert.NotNull(env);
        Assert.Equal(FakeDiscovery.LinuxCompatdata, env!["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(FakeDiscovery.LinuxSteam, env!["STEAM_COMPAT_CLIENT_INSTALL_PATH"]);
    }

    [Fact]
    public void Linux_invokes_proton_run_with_launcher_not_launcher_alone()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        var svc = fx.BuildLinuxService();

        svc.Launch(Guid.NewGuid());

        // The launched command is <proton>, and its argv is [run, launcher.exe, ...flags].
        Assert.Equal(FakeDiscovery.LinuxProton, fx.Launcher.FilePath);
        var args = fx.Launcher.Arguments!;
        Assert.Equal("run", args[0]);
        Assert.Equal(fx.LauncherPath, args[1]);       // native Linux path -- Proton resolves it
        Assert.True(args.Count > 2, "expected launcher flags after the launcher path");
        // --log-level is not emitted (shell level vocabulary != Serilog's).
        Assert.DoesNotContain("--log-level", args);
    }

    [Fact]
    public void Linux_launch_returns_launched_when_process_starts()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.Launcher.Returns = true;
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Launched, result.Status);
    }

    // ---- DiscoveryIncomplete ------------------------------------------------

    [Fact]
    public void DiscoveryIncomplete_linux_partial_returns_missing_field_names()
    {
        // Steam + Darktide found, but compatdata + Proton missing on Linux.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux with
        {
            CompatdataPath = null,
            ProtonBinaryPath = null,
            ProtonVersion = null,
            Status = DiscoveryStatus.Partial,
        };
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.DiscoveryIncomplete, result.Status);
        Assert.Equal(
            new[] { nameof(DiscoveryResult.CompatdataPath), nameof(DiscoveryResult.ProtonBinaryPath) },
            result.MissingDiscoveryFields);

        // Short-circuit: PrepareModRoot must NOT run (we can't launch, so don't write mods.lst).
        Assert.Equal(0, fx.Profiles.PrepareModRootCalls);
        Assert.Equal(0, fx.Launcher.Calls);
    }

    [Fact]
    public void DiscoveryIncomplete_windows_partial_returns_missing_game_binary()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteWindows with
        {
            DarktideGameBinaryPath = null,
            Status = DiscoveryStatus.Partial,
        };
        var svc = fx.BuildWindowsService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.DiscoveryIncomplete, result.Status);
        // Compatdata/Proton are NOT required on Windows -- only the game binary is missing.
        Assert.Equal(
            new[] { nameof(DiscoveryResult.DarktideGameBinaryPath) },
            result.MissingDiscoveryFields);
    }

    [Fact]
    public void DiscoveryIncomplete_failed_returns_all_os_required_fields()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = new DiscoveryResult(
            SteamInstallPath: null,
            DarktideGameBinaryPath: null,
            CompatdataPath: null,
            ProtonBinaryPath: null,
            ProtonVersion: null,
            Status: DiscoveryStatus.Failed,
            Warnings: Array.Empty<string>());
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.DiscoveryIncomplete, result.Status);
        Assert.Equal(
            new[]
            {
                nameof(DiscoveryResult.SteamInstallPath),
                nameof(DiscoveryResult.DarktideGameBinaryPath),
                nameof(DiscoveryResult.CompatdataPath),
                nameof(DiscoveryResult.ProtonBinaryPath),
            },
            result.MissingDiscoveryFields);
    }

    // ---- Profile integration ------------------------------------------------

    [Fact]
    public void Launch_calls_PrepareModRoot_with_profile_id_before_invoking()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        const string PreparedRoot = "/tmp/prepared-mod-root";
        fx.Profiles.PrepareModRootResult = PreparedRoot;
        var profileId = Guid.NewGuid();
        var svc = fx.BuildLinuxService();

        svc.Launch(profileId);

        Assert.Equal(1, fx.Profiles.PrepareModRootCalls);
        Assert.Equal(profileId, fx.Profiles.LastPrepareModRootId);

        // The returned path is the --mod-path (Z:\-translated on Linux).
        var args = fx.Launcher.Arguments!;
        var modIndex = IndexOf(args, "--mod-path");
        var modPath = args[modIndex + 1];
        Assert.Equal(WinePath.ToWine(PreparedRoot), modPath);
    }

    // ---- Error ---------------------------------------------------------------

    [Fact]
    public void Launch_returns_StagingFailed_when_PrepareModRoot_throws()
    {
        // A staging-link creation failure propagates the raised built-in
        // exception from PrepareModRoot (the junction path throws Win32Exception
        // on Windows; the symlink path throws IOException natively; here the fake
        // throws IOException). Launch maps it to StagingFailed, carrying the
        // exception's body on Message (surfaced after the localized framing in
        // the UI), with an empty missing-fields list.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.Profiles.PrepareModRootThrows = true;
        var profileId = Guid.NewGuid();
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(profileId);

        Assert.Equal(LaunchStatus.StagingFailed, result.Status);
        Assert.Equal("simulated staging-link failure", result.Message);
        Assert.Empty(result.MissingDiscoveryFields);
        Assert.Equal(1, fx.Profiles.PrepareModRootCalls);
        Assert.Equal(0, fx.Launcher.Calls); // never spawned
    }

    [Fact]
    public void Error_unknown_profile_returns_error_not_thrown()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux; // discovery OK, but profile unknown
        fx.Profiles.UnknownProfile = true;
        var profileId = Guid.NewGuid();
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(profileId);

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Contains(profileId.ToString(), result.Message);
        Assert.Equal(0, fx.Launcher.Calls);
    }

    [Fact]
    public void Error_missing_runtime_launcher_returns_error()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.DeleteLauncher(); // Relay not deployed
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Contains("modificus_relay.exe", result.Message);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fx.Launcher.Calls);
    }

    [Fact]
    public void Error_process_start_failure_returns_error()
    {
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.Launcher.Returns = false; // process.Start failed (file missing, perms, etc.)
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.NotNull(result.Message);
        Assert.Equal(1, fx.Launcher.Calls); // it tried, but Start returned false
    }

    [Fact]
    public void Error_result_carries_empty_missing_fields()
    {
        // Error (not DiscoveryIncomplete) must always carry an empty missing-fields list.
        using var fx = new RelayFixture();
        fx.Steam.Result = FakeDiscovery.CompleteLinux;
        fx.DeleteLauncher();
        var svc = fx.BuildLinuxService();

        var result = svc.Launch(Guid.NewGuid());

        Assert.Equal(LaunchStatus.Error, result.Status);
        Assert.Empty(result.MissingDiscoveryFields);
    }

    /// <summary>
    /// Ordinal index-of for <see cref="IReadOnlyList{T}"/> (no IndexOf on that
    /// interface; Array.IndexOf needs an Array). Used to locate flag positions.
    /// </summary>
    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }
}
