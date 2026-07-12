using Modificus.Curator.General;

namespace Modificus.Curator.General.Tests;

/// <summary>
/// <see cref="AppStateStore"/>: round-trip + first-run + corrupt-file safety
/// for all three persisted fields (the active-profile id, the last-update-check
/// timestamp, and the manual-refresh throttle window). Establishes the
/// persistence contracts the shell VM (active id), the update-check runner
/// (last-check gate + manual window), rely on, and pins the no-clobber
/// guarantee: assigning one field preserves the others.
/// </summary>
public sealed class AppStateStoreTests
{
    [Fact]
    public void ActiveProfileId_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.ActiveProfileId);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Set_persists_and_get_round_trips_the_value()
    {
        var path = TempPath();
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);

            store.ActiveProfileId = id;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted value.
            Assert.Equal(id, new AppStateStore(path).ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Set_null_clears_the_recorded_value()
    {
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);
            store.ActiveProfileId = Guid.NewGuid();
            store.ActiveProfileId = null;

            Assert.Null(new AppStateStore(path).ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Get_returns_null_for_corrupt_file_without_throwing()
    {
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ this is not json");

            var store = new AppStateStore(path);

            Assert.Null(store.ActiveProfileId);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Get_returns_null_when_parent_directory_is_missing()
    {
        // First-run case: neither the directory nor the file exist yet.
        var path = System.IO.Path.Combine(Path.GetTempPath(), "curator-state-missing-" + Guid.NewGuid(), "app-state.json");

        Assert.Null(new AppStateStore(path).ActiveProfileId);
    }

    // ---- LastUpdateCheckUtc (Task 2: persisted update-check gate) ---------

    [Fact]
    public void LastUpdateCheckUtc_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.LastUpdateCheckUtc);
    }

    [Fact]
    public void LastUpdateCheckUtc_persists_and_round_trips_the_value()
    {
        var path = TempPath();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        try
        {
            var store = new AppStateStore(path);

            store.LastUpdateCheckUtc = stamp;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted value.
            Assert.Equal(stamp, new AppStateStore(path).LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_ActiveProfileId_preserves_a_persisted_LastUpdateCheckUtc()
    {
        // The critical no-clobber guarantee (Task 2): the store holds an
        // in-memory cached model and writes it whole, so assigning one property
        // must not reset the other to its default. Without the cache, saving
        // ActiveProfileId would write a fresh model with LastUpdateCheckUtc
        // null and wipe this stamp.
        var path = TempPath();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);
            store.LastUpdateCheckUtc = stamp;

            store.ActiveProfileId = id; // must NOT wipe the timestamp

            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(id, store.ActiveProfileId);
            // And the on-disk file holds both: a fresh instance sees both too.
            var reloaded = new AppStateStore(path);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_LastUpdateCheckUtc_preserves_a_persisted_ActiveProfileId()
    {
        // The mirror of the above: assigning the timestamp must not wipe the id.
        var path = TempPath();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var id = Guid.NewGuid();
        try
        {
            var store = new AppStateStore(path);
            store.ActiveProfileId = id;

            store.LastUpdateCheckUtc = stamp; // must NOT wipe the id

            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_LastUpdateCheckUtc_loads_null_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes LastUpdateCheckUtc as null (System.Text.Json
        // default for an absent nullable member). The runner floors null to
        // DateTimeOffset.MinValue, so the opening startup check fires normally.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            // Hand-write an old-shape file: only ActiveProfileId.
            var id = Guid.NewGuid();
            File.WriteAllText(path, "{\"activeProfileId\":\"" + id + "\"}");

            var store = new AppStateStore(path);

            Assert.Null(store.LastUpdateCheckUtc);
            Assert.Equal(id, store.ActiveProfileId); // existing field still reads
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---- ManualRefreshTimestamps (the manual throttle's persisted window) ---

    [Fact]
    public void ManualRefreshTimestamps_is_null_when_file_is_missing()
    {
        var path = TempPath();
        var store = new AppStateStore(path);

        Assert.Null(store.ManualRefreshTimestamps);
    }

    [Fact]
    public void ManualRefreshTimestamps_persists_and_round_trips_the_list()
    {
        var path = TempPath();
        var stamps = new[]
        {
            new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 2, 3, 5, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 2, 3, 6, 0, TimeSpan.Zero),
        };
        try
        {
            var store = new AppStateStore(path);

            store.ManualRefreshTimestamps = stamps;

            Assert.True(File.Exists(path));
            // A fresh instance over the same file reads the persisted list.
            var reloaded = new AppStateStore(path).ManualRefreshTimestamps;
            Assert.NotNull(reloaded);
            Assert.Equal(stamps, reloaded);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_null_clears_ManualRefreshTimestamps()
    {
        var path = TempPath();
        try
        {
            var store = new AppStateStore(path);
            store.ManualRefreshTimestamps = new[]
            {
                new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            };
            store.ManualRefreshTimestamps = null;

            Assert.Null(new AppStateStore(path).ManualRefreshTimestamps);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Setting_ManualRefreshTimestamps_preserves_the_other_two_fields()
    {
        // The no-clobber guarantee now covers three fields. Set all three, then
        // mutate ManualRefreshTimestamps and confirm ActiveProfileId +
        // LastUpdateCheckUtc survive; then mutate each of those and confirm
        // ManualRefreshTimestamps survives too (the cached whole-model write is
        // symmetric).
        var path = TempPath();
        var id = Guid.NewGuid();
        var stamp = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var window = new[]
        {
            new DateTimeOffset(2025, 1, 2, 3, 5, 0, TimeSpan.Zero),
        };
        try
        {
            var store = new AppStateStore(path);
            store.ActiveProfileId = id;
            store.LastUpdateCheckUtc = stamp;

            // Mutate the new field: the other two must survive (on the instance
            // and on disk for a fresh instance).
            store.ManualRefreshTimestamps = window;
            Assert.Equal(id, store.ActiveProfileId);
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);

            var reloaded = new AppStateStore(path);
            Assert.Equal(id, reloaded.ActiveProfileId);
            Assert.Equal(stamp, reloaded.LastUpdateCheckUtc);
            Assert.Equal(window, reloaded.ManualRefreshTimestamps);

            // Mutate ActiveProfileId: the other two (incl. the new field) survive.
            var id2 = Guid.NewGuid();
            store.ActiveProfileId = id2;
            Assert.Equal(stamp, store.LastUpdateCheckUtc);
            Assert.Equal(window, store.ManualRefreshTimestamps);

            // Mutate LastUpdateCheckUtc: the other two survive.
            var stamp2 = stamp.AddHours(1);
            store.LastUpdateCheckUtc = stamp2;
            Assert.Equal(id2, store.ActiveProfileId);
            Assert.Equal(window, store.ManualRefreshTimestamps);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Old_state_file_without_ManualRefreshTimestamps_loads_null_for_the_new_field()
    {
        // First-run-after-upgrade: an existing app-state.json from before this
        // field existed deserializes ManualRefreshTimestamps as null
        // (System.Text.Json default for an absent nullable member). The runner
        // treats null as an empty queue (no throttle history), so a manual
        // refresh fires freely after upgrade.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            // Hand-write an old-shape file: only the two prior fields.
            var id = Guid.NewGuid();
            File.WriteAllText(
                path,
                "{\"activeProfileId\":\"" + id + "\",\"lastUpdateCheckUtc\":\"2025-01-02T03:04:05+00:00\"}");

            var store = new AppStateStore(path);

            Assert.Null(store.ManualRefreshTimestamps);
            Assert.Equal(id, store.ActiveProfileId); // existing fields still read
            Assert.NotNull(store.LastUpdateCheckUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Corrupt_file_seeds_all_fields_null_without_throwing()
    {
        // The first-run-safe contract extends to every field: a corrupt file
        // must not throw, and all fields read null.
        var path = TempPath();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ this is not json");

            var store = new AppStateStore(path);

            Assert.Null(store.ActiveProfileId);
            Assert.Null(store.LastUpdateCheckUtc);
            Assert.Null(store.ManualRefreshTimestamps);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Default_state_path_is_under_app_data()
    {
        var path = AppStateStore.DefaultStatePath();

        // Windows nests the data root under an org/app hierarchy
        // (ModifAmorphic\Modificus Curator); Linux keeps the flat
        // Modificus Curator segment. The state file sits directly under it.
        var expectedSegment = OperatingSystem.IsWindows()
            ? System.IO.Path.Combine("ModifAmorphic", "Modificus Curator")
            : "Modificus Curator";
        Assert.EndsWith(System.IO.Path.Combine(expectedSegment, "app-state.json"), path);
    }

    private static string TempPath() =>
        System.IO.Path.Combine(Path.GetTempPath(), "curator-state-" + Guid.NewGuid(), "app-state.json");

    private static void Cleanup(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
