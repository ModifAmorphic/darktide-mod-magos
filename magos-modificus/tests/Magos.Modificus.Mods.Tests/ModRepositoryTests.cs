using System.Text.Json;
using Magos.Modificus.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Mods.Tests;

/// <summary>
/// <see cref="IModRepository"/>: container/version CRUD, <see cref="FindBySource"/>
/// per source-type + <see cref="FindUntrackedByName"/>, manifest round-trip +
/// in-memory index rebuild from a scan, <see cref="PruneUnreferenced"/> (drops the
/// right folders + empty containers), opaque version-folder naming, derived paths.
/// Resolves via DI (black-box) against a temp <c>ModsFolder</c>.
/// </summary>
public sealed class ModRepositoryTests
{
    // ---- list / get --------------------------------------------------------

    [Fact]
    public void List_is_empty_when_folder_is_empty()
    {
        using var fx = new RepoFixture();
        Assert.Empty(fx.Repo.List());
    }

    [Fact]
    public void Get_returns_null_for_unknown_id()
    {
        using var fx = new RepoFixture();
        Assert.Null(fx.Repo.Get(Guid.NewGuid()));
    }

    [Fact]
    public void CreateContainer_assigns_a_non_empty_guid_and_writes_manifest()
    {
        using var fx = new RepoFixture();

        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        Assert.NotEqual(Guid.Empty, container.Id);
        Assert.Equal("DMF", container.Name);
        Assert.IsType<UntrackedSource>(container.Source);
        Assert.Empty(container.Versions);
        // The manifest is on disk.
        Assert.True(File.Exists(fx.ManifestPath(container.Id)));
    }

    [Fact]
    public void CreateContainer_rejects_null_or_whitespace_name()
    {
        using var fx = new RepoFixture();
        Assert.Throws<ArgumentException>(() => fx.Repo.CreateContainer(new UntrackedSource(), ""));
        Assert.Throws<ArgumentException>(() => fx.Repo.CreateContainer(new UntrackedSource(), "   "));
    }

    [Fact]
    public void Get_returns_the_created_container()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 42 }, "WT");

        var loaded = fx.Repo.Get(container.Id);
        Assert.NotNull(loaded);
        Assert.Equal(container.Id, loaded!.Id);
        Assert.Equal("WT", loaded.Name);
        Assert.Equal(42, Assert.IsType<NexusSource>(loaded.Source).ModId);
    }

    // ---- FindBySource / FindUntrackedByName -------------------------------

    [Fact]
    public void FindBySource_finds_Nexus_by_mod_id()
    {
        using var fx = new RepoFixture();
        var created = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");

        var found = fx.Repo.FindBySource(new NexusSource { ModId = 4242 });
        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
    }

    [Fact]
    public void FindBySource_finds_GitHub_by_owner_and_repo_ordinal()
    {
        using var fx = new RepoFixture();
        var created = fx.Repo.CreateContainer(new GitHubSource { Owner = "Magos", Repo = "WT" }, "WT");

        Assert.NotNull(fx.Repo.FindBySource(new GitHubSource { Owner = "Magos", Repo = "WT" }));
        Assert.Null(fx.Repo.FindBySource(new GitHubSource { Owner = "magos", Repo = "WT" })); // ordinal
        Assert.Null(fx.Repo.FindBySource(new GitHubSource { Owner = "Magos", Repo = "other" }));
    }

    [Fact]
    public void FindBySource_returns_null_for_Untracked_and_for_unknown_sources()
    {
        // Untracked identity is the container Name; route through FindUntrackedByName.
        using var fx = new RepoFixture();
        fx.Repo.CreateContainer(new UntrackedSource(), "WT");

        Assert.Null(fx.Repo.FindBySource(new UntrackedSource()));
        Assert.Null(fx.Repo.FindBySource(new NexusSource { ModId = 1 }));
    }

    [Fact]
    public void FindUntrackedByName_finds_untracked_container_by_name_ordinal()
    {
        using var fx = new RepoFixture();
        var created = fx.Repo.CreateContainer(new UntrackedSource(), "WeaponTweaks");

        var found = fx.Repo.FindUntrackedByName("WeaponTweaks");
        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);

        Assert.Null(fx.Repo.FindUntrackedByName("weapontweaks")); // ordinal, case-sensitive
        Assert.Null(fx.Repo.FindUntrackedByName("Other"));
    }

    [Fact]
    public void Untracked_and_Nexus_with_same_name_do_not_collide()
    {
        // Different source-types are separate namespaces (goal #4: no collision
        // across sources).
        using var fx = new RepoFixture();
        var untracked = fx.Repo.CreateContainer(new UntrackedSource(), "WT");
        var nexus = fx.Repo.CreateContainer(new NexusSource { ModId = 99 }, "WT");

        Assert.NotEqual(untracked.Id, nexus.Id);
        Assert.Equal(untracked.Id, fx.Repo.FindUntrackedByName("WT")!.Id);
        Assert.Equal(nexus.Id, fx.Repo.FindBySource(new NexusSource { ModId = 99 })!.Id);
    }

    // ---- AddVersion (new + dedup + isLatest) ------------------------------

    [Fact]
    public void AddVersion_creates_opaque_folder_and_marks_isLatest()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var updated = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker.txt"), "1.0");
        });

        var version = Assert.Single(updated.Versions);
        Assert.Equal("1.0", version.VersionString);
        Assert.True(version.IsLatest);
        Assert.NotEmpty(version.Folder);
        // Opaque: the folder name is a hex GUID (32 chars, no dashes), not the
        // version tag.
        Assert.Matches("^[0-9a-f]{32}$", version.Folder);
        Assert.NotEqual("1.0", version.Folder);

        // Files landed in the derived version-folder path.
        var versionPath = fx.Repo.GetVersionFolderPath(container.Id, version.Folder);
        Assert.True(File.Exists(Path.Combine(versionPath, "marker.txt")));
    }

    [Fact]
    public void AddVersion_flips_isLatest_to_the_newest_by_importedAt()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var v1 = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var v2 = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);

        // v2 is the newest; it carries isLatest. v1's isLatest was cleared.
        Assert.True(v2.Versions.Single(v => v.VersionString == "2.0").IsLatest);
        Assert.False(v2.Versions.Single(v => v.VersionString == "1.0").IsLatest);
    }

    [Fact]
    public void AddVersion_with_same_versionString_dedups_reusing_the_folder()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var first = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "first");
        });
        var firstFolder = first.Versions.Single(v => v.VersionString == "1.0").Folder;

        // Re-import the same versionString: same folder, files refreshed, no new
        // version entry.
        var second = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "b.txt"), "second");
        });

        var version = Assert.Single(second.Versions);
        Assert.Equal(firstFolder, version.Folder); // folder reused
        var versionPath = fx.Repo.GetVersionFolderPath(container.Id, version.Folder);
        Assert.False(File.Exists(Path.Combine(versionPath, "a.txt")), "re-import should clean + repopulate (no merge)");
        Assert.True(File.Exists(Path.Combine(versionPath, "b.txt")));
    }

    [Fact]
    public void AddVersion_throws_KeyNotFoundException_for_unknown_container()
    {
        using var fx = new RepoFixture();
        Assert.Throws<KeyNotFoundException>(() =>
            fx.Repo.AddVersion(Guid.NewGuid(), "1.0", EmptyPopulate));
    }

    // ---- RemoveVersion -----------------------------------------------------

    [Fact]
    public void RemoveVersion_drops_folder_and_manifest_entry()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var folder = updated.Versions[0].Folder;

        fx.Repo.RemoveVersion(container.Id, folder);

        var reloaded = fx.Repo.Get(container.Id);
        Assert.Empty(reloaded!.Versions);
        Assert.False(Directory.Exists(fx.Repo.GetVersionFolderPath(container.Id, folder)));
    }

    [Fact]
    public void RemoveVersion_promotes_newest_remaining_to_isLatest()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var updated = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);
        var latestFolder = updated.Versions.Single(v => v.IsLatest).Folder;

        fx.Repo.RemoveVersion(container.Id, latestFolder);

        var reloaded = fx.Repo.Get(container.Id);
        var promoted = Assert.Single(reloaded!.Versions);
        Assert.Equal("1.0", promoted.VersionString);
        Assert.True(promoted.IsLatest);
    }

    [Fact]
    public void RemoveVersion_is_idempotent_for_unknown_container_or_folder()
    {
        using var fx = new RepoFixture();
        // Unknown container: no-op, no throw.
        fx.Repo.RemoveVersion(Guid.NewGuid(), "whatever");

        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        // Unknown folder on a real container: no-op, no throw.
        fx.Repo.RemoveVersion(container.Id, "nonexistent");
        Assert.Empty(fx.Repo.Get(container.Id)!.Versions);
    }

    // ---- manifest round-trip + index rebuild ------------------------------

    [Fact]
    public void Container_manifest_round_trips_through_a_new_repository_instance()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new GitHubSource { Owner = "o", Repo = "r" }, "WT");
        fx.Repo.AddVersion(container.Id, "v1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        });

        var reloaded = fx.Reload();

        var found = reloaded.FindBySource(new GitHubSource { Owner = "o", Repo = "r" });
        Assert.NotNull(found);
        Assert.Equal("WT", found!.Name);
        var version = Assert.Single(found.Versions);
        Assert.Equal("v1.0", version.VersionString);
        Assert.True(version.IsLatest);
        Assert.NotEmpty(version.Folder);
    }

    [Fact]
    public void Index_rebuild_from_scan_picks_up_all_containers()
    {
        // The repository must rebuild its in-memory index from a scan, not from a
        // single databank file. Multiple containers from a prior instance are
        // visible after a reload.
        using var fx = new RepoFixture();
        var c1 = fx.Repo.CreateContainer(new UntrackedSource(), "A");
        var c2 = fx.Repo.CreateContainer(new NexusSource { ModId = 1 }, "B");
        var c3 = fx.Repo.CreateContainer(new GitHubSource { Owner = "o", Repo = "r" }, "C");

        var reloaded = fx.Reload();

        Assert.Equal(3, reloaded.List().Count);
        Assert.NotNull(reloaded.Get(c1.Id));
        Assert.NotNull(reloaded.Get(c2.Id));
        Assert.NotNull(reloaded.Get(c3.Id));
    }

    [Fact]
    public void Index_rebuild_skips_non_container_dirs_and_corrupt_manifests()
    {
        using var fx = new RepoFixture();
        var good = fx.Repo.CreateContainer(new UntrackedSource(), "Good");

        // A non-guid dir under the root: ignored by the scan.
        Directory.CreateDirectory(Path.Combine(fx.Folder, "not-a-guid"));
        // A guid dir with a corrupt container.json: skipped (logged), not fatal.
        var badId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(fx.Folder, badId.ToString()));
        File.WriteAllText(fx.ManifestPath(badId), "{ this is not json");

        var reloaded = fx.Reload();

        var only = Assert.Single(reloaded.List());
        Assert.Equal(good.Id, only.Id);
    }

    [Fact]
    public void Container_manifest_is_utf8_json_with_kind_discriminators_no_bom()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(
            new GitHubSource { Owner = "o", Repo = "r" },
            "WT");
        fx.Repo.AddVersion(container.Id, "1.2", EmptyPopulate);

        var raw = File.ReadAllText(fx.ManifestPath(container.Id));
        Assert.Contains("\"$kind\": \"github\"", raw);
        // No BOM.
        var bytes = File.ReadAllBytes(fx.ManifestPath(container.Id));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        // And it round-trips as a container.
        var reparsed = JsonSerializer.Deserialize<ModContainer>(raw)!;
        Assert.Equal(container.Id, reparsed.Id);
    }

    // ---- PruneUnreferenced -------------------------------------------------

    [Fact]
    public void Prune_drops_unreferenced_version_folders_keeps_referenced()
    {
        using var fx = new RepoFixture();
        var keep = fx.Repo.CreateContainer(new UntrackedSource(), "Keep");
        var keepVersion = fx.Repo.AddVersion(keep.Id, "1.0", EmptyPopulate);
        var drop = fx.Repo.CreateContainer(new UntrackedSource(), "Drop");
        var dropVersion = fx.Repo.AddVersion(drop.Id, "1.0", EmptyPopulate);

        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>
        {
            (keep.Id, keepVersion.Versions[0].Folder),
        });

        // Kept container intact.
        Assert.NotNull(fx.Repo.Get(keep.Id));
        Assert.True(Directory.Exists(fx.Repo.GetVersionFolderPath(keep.Id, keepVersion.Versions[0].Folder)));
        // Drop container had only the unreferenced version; it is removed entirely
        // (empty after the prune).
        Assert.Null(fx.Repo.Get(drop.Id));
        Assert.False(Directory.Exists(Path.Combine(fx.Folder, drop.Id.ToString())));
    }

    [Fact]
    public void Prune_removes_empty_containers()
    {
        // A container with zero versions after the prune is removed entirely
        // (manifest + dir).
        using var fx = new RepoFixture();
        var empty = fx.Repo.CreateContainer(new UntrackedSource(), "Empty");

        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>());

        Assert.Null(fx.Repo.Get(empty.Id));
        Assert.False(Directory.Exists(Path.Combine(fx.Folder, empty.Id.ToString())));
    }

    [Fact]
    public void Prune_keeps_a_version_when_at_least_one_profile_references_it()
    {
        // Two versions on one container; one referenced, one not. Only the
        // unreferenced one is dropped; the container survives.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        var v1 = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var v2 = fx.Repo.AddVersion(container.Id, "2.0", EmptyPopulate);
        var v1Folder = v1.Versions.Single(v => v.VersionString == "1.0").Folder;
        var v2Folder = v2.Versions.Single(v => v.VersionString == "2.0").Folder;

        // Reference v1 only (e.g. a profile pinned to "1.0"). v2 (the latest) is
        // unreferenced and dropped.
        fx.Repo.PruneUnreferenced(new HashSet<(Guid, string)>
        {
            (container.Id, v1Folder),
        });

        var reloaded = fx.Repo.Get(container.Id);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.Versions);
        Assert.Equal("1.0", reloaded.Versions[0].VersionString);
        Assert.True(reloaded.Versions[0].IsLatest); // promoted on the removal of v2.
        Assert.False(Directory.Exists(fx.Repo.GetVersionFolderPath(container.Id, v2Folder)));
    }

    // ---- DI registration ---------------------------------------------------

    [Fact]
    public void AddMods_registers_resolvable_IModRepository()
    {
        var config = MagosConfig.CreateDefault();
        using var provider = new ServiceCollection()
            .AddSingleton(config)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddMods()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IModRepository>());
    }

    // ---- fixture + helpers -------------------------------------------------

    private static readonly Action<string> EmptyPopulate = dir =>
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
    };

    /// <summary>Per-test fixture: temp <c>ModsFolder</c> + a DI-resolved
    /// <see cref="IModRepository"/>.</summary>
    private sealed class RepoFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "magos-repo-" + Guid.NewGuid());
        public IModRepository Repo { get; }

        public RepoFixture()
        {
            var config = MagosConfig.CreateDefault();
            config.ModsFolder = Folder;
            _provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            Repo = _provider.GetRequiredService<IModRepository>();
        }

        public string ManifestPath(Guid containerId) =>
            Path.Combine(Folder, containerId.ToString(), "container.json");

        public IModRepository Reload()
        {
            var config = MagosConfig.CreateDefault();
            config.ModsFolder = Folder;
            var provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddMods()
                .BuildServiceProvider();
            // Transient provider: tests are short-lived; the process exits before
            // disposal matters (matches the existing Profiles fixture posture).
            return provider.GetRequiredService<IModRepository>();
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
