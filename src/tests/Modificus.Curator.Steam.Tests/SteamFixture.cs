using System.Text;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Per-test fixture: scaffolds a synthetic Steam layout in a fresh temp dir +
/// builds a real <see cref="ISteamService"/> through <c>AddSteam()</c> with the
/// discovery options + platform seams pointed at the fixture. Disposes the temp
/// tree + the service provider on teardown so tests are isolated regardless of
/// outcome.
/// </summary>
/// <remarks>
/// Resolving via DI (rather than constructing the internal implementation
/// directly) keeps tests black-box against <see cref="ISteamService"/> and
/// proves the real registration path, the same approach the Profiles fixture
/// uses. <see cref="SteamDiscoveryOptions"/> / <see cref="ISteamRegistryReader"/>
/// / <see cref="IProcessLookup"/> / <see cref="IConfigLoader"/> are pre-registered
/// so <c>AddSteam()</c>'s <c>TryAdd</c> defaults are skipped in favor of the
/// fixture's fakes. <see cref="Config"/> exposes the live <see cref="CuratorConfig"/>
/// so overlay tests can set <see cref="DiscoveryConfig"/> user overrides.
/// </remarks>
internal sealed class SteamFixture : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SteamDiscoveryOptions _options;

    public string TempRoot { get; }
    public string SteamRoot { get; }       // the "native" Linux / Windows fixture Steam install
    public string FlatpakRoot { get; }     // the Flatpak candidate
    public string CompatToolsDir { get; }  // compatibilitytools.d candidate
    public FakeRegistryReader Registry { get; } = new();
    public FakeProcessLookup Processes { get; } = new();
    public FakeConfigLoader ConfigLoader { get; } = new();
    public CuratorConfig Config => ConfigLoader.Config;
    public ISteamService Service { get; }

    public SteamFixture(
        DiscoveryPlatform platform = DiscoveryPlatform.Linux,
        Action<SteamDiscoveryOptions>? configure = null)
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "curator-steam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempRoot);
        SteamRoot = Path.Combine(TempRoot, "Steam");
        FlatpakRoot = Path.Combine(TempRoot, "flatpak-Steam");
        CompatToolsDir = Path.Combine(TempRoot, "compatibilitytools.d");

        _options = new SteamDiscoveryOptions
        {
            Platform = platform,
            LinuxDefaultSteamRoot = SteamRoot,
            LinuxFlatpakSteamRoot = FlatpakRoot,
            LinuxCompatibilityToolsDir = CompatToolsDir,
            // Reuse the fixture root for Windows tests (registry supplies it via
            // a fake rather than a real second path).
            WindowsDefaultSteamRoot = SteamRoot,
        };
        configure?.Invoke(_options);

        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddSingleton<ISteamRegistryReader>(Registry);
        services.AddSingleton<IProcessLookup>(Processes);
        services.AddSingleton<IConfigLoader>(ConfigLoader);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning)); // quiet by default
        services.AddSteam();
        _provider = services.BuildServiceProvider();

        Service = _provider.GetRequiredService<ISteamService>();
    }

    // ---- layout helpers (fluent; return this) -----------------------------

    /// <summary>
    /// Writes a <c>libraryfolders.vdf</c> under the native Steam root listing the
    /// given library roots. With no args, lists the Steam root itself (a valid
    /// single-library layout).
    /// </summary>
    public SteamFixture WithLibraryFoldersAtSteamRoot(params string[] libraryPaths)
    {
        Directory.CreateDirectory(Path.Combine(SteamRoot, "steamapps"));
        var libs = libraryPaths.Length == 0 ? new[] { SteamRoot } : libraryPaths;
        File.WriteAllText(
            Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf"),
            BuildLibraryFoldersVdf(libs));
        return this;
    }

    /// <summary>Same as <see cref="WithLibraryFoldersAtSteamRoot"/> but writes
    /// under the Flatpak root so the Flatpak candidate is the one that resolves.</summary>
    public SteamFixture WithLibraryFoldersAtFlatpakRoot(params string[] libraryPaths)
    {
        Directory.CreateDirectory(Path.Combine(FlatpakRoot, "steamapps"));
        var libs = libraryPaths.Length == 0 ? new[] { FlatpakRoot } : libraryPaths;
        File.WriteAllText(
            Path.Combine(FlatpakRoot, "steamapps", "libraryfolders.vdf"),
            BuildLibraryFoldersVdf(libs));
        return this;
    }

    /// <summary>Creates an empty Darktide.exe under <c>&lt;libraryRoot&gt;/steamapps/common/&lt;DarktideCommonDir&gt;/binaries/</c>.</summary>
    public SteamFixture WithDarktide(string libraryRoot)
    {
        var exe = Path.Combine(
            libraryRoot, "steamapps", "common",
            _options.DarktideCommonDir, "binaries", _options.GameBinaryName);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, string.Empty);
        return this;
    }

    /// <summary>Creates the compatdata dir for the configured app id under the given Steam root.</summary>
    public SteamFixture WithCompatdata(string steamRoot)
    {
        Directory.CreateDirectory(Path.Combine(
            steamRoot, "steamapps", "compatdata", _options.DarktideAppId.ToString()));
        return this;
    }

    /// <summary>Creates a <c>proton</c> file under <c>&lt;steamRoot&gt;/steamapps/common/&lt;dirName&gt;/</c>.</summary>
    public SteamFixture WithProtonInCommon(string steamRoot, string dirName)
    {
        var proton = Path.Combine(steamRoot, "steamapps", "common", dirName, "proton");
        Directory.CreateDirectory(Path.GetDirectoryName(proton)!);
        File.WriteAllText(proton, string.Empty);
        return this;
    }

    /// <summary>Creates a <c>proton</c> file under <c>compatibilitytools.d/&lt;dirName&gt;/</c>.</summary>
    public SteamFixture WithProtonInCompatTools(string dirName)
    {
        var proton = Path.Combine(CompatToolsDir, dirName, "proton");
        Directory.CreateDirectory(Path.GetDirectoryName(proton)!);
        File.WriteAllText(proton, string.Empty);
        return this;
    }

    // ---- expected-path helpers (assertions) -------------------------------

    public string ExpectedDarktidePath(string libraryRoot) => Path.Combine(
        libraryRoot, "steamapps", "common",
        _options.DarktideCommonDir, "binaries", _options.GameBinaryName);

    public string ExpectedCompatdataPath(string steamRoot) => Path.Combine(
        steamRoot, "steamapps", "compatdata", _options.DarktideAppId.ToString());

    public string ExpectedProtonPath(string parent, string dirName) =>
        Path.Combine(parent, "steamapps", "common", dirName, "proton");

    public string ExpectedCompatToolsProtonPath(string dirName) =>
        Path.Combine(CompatToolsDir, dirName, "proton");

    // ---- static VDF builder ------------------------------------------------

    /// <summary>Builds a realistic minimal <c>libraryfolders.vdf</c> body listing the given library roots.</summary>
    public static string BuildLibraryFoldersVdf(params string[] libraryPaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"libraryfolders\"");
        sb.AppendLine("{");
        for (var i = 0; i < libraryPaths.Length; i++)
        {
            sb.AppendLine($"\t\"{i}\"");
            sb.AppendLine("\t{");
            sb.AppendLine($"\t\t\"path\"\t\t\"{EscapeVdfValue(libraryPaths[i])}\"");
            sb.AppendLine("\t\t\"label\"\t\t\"\"");
            sb.AppendLine("\t\t\"contentid\"\t\t\"0\"");
            sb.AppendLine("\t\t\"apps\"");
            sb.AppendLine("\t\t{");
            sb.AppendLine("\t\t}");
            sb.AppendLine("\t}");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EscapeVdfValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(TempRoot))
        {
            // Best-effort; temp dirs under the OS temp are harmless if left.
            try { Directory.Delete(TempRoot, recursive: true); }
            catch (IOException) { /* ignored */ }
        }
    }
}

/// <summary>Test double for <see cref="ISteamRegistryReader"/>; returns <see cref="SteamPath"/>.</summary>
internal sealed class FakeRegistryReader : ISteamRegistryReader
{
    public string? SteamPath { get; set; }
    public string? GetSteamPath() => SteamPath;
}

/// <summary>Test double for <see cref="IProcessLookup"/>; reports the names in <see cref="Running"/> as running.</summary>
internal sealed class FakeProcessLookup : IProcessLookup
{
    public HashSet<string> Running { get; } = new(StringComparer.Ordinal);
    public bool IsRunning(string processName) => Running.Contains(processName);
}

/// <summary>
/// Minimal <see cref="IConfigLoader"/> double for the steam tests: serves a
/// mutable <see cref="CuratorConfig"/> (so overlay tests can set
/// <see cref="DiscoveryConfig"/> user overrides before calling
/// <see cref="ISteamService.Discover"/>). <see cref="Save"/> mirrors the real
/// loader's round-trip: it promotes the written config to the live snapshot, so
/// the next <see cref="Load"/> returns what was saved (and a read-modify-save
/// in <see cref="SteamService.Discover"/> sees the prior Save's effect).
/// </summary>
internal sealed class FakeConfigLoader : IConfigLoader
{
    public CuratorConfig Config { get; set; } = CuratorConfig.CreateDefault();
    public int SaveCalls { get; private set; }
    public CuratorConfig? LastSaved { get; private set; }

    public CuratorConfig Load() => Config;

    public void Save(CuratorConfig config)
    {
        SaveCalls++;
        LastSaved = config;
        // Promote to the live Config so a subsequent Load returns the saved
        // state (mirrors the real loader's round-trip through the disk file).
        Config = config;
    }
}
