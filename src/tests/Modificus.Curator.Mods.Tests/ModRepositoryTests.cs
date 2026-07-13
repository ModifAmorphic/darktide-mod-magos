using System.Text.Json;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Mods.Tests;

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
    public void AddVersion_dedup_failure_preserves_the_old_version_and_manifest()
    {
        // Core transactional invariant: a failed re-import (populateFolder
        // throws partway through extraction) must leave the OLD version's files
        // intact on disk and the manifest unchanged. The pre-transactional
        // implementation deleted the old version folder (CleanTarget) BEFORE
        // invoking populateFolder, so an extraction failure stranded a
        // manifest-referenced folder with no recovery (the startup prune only
        // reclaims containers no profile references). PopulateAtomically stages
        // into a temp + swaps on success, so the old version is never touched
        // until the new content is fully extracted.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        // First import of "1.0": the original content on disk.
        var first = fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "original");
        });
        var originalFolder = first.Versions.Single(v => v.VersionString == "1.0").Folder;
        var versionPath = fx.Repo.GetVersionFolderPath(container.Id, originalFolder);
        Assert.True(File.Exists(Path.Combine(versionPath, "a.txt")));

        // Re-import "1.0": populateFolder writes one file then simulates a
        // real extraction failure (CRC error, disk full, I/O, etc.). The
        // partial write goes into the TEMP, never reaching the version folder.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Repo.AddVersion(container.Id, "1.0", dir =>
            {
                File.WriteAllText(Path.Combine(dir, "partial.txt"), "partial");
                throw new InvalidOperationException("simulated extraction failure");
            }));
        Assert.Equal("simulated extraction failure", ex.Message);

        // The OLD version's files survived on disk: a.txt still there, the
        // partial.txt written into the temp never reached the version folder.
        Assert.True(File.Exists(Path.Combine(versionPath, "a.txt")),
            "A failed re-import must leave the old version's files intact.");
        Assert.False(File.Exists(Path.Combine(versionPath, "partial.txt")),
            "The failed temp write must not leak into the version folder.");

        // No .tmp.* orphan left under the container dir (the failure path
        // cleaned up its temp).
        var containerDir = Path.Combine(fx.Folder, container.Id.ToString());
        var leftoverTemps = Directory.EnumerateDirectories(containerDir)
            .Where(n => Path.GetFileName(n).Contains(".tmp."))
            .ToArray();
        Assert.Empty(leftoverTemps);

        // Manifest unchanged on disk: reload a fresh repo (reads container.json
        // from disk, not the in-memory index) + verify exactly one version "1.0"
        // with the original folder. Guards against any future failure-path code
        // that mutates the persisted manifest despite the populate throw.
        var reloaded = fx.Reload().Get(container.Id);
        Assert.NotNull(reloaded);
        var version = Assert.Single(reloaded!.Versions);
        Assert.Equal("1.0", version.VersionString);
        Assert.Equal(originalFolder, version.Folder);
    }

    [Fact]
    public void AddVersion_new_version_failure_leaves_no_folder_and_no_manifest_entry()
    {
        // Transactional invariant for the new-version branch: a populateFolder
        // failure must create nothing on disk and add no manifest entry. The
        // pre-transactional implementation pre-created versionDir and called
        // populateFolder on it, so a failure left an empty/partial folder on
        // disk (and the manifest write was reached only after populate, which
        // is why no entry was added, but the disk footprint was leaked).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Repo.AddVersion(container.Id, "1.0", dir =>
            {
                File.WriteAllText(Path.Combine(dir, "partial.txt"), "partial");
                throw new InvalidOperationException("simulated extraction failure");
            }));
        Assert.Equal("simulated extraction failure", ex.Message);

        // The container dir contains only container.json: no version folder
        // created, no .tmp.* orphan left.
        var containerDir = Path.Combine(fx.Folder, container.Id.ToString());
        var subdirs = Directory.EnumerateDirectories(containerDir).ToArray();
        Assert.Empty(subdirs);

        // Manifest has zero versions.
        var reloaded = fx.Repo.Get(container.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded!.Versions);
    }

    [Fact]
    public void AddVersion_populateFolder_receives_an_existing_empty_dir_on_both_branches()
    {
        // New contract: populateFolder receives an EXISTING, EMPTY directory (a
        // temp staged by the repo). Replaces the prior band-aid regression test
        // (AddVersion_dedup_ensures_folder_exists_before_populate), which only
        // asserted existence. The contract is now stronger (empty too) because
        // the dir is a fresh temp, not the cleaned-but-reused version folder.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        // New-version branch: callback sees an existing, empty dir.
        fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Assert.True(Directory.Exists(dir),
                "populateFolder must receive an existing directory (new-version branch).");
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        });

        // Dedup branch: callback again sees an existing, empty dir (the temp,
        // not the old version folder with its prior a.txt).
        fx.Repo.AddVersion(container.Id, "1.0", dir =>
        {
            Assert.True(Directory.Exists(dir),
                "populateFolder must receive an existing directory (dedup branch).");
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
            File.WriteAllText(Path.Combine(dir, "b.txt"), "y");
        });
    }

    [Fact]
    public void AddVersion_propagates_the_original_exception_from_populateFolder()
    {
        // The repo must rethrow populateFolder's exception AS-IS (no swallowing,
        // no wrapping), so callers see the actual failure type + message. This
        // is what lets the UI surface the real cause (e.g. InvalidDataException
        // for a corrupt archive from ModImportService.ExtractArchive).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fx.Repo.AddVersion(container.Id, "1.0", _ =>
                throw new InvalidOperationException("simulated extraction failure")));

        Assert.Equal("simulated extraction failure", ex.Message);
        // No wrapping: the propagated exception is exactly the one thrown.
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void AddVersion_sweeps_orphan_temp_dirs_left_by_a_prior_crashed_import()
    {
        // Crash-recovery: if the process dies between CreateDirectory(temp) and
        // the atomic Move, the temp is left as a <versionFolder>.tmp.<guid>
        // orphan under the container dir. The repo's index is built from
        // container.json (not by scanning version subfolders), so the orphan is
        // invisible to the index but occupies disk. SweepOrphanTemps deletes
        // any *.tmp.* directories at the start of each AddVersion call.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");

        // Simulate a crashed prior import: leave a recognizable orphan temp dir
        // under the container dir, plus a decoy non-tmp dir that must be left
        // alone.
        var containerDir = Path.Combine(fx.Folder, container.Id.ToString());
        var orphanPath = Path.Combine(containerDir, "deadbeef.tmp." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Combine(orphanPath, "partial.txt"), "partial");
        var decoyPath = Path.Combine(containerDir, "regular-folder");
        Directory.CreateDirectory(decoyPath);

        fx.Repo.AddVersion(container.Id, "1.0", dir =>
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x"));

        // The orphan was swept; the decoy is untouched.
        Assert.False(Directory.Exists(orphanPath), "Orphan .tmp.* dir must be swept by AddVersion.");
        Assert.True(Directory.Exists(decoyPath), "Non-tmp dirs must be left alone.");
    }

    [Fact]
    public void RebuildIndex_sweeps_orphan_temp_dirs_at_startup()
    {
        // Crash-recovery across containers: SweepOrphanTemps also runs during
        // RebuildIndex (construction/rescan), once per container dir. An orphan
        // left by a crashed import into container A is reclaimed at the next
        // index build even if container A is never re-imported (the per-AddVersion
        // sweep would otherwise never touch it). Without this, an orphan in a
        // never-re-imported container would linger on disk indefinitely.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        fx.Repo.AddVersion(container.Id, "1.0", dir =>
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x"));

        // Simulate a crashed prior import: leave an orphan temp + a decoy dir.
        var containerDir = Path.Combine(fx.Folder, container.Id.ToString());
        var orphanPath = Path.Combine(containerDir, "deadbeef.tmp." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Combine(orphanPath, "partial.txt"), "partial");
        var decoyPath = Path.Combine(containerDir, "regular-folder");
        Directory.CreateDirectory(decoyPath);

        // A fresh repo (construction = RebuildIndex) sweeps the orphan. NO
        // AddVersion call here, deliberately: this is the case the per-AddVersion
        // sweep cannot cover (the container is never re-imported).
        fx.Reload();

        Assert.False(Directory.Exists(orphanPath), "Startup RebuildIndex must sweep orphan .tmp.* dirs.");
        Assert.True(Directory.Exists(decoyPath), "Non-tmp dirs must be left alone.");
    }

    [Fact]
    public void AddVersion_throws_KeyNotFoundException_for_unknown_container()
    {
        using var fx = new RepoFixture();
        Assert.Throws<KeyNotFoundException>(() =>
            fx.Repo.AddVersion(Guid.NewGuid(), "1.0", EmptyPopulate));
    }

    // ---- AddVersion + RemoteUploadedAt (Nexus update-check basis) ---------

    [Fact]
    public void AddVersion_records_RemoteUploadedAt_on_a_new_version()
    {
        // The publish date forwarded by the acquisition layer is stamped on the
        // new entry (the basis for the update-check publish-date comparison).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var publishedAt = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, publishedAt);

        var version = Assert.Single(updated.Versions);
        Assert.Equal(publishedAt, version.RemoteUploadedAt);
    }

    [Fact]
    public void AddVersion_default_remoteUploadedAt_is_null()
    {
        // Existing callers (manual imports, profile fixture helpers) omit the
        // param; the entry's RemoteUploadedAt is null (the update check then
        // falls back to ImportedAt).
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "Mod");

        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);

        var version = Assert.Single(updated.Versions);
        Assert.Null(version.RemoteUploadedAt);
    }

    [Fact]
    public void AddVersion_dedup_refreshes_RemoteUploadedAt_on_re_import()
    {
        // Re-importing the same VersionString refreshes the files AND
        // RemoteUploadedAt (matching how dedup refreshes files). A
        // re-acquired version carries the current publish date, not the stale
        // one from the first import, so a post-update check does not re-flag.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var first = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var refresh = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, first);
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, refresh);

        var version = Assert.Single(updated.Versions);
        Assert.Equal(refresh, version.RemoteUploadedAt);
    }

    [Fact]
    public void AddVersion_persists_RemoteUploadedAt_through_a_new_repository_instance()
    {
        // Backward-compatible on disk: a nullable init-only property round-
        // trips through container.json. A pre-existing manifest (without the
        // field) deserializes the field to null; a manifest written with the
        // field preserves the value on reload.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Mod");
        var publishedAt = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);
        fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate, publishedAt);

        var reloaded = fx.Reload();
        var version = Assert.Single(reloaded.Get(container.Id)!.Versions);
        Assert.Equal(publishedAt, version.RemoteUploadedAt);
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

    // ---- RenameContainer ---------------------------------------------------

    [Fact]
    public void RenameContainer_updates_the_name_and_persists_it_to_the_manifest()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 42 }, "Old Name");

        var updated = fx.Repo.RenameContainer(container.Id, "New Author Title");

        Assert.NotNull(updated);
        Assert.Equal("New Author Title", updated!.Name);
        // In-memory index reflects the new name.
        Assert.Equal("New Author Title", fx.Repo.Get(container.Id)!.Name);
        // The manifest on disk reflects the new name (reload reads container.json,
        // not the in-memory index).
        Assert.Equal("New Author Title", fx.Reload().Get(container.Id)!.Name);
    }

    [Fact]
    public void RenameContainer_keeps_identity_and_does_not_move_the_directory()
    {
        // Identity (Id) is unchanged + the on-disk container directory (keyed by
        // Id) does not move: only the Name field in the manifest changes.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 7 }, "Old");
        var dirBefore = Path.Combine(fx.Folder, container.Id.ToString());
        Assert.True(Directory.Exists(dirBefore));

        var updated = fx.Repo.RenameContainer(container.Id, "New");

        Assert.Equal(container.Id, updated!.Id);
        Assert.Equal(dirBefore, Path.Combine(fx.Folder, updated.Id.ToString()));
        Assert.True(Directory.Exists(dirBefore));
        Assert.True(File.Exists(fx.ManifestPath(container.Id)));
    }

    [Fact]
    public void RenameContainer_is_a_noop_when_the_name_already_matches()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "Same");

        var result = fx.Repo.RenameContainer(container.Id, "Same");

        Assert.NotNull(result);
        Assert.Equal("Same", result!.Name);
        // The returned reference is the unchanged container (same name); no error.
        Assert.Equal("Same", fx.Repo.Get(container.Id)!.Name);
    }

    [Fact]
    public void RenameContainer_returns_null_for_an_unknown_container()
    {
        using var fx = new RepoFixture();

        Assert.Null(fx.Repo.RenameContainer(Guid.NewGuid(), "Whatever"));
    }

    [Fact]
    public void RenameContainer_is_ordinal_case_sensitive()
    {
        // The name comparison is ordinal; a case-only difference is a rename.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 11 }, "WeaponTweaks");

        var updated = fx.Repo.RenameContainer(container.Id, "weapontweaks");

        Assert.Equal("weapontweaks", updated!.Name);
        Assert.Equal("weapontweaks", fx.Repo.Get(container.Id)!.Name);
    }

    [Fact]
    public void RenameContainer_keeps_the_untracked_name_index_consistent()
    {
        // Renaming an untracked container must update the untracked-name dedup
        // index: FindUntrackedByName resolves the new name + NOT the old one.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "OldUntracked");

        fx.Repo.RenameContainer(container.Id, "NewUntracked");

        Assert.Null(fx.Repo.FindUntrackedByName("OldUntracked"));
        var found = fx.Repo.FindUntrackedByName("NewUntracked");
        Assert.NotNull(found);
        Assert.Equal(container.Id, found!.Id);
    }

    [Fact]
    public void RenameContainer_on_a_nexus_container_does_not_touch_untracked_dedup()
    {
        // Nexus identity is the mod id, not the name: renaming a Nexus container
        // must not register it in (or remove it from) the untracked-name index,
        // and FindBySource still resolves it by mod id.
        using var fx = new RepoFixture();
        var nexus = fx.Repo.CreateContainer(new NexusSource { ModId = 55 }, "Nexus Old");

        fx.Repo.RenameContainer(nexus.Id, "Nexus New");

        // Still resolvable by mod id (identity unchanged).
        var found = fx.Repo.FindBySource(new NexusSource { ModId = 55 });
        Assert.NotNull(found);
        Assert.Equal("Nexus New", found!.Name);
        // Never registered under either name in the untracked index.
        Assert.Null(fx.Repo.FindUntrackedByName("Nexus Old"));
        Assert.Null(fx.Repo.FindUntrackedByName("Nexus New"));
    }

    // ---- manifest round-trip + index rebuild ------------------------------

    [Fact]
    public void Container_manifest_round_trips_through_a_new_repository_instance()
    {
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new NexusSource { ModId = 4242 }, "WT");
        fx.Repo.AddVersion(container.Id, "v1.0", dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        });

        var reloaded = fx.Reload();

        var found = reloaded.FindBySource(new NexusSource { ModId = 4242 });
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
        var c3 = fx.Repo.CreateContainer(new NexusSource { ModId = 2 }, "C");

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
            new NexusSource { ModId = 4242 },
            "WT");
        fx.Repo.AddVersion(container.Id, "1.2", EmptyPopulate);

        var raw = File.ReadAllText(fx.ManifestPath(container.Id));
        Assert.Contains("\"$kind\": \"nexus\"", raw);
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
        using var provider = new ServiceCollection()
            .AddSingleton<IConfigLoader>(new FakeConfigLoader())
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddMods()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService<IModRepository>());
    }

    // ---- Relocate ----------------------------------------------------------

    [Fact]
    public void Relocate_moves_all_containers_from_old_to_new_path()
    {
        using var fx = new RepoFixture();
        var c1 = fx.Repo.CreateContainer(new UntrackedSource(), "A");
        var c2 = fx.Repo.CreateContainer(new NexusSource { ModId = 7 }, "B");
        fx.Repo.AddVersion(c1.Id, "1.0", EmptyPopulate);
        fx.Repo.AddVersion(c2.Id, "2.0", EmptyPopulate);

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-relocated-" + Guid.NewGuid());
        try
        {
            fx.Repo.Relocate(newPath);

            // Every container directory landed under the new path.
            Assert.True(Directory.Exists(Path.Combine(newPath, c1.Id.ToString())));
            Assert.True(Directory.Exists(Path.Combine(newPath, c2.Id.ToString())));
            // The version folders moved with them.
            Assert.True(File.Exists(Path.Combine(newPath, c1.Id.ToString(),
                fx.Repo.Get(c1.Id)!.Versions[0].Folder, "marker.txt")));
            // The old path is empty of container dirs.
            Assert.False(Directory.Exists(Path.Combine(fx.Folder, c1.Id.ToString())));
            Assert.False(Directory.Exists(Path.Combine(fx.Folder, c2.Id.ToString())));
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    [Fact]
    public void Relocate_moves_save_config_and_rescan_in_one_atomic_call()
    {
        // Atomic contract: a single Relocate call moves the containers, saves
        // ModsFolder = newPath into config, and rescans. After it returns, the
        // config points at the new path, the index reflects the new location,
        // path derivation uses the new root, and the old path is empty of moved
        // container dirs.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "DMF");
        var updated = fx.Repo.AddVersion(container.Id, "1.0", EmptyPopulate);
        var versionFolder = updated.Versions[0].Folder;

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-rescan-" + Guid.NewGuid());
        try
        {
            fx.Repo.Relocate(newPath);

            // Config was saved with the new path (FakeConfigLoader mirrors the
            // round-trip, so a re-load reflects it).
            Assert.Equal(newPath, fx.ConfigLoader.Load().ModsFolder);

            // Index reflects the new location.
            var reloaded = fx.Repo.Get(container.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("DMF", reloaded!.Name);
            Assert.Single(reloaded.Versions);

            // Path derivation now uses the new root.
            var versionPath = fx.Repo.GetVersionFolderPath(container.Id, versionFolder);
            Assert.StartsWith(newPath, versionPath);
            Assert.True(File.Exists(Path.Combine(versionPath, "marker.txt")));

            // Old path is empty of the moved container dir.
            Assert.False(Directory.Exists(Path.Combine(fx.Folder, container.Id.ToString())));
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    [Fact]
    public void Relocate_rolls_back_the_move_when_config_save_throws()
    {
        // Atomicity: the move succeeded, but the config save threw. Relocate
        // rolls the moved container dirs back to the old path so files + config
        // agree at the old path, then rethrows so the caller sees the failure.
        using var fx = new RepoFixture();
        fx.ConfigLoader.SaveException = new IOException("disk full");
        var c1 = fx.Repo.CreateContainer(new UntrackedSource(), "A");
        var c2 = fx.Repo.CreateContainer(new UntrackedSource(), "B");
        fx.Repo.AddVersion(c1.Id, "1.0", EmptyPopulate);
        fx.Repo.AddVersion(c2.Id, "1.0", EmptyPopulate);

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-rollback-throw-" + Guid.NewGuid());
        try
        {
            Assert.Throws<IOException>(() => fx.Repo.Relocate(newPath));

            // Config still points at the old path (the save threw, so nothing
            // persisted).
            Assert.Equal(fx.Folder, fx.ConfigLoader.Load().ModsFolder);

            // No container dir landed under the new path (rolled back).
            Assert.False(Directory.Exists(Path.Combine(newPath, c1.Id.ToString())));
            Assert.False(Directory.Exists(Path.Combine(newPath, c2.Id.ToString())));

            // Containers are back at the old path.
            Assert.True(Directory.Exists(Path.Combine(fx.Folder, c1.Id.ToString())));
            Assert.True(Directory.Exists(Path.Combine(fx.Folder, c2.Id.ToString())));
            // Their version files came back with them.
            Assert.True(File.Exists(Path.Combine(
                fx.Folder, c1.Id.ToString(),
                fx.Repo.Get(c1.Id)!.Versions[0].Folder, "marker.txt")));

            // Index still resolves them (ids are stable; paths derive from the
            // live config, which still points at the old path).
            Assert.NotNull(fx.Repo.Get(c1.Id));
            Assert.NotNull(fx.Repo.Get(c2.Id));
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    [Fact]
    public void Relocate_rolls_back_the_move_when_config_save_silently_fails()
    {
        // The production ConfigLoader.Save swallows write errors by design (the
        // best-effort Preferences flow), so a thrown exception is not the only
        // save-failure mode. Relocate verifies the save persisted by re-loading;
        // a save that returned without throwing but also without persisting
        // (PersistOnSave = false) is treated as a failure and rolled back too.
        using var fx = new RepoFixture();
        fx.ConfigLoader.PersistOnSave = false;
        var c1 = fx.Repo.CreateContainer(new UntrackedSource(), "A");
        fx.Repo.AddVersion(c1.Id, "1.0", EmptyPopulate);

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-rollback-silent-" + Guid.NewGuid());
        try
        {
            Assert.Throws<IOException>(() => fx.Repo.Relocate(newPath));

            // Config still reflects the old path (the save did not persist).
            Assert.Equal(fx.Folder, fx.ConfigLoader.Load().ModsFolder);

            // The moved container was rolled back to the old path.
            Assert.False(Directory.Exists(Path.Combine(newPath, c1.Id.ToString())));
            Assert.True(Directory.Exists(Path.Combine(fx.Folder, c1.Id.ToString())));
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    [Fact]
    public void Relocate_to_same_path_is_a_noop()
    {
        // Relocating to the path the config already points at is a no-op: no
        // move, no exception. (The conflict check would otherwise always fire,
        // since the current root contains the indexed UUIDs.)
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "A");

        fx.Repo.Relocate(fx.Folder);

        // Container is still where it was, still indexed.
        Assert.True(Directory.Exists(Path.Combine(fx.Folder, container.Id.ToString())));
        Assert.NotNull(fx.Repo.Get(container.Id));
    }

    [Fact]
    public void Relocate_rejects_null_or_whitespace_path()
    {
        using var fx = new RepoFixture();
        Assert.Throws<ArgumentException>(() => fx.Repo.Relocate(""));
        Assert.Throws<ArgumentException>(() => fx.Repo.Relocate("   "));
        Assert.Throws<ArgumentException>(() => fx.Repo.Relocate(null!));
    }

    [Fact]
    public void Relocate_rejects_relative_path()
    {
        using var fx = new RepoFixture();
        Assert.Throws<ArgumentException>(() => fx.Repo.Relocate("relative/path"));
    }

    [Fact]
    public void Relocate_throws_when_destination_already_contains_a_tracked_container_uuid()
    {
        // The destination already has a directory whose name is one of the
        // indexed container UUIDs: refuse, so the move cannot silently shadow
        // an existing container.
        using var fx = new RepoFixture();
        var container = fx.Repo.CreateContainer(new UntrackedSource(), "A");

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-conflict-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(newPath);
            // Pre-create a directory with the SAME uuid name as our container.
            Directory.CreateDirectory(Path.Combine(newPath, container.Id.ToString()));

            Assert.Throws<InvalidOperationException>(() => fx.Repo.Relocate(newPath));

            // On rejection, nothing moved: the source container is still in place.
            Assert.True(Directory.Exists(Path.Combine(fx.Folder, container.Id.ToString())));
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    [Fact]
    public void Relocate_does_not_move_non_container_directories()
    {
        // Stray directories under the old root that are NOT indexed container
        // UUIDs are left in place (the repo does not own them).
        using var fx = new RepoFixture();
        fx.Repo.CreateContainer(new UntrackedSource(), "A");

        // A non-UUID stray dir + a UUID dir with no manifest (never indexed).
        var strayPath = Path.Combine(fx.Folder, "not-a-guid");
        Directory.CreateDirectory(strayPath);
        File.WriteAllText(Path.Combine(strayPath, "junk.txt"), "x");

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-stray-" + Guid.NewGuid());
        try
        {
            fx.Repo.Relocate(newPath);

            // The stray dir is NOT moved (out of scope: not a tracked container).
            Assert.True(Directory.Exists(strayPath));
            Assert.False(Directory.Exists(Path.Combine(newPath, "not-a-guid")));
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    // ---- Relocate (cross-volume copy + delete) ----------------------------

    [Fact]
    public void Relocate_across_volumes_copies_and_deletes_so_no_container_is_stranded()
    {
        // The key M1 assertion: a relocate that cannot use Directory.Move (a
        // cross-volume move, e.g. Windows C: -> D:) still succeeds via copy +
        // delete instead of throwing on every container. Without the volume
        // branch, every move would throw, the save would still flip ModsFolder,
        // Rescan would rebuild against an empty new path, and the containers
        // would be stranded (invisible, no UI recovery).
        //
        // A real second volume cannot be simulated under one temp root, so the
        // volume detector is forced to "always cross-volume" via the internal
        // test ctor, exercising the copy + delete path directly.
        using var fx = new RepoFixture(sameVolume: (_, _) => false);
        var c1 = fx.Repo.CreateContainer(new UntrackedSource(), "A");
        var c2 = fx.Repo.CreateContainer(new NexusSource { ModId = 9 }, "B");
        fx.Repo.AddVersion(c1.Id, "1.0", EmptyPopulate);
        fx.Repo.AddVersion(c2.Id, "2.0", EmptyPopulate);

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-xvol-" + Guid.NewGuid());
        try
        {
            fx.Repo.Relocate(newPath);

            // Every container directory landed at the new path (copy ran).
            Assert.True(Directory.Exists(Path.Combine(newPath, c1.Id.ToString())));
            Assert.True(Directory.Exists(Path.Combine(newPath, c2.Id.ToString())));
            // The version files came along (the tree was reproduced, not just
            // the top dir).
            Assert.True(File.Exists(Path.Combine(newPath, c1.Id.ToString(),
                fx.Repo.Get(c1.Id)!.Versions[0].Folder, "marker.txt")));
            // The source dirs are GONE: the delete ran after the copy, so this
            // is a real move, not a copy that leaves a stale duplicate.
            Assert.False(Directory.Exists(Path.Combine(fx.Folder, c1.Id.ToString())));
            Assert.False(Directory.Exists(Path.Combine(fx.Folder, c2.Id.ToString())));
            // Config + index reflect the new path (no stranding).
            Assert.Equal(newPath, fx.ConfigLoader.Load().ModsFolder);
            Assert.NotNull(fx.Repo.Get(c1.Id));
            Assert.NotNull(fx.Repo.Get(c2.Id));
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    [Fact]
    public void Relocate_across_volumes_rolls_back_to_old_path_when_save_fails()
    {
        // Cross-volume relocate whose config save throws: the rollback is also
        // a copy + delete (the volume relationship is symmetric), and it must
        // restore the moved containers to the old path so files + config agree
        // there again. Without the volume branch in the rollback, the rollback
        // would throw on every container (Directory.Move across volumes) and
        // leave them stranded at the new path.
        using var fx = new RepoFixture(sameVolume: (_, _) => false);
        fx.ConfigLoader.SaveException = new IOException("disk full");
        var c1 = fx.Repo.CreateContainer(new UntrackedSource(), "A");
        fx.Repo.AddVersion(c1.Id, "1.0", EmptyPopulate);

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-xvol-rollback-" + Guid.NewGuid());
        try
        {
            Assert.Throws<IOException>(() => fx.Repo.Relocate(newPath));

            // Config still points at the old path (the save threw).
            Assert.Equal(fx.Folder, fx.ConfigLoader.Load().ModsFolder);
            // Nothing landed at the new path (rolled back + deleted there).
            Assert.False(Directory.Exists(Path.Combine(newPath, c1.Id.ToString())));
            // The container is back at the old path (rollback copy + delete).
            Assert.True(Directory.Exists(Path.Combine(fx.Folder, c1.Id.ToString())));
            Assert.True(File.Exists(Path.Combine(
                fx.Folder, c1.Id.ToString(),
                fx.Repo.Get(c1.Id)!.Versions[0].Folder, "marker.txt")));
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    [Fact]
    public void Relocate_same_volume_still_uses_atomic_directory_move()
    {
        // The fast path is preserved: when the detector reports same-volume,
        // Relocate uses Directory.Move (a rename), not copy + delete. The
        // observable difference is that the source is gone after the move
        // (same end state as copy + delete), but this guards the branch: a
        // forced same-volume detector must not take the cross-volume path.
        using var fx = new RepoFixture(sameVolume: (_, _) => true);
        var c1 = fx.Repo.CreateContainer(new UntrackedSource(), "A");
        fx.Repo.AddVersion(c1.Id, "1.0", EmptyPopulate);

        var newPath = Path.Combine(Path.GetTempPath(), "curator-repo-samevol-" + Guid.NewGuid());
        try
        {
            fx.Repo.Relocate(newPath);

            Assert.True(Directory.Exists(Path.Combine(newPath, c1.Id.ToString())));
            Assert.False(Directory.Exists(Path.Combine(fx.Folder, c1.Id.ToString())));
            Assert.Equal(newPath, fx.ConfigLoader.Load().ModsFolder);
        }
        finally
        {
            if (Directory.Exists(newPath)) Directory.Delete(newPath, recursive: true);
        }
    }

    [Fact]
    public void DirectoryCopy_reproduces_the_source_tree_and_leaves_the_source_intact()
    {
        // The shared helper behind the cross-volume relocate + the folder
        // import: a faithful recursive copy. Files + nested subdirs land at the
        // target exactly; the source is left intact (the caller is responsible
        // for the delete that turns a copy into a move).
        using var fx = new RepoFixture();
        var source = Path.Combine(fx.Folder, "src");
        Directory.CreateDirectory(Path.Combine(source, "sub", "deep"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "b");
        File.WriteAllText(Path.Combine(source, "sub", "deep", "c.txt"), "c");

        var target = Path.Combine(fx.Folder, "dst");

        DirectoryCopy.Copy(source, target);

        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
        Assert.True(File.Exists(Path.Combine(target, "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(target, "sub", "deep", "c.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(target, "sub", "deep", "c.txt")));
        // Source untouched (copy does not delete).
        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        Assert.True(File.Exists(Path.Combine(source, "sub", "deep", "c.txt")));
    }

    // ---- Rescan ------------------------------------------------------------

    [Fact]
    public void Rescan_drops_index_entries_for_containers_removed_from_disk()
    {
        // Rescan clears the index first, so a container deleted out-of-band
        // (between scans) disappears from the index. Without a clear, the prior
        // entry would survive as stale.
        using var fx = new RepoFixture();
        var keep = fx.Repo.CreateContainer(new UntrackedSource(), "Keep");
        var drop = fx.Repo.CreateContainer(new UntrackedSource(), "Drop");

        // Out-of-band delete of the "Drop" container dir.
        Directory.Delete(Path.Combine(fx.Folder, drop.Id.ToString()), recursive: true);

        fx.Repo.Rescan();

        Assert.NotNull(fx.Repo.Get(keep.Id));
        Assert.Null(fx.Repo.Get(drop.Id));
        Assert.Single(fx.Repo.List());
    }

    [Fact]
    public void Rescan_picks_up_containers_added_out_of_band()
    {
        // A container.json copied into the mods folder by an external tool
        // (backup restore, sync) appears in the index after Rescan.
        using var fx = new RepoFixture();
        var first = fx.Repo.CreateContainer(new UntrackedSource(), "First");

        // Simulate an external copy: write a fresh container.json for a new UUID
        // directly to disk (the manifest format the repo reads).
        var externalId = Guid.NewGuid();
        var externalDir = Path.Combine(fx.Folder, externalId.ToString());
        Directory.CreateDirectory(externalDir);
        File.WriteAllText(
            Path.Combine(externalDir, "container.json"),
            $$"""
            {
              "$kind": "untracked",
              "Id": "{{externalId}}",
              "Name": "External",
              "Source": { "$kind": "untracked" },
              "Versions": []
            }
            """);

        // Before rescan: only the originally-created container is indexed.
        Assert.Single(fx.Repo.List());

        fx.Repo.Rescan();

        // After rescan: both are indexed.
        Assert.Equal(2, fx.Repo.List().Count);
        Assert.NotNull(fx.Repo.Get(first.Id));
        Assert.NotNull(fx.Repo.Get(externalId));
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
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "curator-repo-" + Guid.NewGuid());
        public IModRepository Repo { get; }

        /// <summary>
        /// The live <see cref="FakeConfigLoader"/> the repository reads
        /// <c>ModsFolder</c> from. Exposed so tests can set failure hooks
        /// (<see cref="FakeConfigLoader.SaveException"/> /
        /// <see cref="FakeConfigLoader.PersistOnSave"/>) for the relocate
        /// rollback coverage and re-read the persisted state.
        /// </summary>
        public FakeConfigLoader ConfigLoader => _configLoader;

        private readonly FakeConfigLoader _configLoader;

        /// <summary>
        /// The live <see cref="CuratorConfig"/> the repository reads
        /// <c>ModsFolder</c> from.
        /// </summary>
        public CuratorConfig Config => _configLoader.Config;

        public RepoFixture(Func<string, string, bool>? sameVolume = null)
        {
            var config = CuratorConfig.CreateDefault();
            config.ModsFolder = Folder;
            _configLoader = new FakeConfigLoader { Config = config };

            if (sameVolume is null)
            {
                // Production DI path: AddMods() resolves ModRepository via its
                // public ctor (real same-volume detector).
                _provider = new ServiceCollection()
                    .AddSingleton<IConfigLoader>(_configLoader)
                    .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                    .AddMods()
                    .BuildServiceProvider();
                Repo = _provider.GetRequiredService<IModRepository>();
            }
            else
            {
                // Test-injected detector: construct ModRepository directly via
                // its internal ctor so a test can force the cross-volume copy +
                // delete path (a real second volume cannot be simulated under
                // one temp root).
                _provider = new ServiceCollection()
                    .AddSingleton<IConfigLoader>(_configLoader)
                    .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                    .BuildServiceProvider();
                var logger = _provider.GetRequiredService<ILogger<ModRepository>>();
                Repo = new ModRepository(_configLoader, logger, sameVolume);
            }
        }

        public string ManifestPath(Guid containerId) =>
            Path.Combine(Folder, containerId.ToString(), "container.json");

        public IModRepository Reload()
        {
            var config = CuratorConfig.CreateDefault();
            config.ModsFolder = Folder;
            var provider = new ServiceCollection()
                .AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config })
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
