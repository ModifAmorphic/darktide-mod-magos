using Magos.Modificus.Profiles;
using Magos.Modificus.Mods;
using Magos.Modificus.Steam;

namespace Magos.Modificus.EnginseerClient.Tests;

/// <summary>
/// Hand-rolled test double for <see cref="IProfileService"/>. Only
/// <see cref="PrepareModRoot"/> is exercised by the launch path; the rest of the
/// surface throws <see cref="NotSupportedException"/> to catch accidental misuse.
/// </summary>
internal sealed class FakeProfileService : IProfileService
{
    /// <summary>The path returned by <see cref="PrepareModRoot"/> (the --mod-path).</summary>
    public string PrepareModRootResult { get; set; } = "/home/u/.local/share/Magos Modificus/profiles/<id>/mods";

    /// <summary>When set, <see cref="PrepareModRoot"/> throws KeyNotFoundException (unknown profile).</summary>
    public bool UnknownProfile { get; set; }

    public Guid LastPrepareModRootId { get; private set; }
    public int PrepareModRootCalls { get; private set; }

    /// <inheritdoc />
    public string PrepareModRoot(Guid id)
    {
        PrepareModRootCalls++;
        LastPrepareModRootId = id;
        if (UnknownProfile)
        {
            throw new KeyNotFoundException($"No profile exists with id '{id}'.");
        }
        return PrepareModRootResult;
    }

    // The remainder of the surface is unused by the launch path.
    public IReadOnlyList<ProfileSummary> ListProfiles() => throw new NotSupportedException();
    public Profile GetProfile(Guid id) => throw new NotSupportedException();
    public Profile CreateProfile(string name) => throw new NotSupportedException();
    public void RenameProfile(Guid id, string newName) => throw new NotSupportedException();
    public void DeleteProfile(Guid id) => throw new NotSupportedException();
    public IReadOnlyList<ModListEntry> GetModList(Guid id) => throw new NotSupportedException();
    public void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder) => throw new NotSupportedException();
    public void SetModEnabled(Guid id, Guid containerId, bool enabled) => throw new NotSupportedException();
    public void AddMod(Guid id, Guid containerId, ModVersionPolicy policy) => throw new NotSupportedException();
    public void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy) => throw new NotSupportedException();
    public void RemoveMod(Guid id, Guid containerId) => throw new NotSupportedException();
}

/// <summary>Hand-rolled test double for <see cref="ISteamService"/>.</summary>
internal sealed class FakeSteamService : ISteamService
{
    public DiscoveryResult Result { get; set; } = FakeDiscovery.CompleteLinux;
    public int DiscoverCalls { get; private set; }

    /// <inheritdoc />
    public DiscoveryResult Discover()
    {
        DiscoverCalls++;
        return Result;
    }

    /// <inheritdoc />
    public bool IsGameRunning() => false;
}

/// <summary>
/// Hand-rolled test double for <see cref="IProcessLauncher"/>. Records the last
/// invocation's filePath / arguments / environment and returns a configurable
/// boolean (default <c>true</c> = started).
/// </summary>
internal sealed class FakeProcessLauncher : IProcessLauncher
{
    /// <summary>The value returned by <see cref="Start"/> (default true = started).</summary>
    public bool Returns { get; set; } = true;

    public string? FilePath { get; private set; }
    public IReadOnlyList<string>? Arguments { get; private set; }
    public IReadOnlyDictionary<string, string>? Environment { get; private set; }
    public int Calls { get; private set; }

    /// <inheritdoc />
    public bool Start(
        string filePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        Calls++;
        FilePath = filePath;
        Arguments = arguments;
        Environment = environmentVariables;
        return Returns;
    }
}

/// <summary>
/// Realistic complete <see cref="DiscoveryResult"/> fixtures for each platform —
/// the values a real Steam discovery would yield on a healthy install. Tests
/// selectively null fields to exercise the DiscoveryIncomplete path.
/// </summary>
internal static class FakeDiscovery
{
    public const string LinuxSteam = "/home/u/.steam/steam";
    public const string LinuxGameBinary =
        "/home/u/.steam/steam/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe";
    public const string LinuxCompatdata =
        "/home/u/.steam/steam/steamapps/compatdata/1361210";
    public const string LinuxProton =
        "/home/u/.steam/steam/steamapps/common/Proton - Experimental/proton";
    public const string LinuxProtonVersion = "Proton - Experimental";

    public const string WindowsSteam = @"C:\Program Files (x86)\Steam";
    public const string WindowsGameBinary =
        @"C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40,000 DARKTIDE\binaries\Darktide.exe";

    public static DiscoveryResult CompleteLinux { get; } = new(
        SteamInstallPath: LinuxSteam,
        DarktideGameBinaryPath: LinuxGameBinary,
        CompatdataPath: LinuxCompatdata,
        ProtonBinaryPath: LinuxProton,
        ProtonVersion: LinuxProtonVersion,
        Status: DiscoveryStatus.Complete,
        Warnings: Array.Empty<string>());

    public static DiscoveryResult CompleteWindows { get; } = new(
        SteamInstallPath: WindowsSteam,
        DarktideGameBinaryPath: WindowsGameBinary,
        CompatdataPath: null,
        ProtonBinaryPath: null,
        ProtonVersion: null,
        Status: DiscoveryStatus.Complete,
        Warnings: Array.Empty<string>());
}
