using System.Text;
using Magos.Modificus.Config;
using Magos.Modificus.SharedMods;
using Microsoft.Extensions.DependencyInjection;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// Phase 2 shared-first staging contract: shared mods symlink to the shared
/// store; diverged mods symlink to <c>diverged/</c>; <c>staged/</c> holds only
/// symlinks + <c>mods.lst</c> (no copied files); regeneration clears + rebuilds;
/// a missing <c>diverged/</c> copy is skipped + warned (not a crash); a
/// symlink-creation failure throws <see cref="SymlinkStagingException"/> (never
/// silently copies); and <see cref="IProfileService.SetModPolicy"/> reconciles
/// the diverged copy on share↔diverge transitions.
/// </summary>
public sealed class StagingTests
{
    // ---- Share / Diverge symlink targets -----------------------------------

    [Fact]
    public void Shared_mod_is_symlinked_into_shared_store()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF");
        fx.Service.AddMod(profile.Id, "DMF"); // Latest, shared is Latest -> Share

        fx.Service.PrepareModRoot(profile.Id);

        var link = fx.StagedModLink(profile.Id, "DMF");
        Assert.True(Directory.Exists(link), "staged symlink should resolve to the shared mod dir");
        Assert.True(IsSymlink(link), "staged entry should be a symlink, not a copy");
        Assert.Equal(fx.SharedModDir("DMF"), ResolveLink(link));
    }

    [Fact]
    public void Diverged_mod_is_symlinked_into_profile_diverged_dir()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF", policyLabel: "pinned", version: "1.0.0");
        // Profile pins a different version -> Diverge.
        fx.Service.AddMod(profile.Id, "DMF", new PinnedPolicy(new Version(2, 0, 0)));
        // Simulate Phase 4 having placed the diverged copy.
        Directory.CreateDirectory(fx.DivergedModDir(profile.Id, "DMF"));

        fx.Service.PrepareModRoot(profile.Id);

        var link = fx.StagedModLink(profile.Id, "DMF");
        Assert.True(Directory.Exists(link));
        Assert.True(IsSymlink(link));
        Assert.Equal(fx.DivergedModDir(profile.Id, "DMF"), ResolveLink(link));
    }

    [Fact]
    public void Staged_root_contains_only_symlinks_and_mods_lst_no_copied_files()
    {
        // The whole point of shared-mod storage: one copy, symlinked. Staging
        // must not duplicate mod files into staged/ — every mod entry is a
        // symlink (ReparsePoint), and only mods.lst is a real file.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF");
        fx.Service.AddMod(profile.Id, "DMF");

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

        // The mod's actual files live only in the shared store.
        Assert.True(File.Exists(Path.Combine(fx.SharedModDir("DMF"), "marker.txt")));
    }

    // ---- regeneration ------------------------------------------------------

    [Fact]
    public void Regeneration_clears_and_rebuilds_staged_root()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("OldMod");
        fx.Service.AddMod(profile.Id, "OldMod");
        fx.Service.PrepareModRoot(profile.Id);
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "OldMod")));

        // Remove OldMod from the profile + shared store; add NewMod.
        fx.Service.RemoveMod(profile.Id, "OldMod");
        fx.SharedStore.Remove("OldMod");
        Directory.Delete(fx.SharedModDir("OldMod"), recursive: true);
        fx.AddSharedMod("NewMod");
        fx.Service.AddMod(profile.Id, "NewMod");

        fx.Service.PrepareModRoot(profile.Id);

        // Old symlink gone; new one present; mods.lst reflects NewMod only.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "OldMod")));
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "NewMod")));
        Assert.Equal("NewMod\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Regeneration_never_follows_stale_symlinks_into_shared_store()
    {
        // Data-safety: a prior staged/DMF symlink pointing into the shared store
        // must be removed as a LINK (not followed), so the shared files survive a
        // regenerate. This guards ClearStagedDir's symlink-awareness.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.PrepareModRoot(profile.Id);
        var sharedMarker = Path.Combine(fx.SharedModDir("DMF"), "marker.txt");
        Assert.True(File.Exists(sharedMarker));

        // Regenerate (DMF now disabled -> not staged). The shared copy must be intact.
        fx.Service.SetModEnabled(profile.Id, "DMF", enabled: false);
        fx.Service.PrepareModRoot(profile.Id);

        Assert.True(File.Exists(sharedMarker), "shared mod files must survive staged/ regeneration");
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
    }

    // ---- missing-diverged grace --------------------------------------------

    [Fact]
    public void Diverged_mod_without_local_copy_is_skipped_not_crashed()
    {
        // Phase 4 hasn't acquired the diverged copy -> the mod is omitted from
        // staged/ + mods.lst, with a warning. No exception.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF", policyLabel: "pinned", version: "1.0.0");
        fx.Service.AddMod(profile.Id, "DMF", new PinnedPolicy(new Version(2, 0, 0)));
        // diverged/DMF intentionally NOT created (acquisition pending).

        fx.Service.PrepareModRoot(profile.Id); // must not throw

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Mod_not_in_shared_store_is_skipped_when_no_diverged_copy()
    {
        // A profile mod whose name isn't in the shared store at all, and has no
        // diverged/ copy, is skipped (nothing to stage). Other mods still stage.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("RealMod");
        fx.Service.AddMod(profile.Id, "GhostMod"); // not in shared store, no diverged/
        fx.Service.AddMod(profile.Id, "RealMod");

        fx.Service.PrepareModRoot(profile.Id);

        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "GhostMod")));
        Assert.Equal("RealMod\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void Diverged_mod_without_shared_entry_uses_diverged_copy()
    {
        // No shared entry, but a diverged/ copy exists -> stage the local copy.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "LocalOnly");
        Directory.CreateDirectory(fx.DivergedModDir(profile.Id, "LocalOnly"));

        fx.Service.PrepareModRoot(profile.Id);

        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "LocalOnly")));
        Assert.Equal("LocalOnly\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    // ---- symlink-failure path ----------------------------------------------

    [Fact]
    public void Symlink_creation_failure_throws_clear_error_not_silent_copy()
    {
        // Inject a SymlinkCreator that simulates Windows-without-symlink-perms.
        SymlinkCreator throwing = (_, _) =>
            throw new UnauthorizedAccessException("simulated: symlink not permitted");

        using var fx = new ProfileServiceFixture(symlink: throwing);
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF");
        fx.Service.AddMod(profile.Id, "DMF");

        var ex = Assert.Throws<SymlinkStagingException>(() => fx.Service.PrepareModRoot(profile.Id));
        Assert.Contains("Symlinks are required", ex.Message);
        Assert.Contains("Developer Mode", ex.Message);

        // And it did NOT silently copy: no real mod dir under staged/.
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
    }

    // ---- divergence transitions (SetModPolicy) -----------------------------

    [Fact]
    public void SetModPolicy_share_to_diverge_marks_metadata_only()
    {
        // share->diverge: the policy is recorded; diverged/ is Phase 4's job, so
        // it is NOT created here. Staging looks for it (absent -> skip + warn).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF", policyLabel: "pinned", version: "1.0.0");
        fx.Service.AddMod(profile.Id, "DMF", new PinnedPolicy(new Version(1, 0, 0))); // Share (same pin)

        fx.Service.SetModPolicy(profile.Id, "DMF", new PinnedPolicy(new Version(2, 0, 0))); // -> Diverge

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal(new Version(2, 0, 0), Assert.IsType<PinnedPolicy>(entry.Policy).Version);
        // diverged/ not created by SetModPolicy (acquisition is Phase 4).
        Assert.False(Directory.Exists(fx.DivergedModDir(profile.Id, "DMF")));

        // Staging: diverged copy absent -> skipped.
        fx.Service.PrepareModRoot(profile.Id);
        Assert.False(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
    }

    [Fact]
    public void SetModPolicy_diverge_to_share_drops_diverged_copy()
    {
        // diverge->share: the local copy is no longer needed; dropped.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF", policyLabel: "pinned", version: "1.0.0");
        fx.Service.AddMod(profile.Id, "DMF", new PinnedPolicy(new Version(2, 0, 0))); // Diverge
        // Pretend Phase 4 placed the diverged copy.
        Directory.CreateDirectory(fx.DivergedModDir(profile.Id, "DMF"));
        Assert.True(Directory.Exists(fx.DivergedModDir(profile.Id, "DMF")));

        fx.Service.SetModPolicy(profile.Id, "DMF", new PinnedPolicy(new Version(1, 0, 0))); // -> Share

        Assert.False(Directory.Exists(fx.DivergedModDir(profile.Id, "DMF")),
            "converging to Share should reclaim the diverged/ copy");

        // Staging now symlinks to the shared store.
        fx.Service.PrepareModRoot(profile.Id);
        Assert.True(IsSymlink(fx.StagedModLink(profile.Id, "DMF")));
        Assert.Equal(fx.SharedModDir("DMF"), ResolveLink(fx.StagedModLink(profile.Id, "DMF")));
    }

    [Fact]
    public void SetModPolicy_diverge_to_share_graceful_when_no_diverged_copy()
    {
        // Converging to Share when there's nothing to drop is a no-op (no throw).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF", policyLabel: "pinned", version: "1.0.0");
        fx.Service.AddMod(profile.Id, "DMF", new PinnedPolicy(new Version(2, 0, 0)));

        fx.Service.SetModPolicy(profile.Id, "DMF", new PinnedPolicy(new Version(1, 0, 0))); // -> Share

        Assert.False(Directory.Exists(fx.DivergedModDir(profile.Id, "DMF"))); // never existed
    }

    [Fact]
    public void SetModPolicy_persists_and_round_trips_policy()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");

        fx.Service.SetModPolicy(profile.Id, "DMF", new PinnedPolicy(new Version(3, 1, 4)));

        // Reload from disk (new service instance) to prove persistence.
        var reloadConfig = MagosConfig.CreateDefault();
        reloadConfig.ProfilesBaseFolder = fx.BaseFolder;
        reloadConfig.SharedModsFolder = fx.SharedFolder;
        using var provider = new ServiceCollection()
            .AddSingleton(reloadConfig)
            .AddLogging()
            .AddSharedMods()
            .AddProfiles()
            .BuildServiceProvider();
        var reloaded = provider.GetRequiredService<IProfileService>().GetModList(profile.Id);

        var entry = Assert.Single(reloaded);
        Assert.Equal(new Version(3, 1, 4), Assert.IsType<PinnedPolicy>(entry.Policy).Version);
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
