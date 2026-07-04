using System.Text;
using Magos.Modificus.Config;
using Magos.Modificus.Mods;
using Microsoft.Extensions.DependencyInjection;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// Container-based staging contract: each enabled mod resolves its
/// <see cref="ModVersionPolicy"/> against its <see cref="ModContainer"/>
/// (<see cref="LatestPolicy"/> -> the container's isLatest version folder;
/// <see cref="PinnedPolicy"/> -> the version whose <see cref="ModVersion.Folder"/>
/// matches the pin's <see cref="PinnedPolicy.VersionId"/>) and is symlinked
/// into <c>staged/&lt;displayName&gt;</c>; missing containers/versions are
/// skipped + warned; symlink-name collisions are disambiguated; the staged root
/// holds only symlinks + <c>mods.lst</c> (no copied files); a symlink-creation
/// failure throws <see cref="SymlinkStagingException"/> (never silently copies).
/// </summary>
public sealed class StagingTests
{
    // ---- Latest / Pinned symlink targets -----------------------------------

    [Fact]
    public void Latest_policy_symlinks_to_the_isLatest_version_folder()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        var link = fx.StagedModLink(profile.Id, "DMF");
        Assert.True(Directory.Exists(link), "staged symlink should resolve to the version folder");
        Assert.True(IsSymlink(link), "staged entry should be a symlink, not a copy");
        Assert.Equal(fx.VersionDir(container.Id, container.Versions[0].Folder), ResolveLink(link));
    }

    [Fact]
    public void Pinned_policy_symlinks_to_the_version_matching_the_pin()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        // Add v1.0 (becomes isLatest), then v2.0 (becomes isLatest). Pin to 1.0
        // by its version id (the ModVersion.Folder value).
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.AddVersion(container.Id, "2.0");
        var v1Folder = container.Versions.Single(v => v.VersionString == "1.0").Folder;
        fx.Service.AddMod(profile.Id, container.Id, new PinnedPolicy(v1Folder));

        fx.Service.PrepareModRoot(profile.Id);

        var link = fx.StagedModLink(profile.Id, "DMF");
        Assert.True(Directory.Exists(link));
        Assert.True(IsSymlink(link));
        Assert.Equal(fx.VersionDir(container.Id, v1Folder), ResolveLink(link));
    }

    [Fact]
    public void Moving_isLatest_requires_zero_profile_entry_changes()
    {
        // Acceptance #4: re-keying which version is "latest" is a repository-only
        // change. Two profiles, both Latest, share the same entry; the entry
        // never changes when the container's isLatest moves.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        var v1Folder = container.Versions.Single(v => v.VersionString == "1.0").Folder;
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        var link1 = fx.StagedModLink(profile.Id, "DMF");
        Assert.Equal(fx.VersionDir(container.Id, v1Folder), ResolveLink(link1));

        // Add v2.0 (becomes isLatest). The profile entry is unchanged; staging
        // re-resolves dynamically.
        fx.AddVersion(container.Id, "2.0");
        var v2Folder = fx.Repo.Get(container.Id)!.Versions.Single(v => v.VersionString == "2.0").Folder;
        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal(fx.VersionDir(container.Id, v2Folder), ResolveLink(link1));
        var modEntry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(container.Id, modEntry.ContainerId); // unchanged
    }

    // ---- staging shape: symlinks only + mods.lst --------------------------

    [Fact]
    public void Staged_root_contains_only_symlinks_and_mods_lst_no_copied_files()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        // staged/ contains exactly the symlink + mods.lst.
        var stagedEntries = Directory.GetFileSystemEntries(fx.StagedDir(profile.Id));
        Assert.Equal(2, stagedEntries.Length);

        foreach (var entry in stagedEntries)
        {
            if (Path.GetFileName(entry) == "mods.lst")
            {
                var attrs = File.GetAttributes(entry);
                Assert.True((attrs & FileAttributes.ReparsePoint) == 0,
                    "mods.lst should be a real file, not a symlink");
            }
            else
            {
                Assert.True(IsSymlink(entry),
                    $"staged mod entry '{Path.GetFileName(entry)}' must be a symlink, not a copied directory");
            }
        }

        // The mod's actual files live only in the repository.
        Assert.True(File.Exists(Path.Combine(fx.VersionDir(container.Id, container.Versions[0].Folder), "marker.txt")));
    }

    // ---- regeneration ------------------------------------------------------

    [Fact]
    public void Regeneration_clears_and_rebuilds_staged_root()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var oldContainer = fx.AddContainerWithVersion("OldMod");
        fx.Service.AddMod(profile.Id, oldContainer.Id, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "OldMod")));

        // Remove OldMod from the profile; add NewMod.
        fx.Service.RemoveMod(profile.Id, oldContainer.Id);
        var newContainer = fx.AddContainerWithVersion("NewMod");
        fx.Service.AddMod(profile.Id, newContainer.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        // Old symlink gone; new one present; mods.lst reflects NewMod only.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "OldMod")));
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "NewMod")));
        Assert.Equal("NewMod\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Regeneration_never_follows_stale_symlinks_into_the_repository()
    {
        // Data-safety: a prior staged/DMF symlink pointing into the repository
        // must be removed as a LINK (not followed), so the files survive a
        // regenerate. Guards ClearStagedDir's symlink-awareness.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        var versionPath = fx.VersionDir(container.Id, container.Versions[0].Folder);
        var marker = Path.Combine(versionPath, "marker.txt");
        Assert.True(File.Exists(marker));

        // Regenerate (DMF now disabled -> not staged). The repository files survive.
        fx.Service.SetModEnabled(profile.Id, container.Id, enabled: false);
        fx.Service.PrepareModRoot(profile.Id);

        Assert.True(File.Exists(marker), "repository mod files must survive staged/ regeneration");
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
    }

    // ---- missing-version grace --------------------------------------------

    [Fact]
    public void Mod_whose_container_is_missing_is_skipped_not_crashed()
    {
        // A profile entry whose container is gone (a stale reference, or pruned)
        // is omitted from staged/ + mods.lst, with a warning. No exception.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var realContainer = fx.AddContainerWithVersion("RealMod");
        // A ghost: add an entry pointing at a non-existent container.
        fx.Service.AddMod(profile.Id, realContainer.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, Guid.NewGuid(), ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id); // must not throw

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "Ghost")));
        // Only RealMod appears.
        Assert.Equal("RealMod\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Pinned_policy_with_unknown_version_is_skipped_with_warning()
    {
        // A pin to a version id that does not exist on the container: skip + warn.
        // The mod is omitted from staged/ + mods.lst. (The UI dropdown can't
        // produce such an id; this covers a programmatic / stale-id call.)
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        // AddMod does not validate the versionId (only SetModPolicy does); so a
        // phantom pin can be seeded here for the staging-skip assertion.
        fx.Service.AddMod(profile.Id, container.Id, new PinnedPolicy("no-such-version-id"));

        fx.Service.PrepareModRoot(profile.Id); // must not throw

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Latest_policy_on_a_container_with_no_versions_is_skipped()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var empty = fx.Repo.CreateContainer(new UntrackedSource(), "Empty");
        fx.Service.AddMod(profile.Id, empty.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "Empty")));
        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    // ---- symlink-name disambiguation --------------------------------------

    [Fact]
    public void Two_containers_with_the_same_name_disambiguate_the_symlink()
    {
        // Goal: the loader is agnostic to the symlink name. Two containers with
        // the same display name in one profile must each get a distinct symlink.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var a = fx.AddContainerWithVersion("DMF");
        var b = fx.AddContainerWithVersion("DMF"); // same name, different container
        fx.Service.AddMod(profile.Id, a.Id, ModVersionPolicy.Latest);
        fx.Service.AddMod(profile.Id, b.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        // One symlink is named "DMF"; the other is "DMF-<short-id>" (or similar).
        // Both resolve to distinct version folders; both are listed.
        var entries = Directory.GetFileSystemEntries(fx.StagedDir(profile.Id))
            .Where(p => Path.GetFileName(p) != "mods.lst")
            .ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(2, entries.Select(p => ResolveLink(p)).Distinct().Count());

        var modsLst = File.ReadAllText(fx.ModsLst(profile.Id));
        Assert.Equal(2, modsLst.Count(c => c == '\n'));
    }

    [Fact]
    public void Symlink_name_is_sanitized_for_filesystem_illegal_chars()
    {
        // A container whose Name contains filesystem-illegal chars still stages:
        // the symlink name is sanitized (illegal chars replaced with '_'). The
        // forward-slash is invalid as a filename char on every platform, so it
        // makes a cross-platform test.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "Weapon/Tweaks");
        fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        });
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        fx.Service.PrepareModRoot(profile.Id);

        // mods.lst reflects a sanitized name (the slash is replaced, no subdir).
        var modsLst = File.ReadAllText(fx.ModsLst(profile.Id));
        Assert.DoesNotContain('/', modsLst);
        Assert.Contains("Weapon", modsLst);
        Assert.Contains("Tweaks", modsLst);
        // Exactly one line (the sanitized name did not split into two entries).
        Assert.Equal(1, modsLst.Count(c => c == '\n'));
    }

    // ---- symlink-failure path ----------------------------------------------

    [Fact]
    public void Symlink_creation_failure_throws_clear_error_not_silent_copy()
    {
        SymlinkCreator throwing = (_, _) =>
            throw new UnauthorizedAccessException("simulated: symlink not permitted");

        using var fx = new ProfileServiceFixture(symlink: throwing);
        var profile = fx.Service.CreateProfile("P");
        var container = fx.AddContainerWithVersion("DMF");
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest);

        var ex = Assert.Throws<SymlinkStagingException>(() => fx.Service.PrepareModRoot(profile.Id));
        Assert.Contains("Symlinks are required", ex.Message);
        Assert.Contains("Developer Mode", ex.Message);

        // And it did NOT silently copy: no real mod dir under staged/.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
    }

    // ---- SetModPolicy (no on-disk transition) ------------------------------

    [Fact]
    public void SetModPolicy_persists_and_round_trips_policy_with_no_on_disk_transition()
    {
        // The new model has no diverged copy to reconcile. SetModPolicy just
        // records the policy; staging re-resolves on the next PrepareModRoot.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var container = fx.AddContainerWithVersion("DMF", "1.0");
        fx.AddVersion(container.Id, "2.0"); // v2.0 is isLatest
        var v1Folder = container.Versions.Single(v => v.VersionString == "1.0").Folder;
        fx.Service.AddMod(profile.Id, container.Id, ModVersionPolicy.Latest); // -> v2.0

        // Pin to v1.0 by its version id; staging re-resolves to the v1.0 folder.
        fx.Service.SetModPolicy(profile.Id, container.Id, new PinnedPolicy(v1Folder));

        // Reload from disk (new service instance) to prove persistence.
        var reloadConfig = MagosConfig.CreateDefault();
        reloadConfig.ProfilesBaseFolder = fx.BaseFolder;
        reloadConfig.ModsFolder = fx.ModsFolder;
        using var provider = new ServiceCollection()
            .AddSingleton(reloadConfig)
            .AddLogging()
            .AddMods()
            .AddProfiles()
            .BuildServiceProvider();
        var reloaded = provider.GetRequiredService<IProfileService>();

        var entry = Assert.Single(reloaded.GetModList(profile.Id));
        Assert.Equal(v1Folder, Assert.IsType<PinnedPolicy>(entry.Policy).VersionId);

        // And staging reflects the pin.
        reloaded.PrepareModRoot(profile.Id);
        var link = fx.StagedModLink(profile.Id, "DMF");
        Assert.Equal(fx.VersionDir(container.Id, v1Folder), ResolveLink(link));
    }

    // ---- helpers ------------------------------------------------------------

    private static bool IsSymlink(string path)
    {
        var attrs = File.GetAttributes(path);
        return (attrs & FileAttributes.ReparsePoint) != 0;
    }

    private static string ResolveLink(string path) =>
        File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
            ?? throw new IOException("not a link");
}
