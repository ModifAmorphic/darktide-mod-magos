using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Cross-cutting sentinel-safety tests for linked mods: a marker file inside
/// the external target survives every Curator operation (the external target is
/// never modified/deleted/copied-from), the availability signal recomputes on
/// rescan (missing-then-returned), and a repository relocation (same-volume +
/// cross-volume) moves only the container's manifest dir, never the external
/// target. Uses the real <see cref="IModRepository"/> + <see cref="IModImportService"/>
/// via DI (black-box), and the injectable same-volume detector to force the
/// cross-volume path.
/// </summary>
public sealed class LinkedFolderSafetyTests
{
    // ---- availability recomputation on rescan --------------------------------

    [Fact]
    public void Availability_flips_false_when_external_folder_is_missing_then_true_when_it_returns()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var containerId = fx.Imports.LinkFolder(external);

        Assert.True(fx.Repo.IsExternalAvailable(containerId));

        // Remove the external target + rescan: availability flips to false.
        Directory.Delete(external, recursive: true);
        fx.Repo.Rescan();
        Assert.False(fx.Repo.IsExternalAvailable(containerId));

        // Recreate it + rescan: availability flips back.
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "LinkedMod.mod"), "LinkedMod");
        fx.Repo.Rescan();
        Assert.True(fx.Repo.IsExternalAvailable(containerId));
    }

    [Fact]
    public void Rescan_does_not_touch_the_external_target()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);

        fx.Repo.Rescan();
        fx.Repo.Rescan();

        Assert.NotNull(fx.Repo.Get(containerId));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    // ---- repository relocation (same-volume + cross-volume) ------------------

    [Fact]
    public void Relocate_same_volume_moves_only_the_container_manifest_not_the_external_target()
    {
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("LinkedMod");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var containerId = fx.Imports.LinkFolder(external);

        var newModsRoot = Path.Combine(Path.GetTempPath(), "curator-safety-relocate-" + Guid.NewGuid());
        try
        {
            fx.Repo.Relocate(newModsRoot);

            // The container manifest moved with the rest of the mods root.
            Assert.True(File.Exists(Path.Combine(newModsRoot, containerId.ToString(), "container.json")));
            // The linked container is still resolvable by external path + still
            // available (the external target is unaffected).
            Assert.NotNull(fx.Repo.FindBySource(
                new LinkedSource { ExternalPath = external }));
            Assert.True(fx.Repo.IsExternalAvailable(containerId));
            // The external target + sentinel survived the relocate intact.
            Assert.True(Directory.Exists(external));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(newModsRoot))
            {
                // The new mods root holds only container dirs (no staging links
                // to external targets), so a plain recursive delete is safe.
                Directory.Delete(newModsRoot, recursive: true);
            }
        }
    }
    // Cross-volume relocate coverage lives in ModRepositoryTests
    // (Relocate_cross_volume_copies_a_linked_container_json_and_leaves_external_target_untouched),
    // which has InternalsVisibleTo to force the cross-volume detector.

    // ---- profile deletion + relocate round-trip sentinel survival -----------

    [Fact]
    public void Sentinel_survives_link_stage_remove_rescan_and_profile_deletion_sequence()
    {
        // An end-to-end sequence exercising every Curator operation against the
        // same external target; the sentinel must be byte-identical at the end.
        using var fx = new ProfileServiceFixture();
        var external = fx.MakeExternalModFolder("Survivor");
        var sentinel = Path.Combine(external, "sentinel.txt");

        var containerId = fx.Imports.LinkFolder(external);
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, containerId, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        fx.Service.SetModEnabled(profile.Id, containerId, enabled: false);
        fx.Service.PrepareModRoot(profile.Id);
        fx.Service.SetModEnabled(profile.Id, containerId, enabled: true);
        fx.Service.SetModPolicy(profile.Id, containerId, ModVersionPolicy.Latest);
        fx.Service.PrepareModRoot(profile.Id);
        fx.Repo.Rescan();
        fx.Service.RemoveMod(profile.Id, containerId);
        fx.Service.DeleteProfile(profile.Id);

        Assert.True(Directory.Exists(external));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }
}
