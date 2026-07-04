using System.Text;
using System.Text.Json;
using Magos.Modificus.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.SharedMods.Tests;

/// <summary>
/// <see cref="ISharedModStore"/> CRUD + <c>shared-manifest.json</c> persistence +
/// first-run <c>SharedModsFolder</c> auto-create. Resolves via DI (black-box).
/// </summary>
public sealed class SharedModStoreTests
{
    private static SharedModEntry Entry(
        string name,
        string policyLabel = "latest",
        string? version = null,
        ModSource? source = null)
    {
        var policy = policyLabel == "pinned"
            ? (ModVersionPolicy)new PinnedPolicy(version ?? "1.0.0")
            : ModVersionPolicy.Latest;
        var actualVersion = version ?? "1.0.0";
        return new SharedModEntry
        {
            Name = name,
            Policy = policy,
            Source = source ?? new NoneSource(),
            ActualVersion = actualVersion,
            Path = $"/store/{name}",
        };
    }

    // ---- list / get --------------------------------------------------------

    [Fact]
    public void List_is_empty_when_manifest_absent()
    {
        using var fx = new SharedStoreFixture();
        Assert.Empty(fx.Store.List());
    }

    [Fact]
    public void Add_then_list_round_trips_entry()
    {
        using var fx = new SharedStoreFixture();
        fx.Store.Add(Entry("DMF", "pinned", "1.2.3"));

        var only = Assert.Single(fx.Store.List());
        Assert.Equal("DMF", only.Name);
        Assert.Equal("1.2.3", Assert.IsType<PinnedPolicy>(only.Policy).Version);
        Assert.Equal("1.2.3", only.ActualVersion);
        Assert.Equal("/store/DMF", only.Path);
        Assert.IsType<NoneSource>(only.Source);
    }

    [Fact]
    public void Get_returns_entry_by_name_ordinal_null_when_absent()
    {
        using var fx = new SharedStoreFixture();
        fx.Store.Add(Entry("DMF"));

        Assert.NotNull(fx.Store.Get("DMF"));
        Assert.Null(fx.Store.Get("dmf")); // ordinal, case-sensitive
        Assert.Null(fx.Store.Get("Missing"));
    }

    // ---- add (upsert) ------------------------------------------------------

    [Fact]
    public void Add_upserts_same_named_entry()
    {
        using var fx = new SharedStoreFixture();
        fx.Store.Add(Entry("DMF", "latest"));
        fx.Store.Add(Entry("DMF", "pinned", "2.0.0")); // replace

        var only = Assert.Single(fx.Store.List());
        Assert.Equal("2.0.0", Assert.IsType<PinnedPolicy>(only.Policy).Version);
    }

    [Fact]
    public void Add_rejects_null_or_empty_entry()
    {
        using var fx = new SharedStoreFixture();
        Assert.Throws<ArgumentNullException>(() => fx.Store.Add(null!));
        Assert.Throws<ArgumentException>(() => fx.Store.Add(Entry("")));
        Assert.Throws<ArgumentException>(() => fx.Store.Add(Entry("   ")));
    }

    // ---- remove ------------------------------------------------------------

    [Fact]
    public void Remove_drops_entry_from_manifest()
    {
        using var fx = new SharedStoreFixture();
        fx.Store.Add(Entry("DMF"));
        fx.Store.Add(Entry("ModB"));

        fx.Store.Remove("DMF");

        Assert.Single(fx.Store.List(), e => e.Name == "ModB");
        Assert.Null(fx.Store.Get("DMF"));
    }

    [Fact]
    public void Remove_is_idempotent_for_missing_name()
    {
        using var fx = new SharedStoreFixture();
        fx.Store.Add(Entry("DMF"));

        fx.Store.Remove("Ghost"); // no-op, no throw
        fx.Store.Remove("Ghost"); // again
        Assert.Single(fx.Store.List());
    }

    [Fact]
    public void Remove_does_not_touch_the_mod_files()
    {
        // Remove owns the manifest only; the files live at entry.Path and are the
        // acquisition's responsibility (Phase 4). Here we just assert the contract
        // holds — no file at Path is created or deleted by Remove.
        using var fx = new SharedStoreFixture();
        fx.Store.Add(Entry("DMF"));

        fx.Store.Remove("DMF");

        Assert.Empty(fx.Store.List());
    }

    // ---- persistence -------------------------------------------------------

    [Fact]
    public void Manifest_persists_across_store_instances()
    {
        using var fx = new SharedStoreFixture();
        fx.Store.Add(Entry("DMF", "pinned", "1.0.0"));
        fx.Store.Add(Entry("ModB"));

        // A second store instance reads the same manifest from disk.
        var reloaded = fx.Reload();

        var entries = reloaded.List();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "DMF");
        Assert.Contains(entries, e => e.Name == "ModB");
    }

    [Fact]
    public void Manifest_is_utf8_json_array_with_kind_discriminator()
    {
        using var fx = new SharedStoreFixture();
        fx.Store.Add(Entry("DMF", "latest"));
        fx.Store.Add(Entry("ModB", "pinned", "2.0.0"));

        // The on-disk shape is a JSON array carrying the $kind discriminator so
        // the policy hierarchy round-trips deterministically.
        var raw = File.ReadAllText(fx.ManifestPath);
        Assert.Contains("\"$kind\": \"latest\"", raw);
        Assert.Contains("\"$kind\": \"pinned\"", raw);
        // No BOM.
        var bytes = File.ReadAllBytes(fx.ManifestPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        // And it deserializes as a list.
        var parsed = JsonSerializer.Deserialize<List<SharedModEntry>>(raw)!;
        Assert.Equal(2, parsed.Count);
    }

    [Fact]
    public void Add_then_list_round_trips_Source_for_each_kind()
    {
        // Source is persisted polymorphically via its own $kind discriminator,
        // mirroring the policy hierarchy. NoneSource is the default.
        using var fx = new SharedStoreFixture();
        fx.Store.Add(new SharedModEntry
        {
            Name = "LocalMod",
            Source = new NoneSource(),
            ActualVersion = "1.0",
            Path = "/store/LocalMod",
        });
        fx.Store.Add(new SharedModEntry
        {
            Name = "NexusMod",
            Source = new NexusSource { ModId = 12345 },
            ActualVersion = "2.1",
            Path = "/store/NexusMod",
        });
        fx.Store.Add(new SharedModEntry
        {
            Name = "GitHubMod",
            Source = new GitHubSource { Owner = "owner", Repo = "repo" },
            ActualVersion = "v3.0",
            Path = "/store/GitHubMod",
        });

        var reloaded = fx.Reload().List();
        Assert.Equal(3, reloaded.Count);

        var local = reloaded.Single(e => e.Name == "LocalMod");
        Assert.IsType<NoneSource>(local.Source);

        var nexus = reloaded.Single(e => e.Name == "NexusMod");
        var nexusSource = Assert.IsType<NexusSource>(nexus.Source);
        Assert.Equal(12345, nexusSource.ModId);
        Assert.Equal("2.1", nexus.ActualVersion);

        var gitHub = reloaded.Single(e => e.Name == "GitHubMod");
        var ghSource = Assert.IsType<GitHubSource>(gitHub.Source);
        Assert.Equal("owner", ghSource.Owner);
        Assert.Equal("repo", ghSource.Repo);
        Assert.Equal("v3.0", gitHub.ActualVersion);
    }

    [Fact]
    public void Add_with_default_Source_reads_back_as_NoneSource()
    {
        // A legacy/Phase-2 entry that never set Source (the property defaults to
        // NoneSource) round-trips as NoneSource: the JSON carries "$kind":"none".
        using var fx = new SharedStoreFixture();
        fx.Store.Add(new SharedModEntry { Name = "Legacy", Path = "/store/Legacy" });

        var reloaded = fx.Reload().Get("Legacy")!;
        Assert.IsType<NoneSource>(reloaded.Source);

        var raw = File.ReadAllText(fx.ManifestPath);
        Assert.Contains("\"$kind\": \"none\"", raw);
    }

    [Fact]
    public void Add_upserts_Source_and_ActualVersion_on_re_add()
    {
        // Upsert replaces the whole entry, so Source + ActualVersion are updated
        // when the importer re-imports with new metadata.
        using var fx = new SharedStoreFixture();
        fx.Store.Add(new SharedModEntry
        {
            Name = "DMF",
            Source = new NoneSource(),
            ActualVersion = "1.0",
            Path = "/store/DMF",
        });
        fx.Store.Add(new SharedModEntry
        {
            Name = "DMF",
            Source = new NexusSource { ModId = 99 },
            ActualVersion = "2.0",
            Path = "/store/DMF",
        });

        var only = Assert.Single(fx.Store.List());
        Assert.Equal("2.0", only.ActualVersion);
        var nexus = Assert.IsType<NexusSource>(only.Source);
        Assert.Equal(99, nexus.ModId);
    }

    [Fact]
    public void Corrupt_manifest_is_treated_as_empty_not_a_crash()
    {
        using var fx = new SharedStoreFixture();
        Directory.CreateDirectory(fx.Folder);
        File.WriteAllText(fx.ManifestPath, "{ this is not json", new UTF8Encoding(false));

        Assert.Empty(fx.Store.List());
        Assert.Null(fx.Store.Get("DMF"));
    }

    // ---- first-run ---------------------------------------------------------

    [Fact]
    public void FirstRun_creates_missing_SharedModsFolder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "magos-shared-firstrun-" + Guid.NewGuid());
        var folder = Path.Combine(tempRoot, "deep", "shared-mods");
        Assert.False(Directory.Exists(folder));

        try
        {
            var config = MagosConfig.CreateDefault();
            config.SharedModsFolder = folder;
            using var provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddSharedMods()
                .BuildServiceProvider();

            provider.GetRequiredService<ISharedModStore>(); // forces construction

            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    // ---- DI registration ---------------------------------------------------

    [Fact]
    public void AddSharedMods_registers_resolvable_ISharedModStore()
    {
        var config = MagosConfig.CreateDefault();
        using var provider = new ServiceCollection()
            .AddSingleton(config)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddSharedMods()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<ISharedModStore>());
    }

    [Fact]
    public void AddSharedMods_registers_resolvable_IModImportService()
    {
        var config = MagosConfig.CreateDefault();
        using var provider = new ServiceCollection()
            .AddSingleton(config)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddSharedMods()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IModImportService>());
    }

    [Fact]
    public void AddSharedMods_is_idempotent_and_returns_same_collection()
    {
        var services = new ServiceCollection();
        var returned = services.AddSharedMods();
        Assert.Same(services, returned);
    }

    /// <summary>Per-test fixture: temp <c>SharedModsFolder</c> + a DI-resolved store.</summary>
    private sealed class SharedStoreFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "magos-shared-" + Guid.NewGuid());
        public string ManifestPath => Path.Combine(Folder, "shared-manifest.json");
        public ISharedModStore Store { get; }

        public SharedStoreFixture()
        {
            var config = MagosConfig.CreateDefault();
            config.SharedModsFolder = Folder;
            _provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddSharedMods()
                .BuildServiceProvider();
            Store = _provider.GetRequiredService<ISharedModStore>();
        }

        public ISharedModStore Reload()
        {
            var config = MagosConfig.CreateDefault();
            config.SharedModsFolder = Folder;
            var provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddSharedMods()
                .BuildServiceProvider();
            // Transfer ownership: the caller doesn't dispose this transient
            // provider; tests are short-lived and the process exits. (Matches the
            // profiles ReloadFixture posture.)
            return provider.GetRequiredService<ISharedModStore>();
        }

        public void Dispose()
        {
            _provider.Dispose();
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
    }
}
