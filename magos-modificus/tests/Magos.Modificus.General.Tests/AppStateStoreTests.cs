using Magos.Modificus.General;

namespace Magos.Modificus.General.Tests;

/// <summary>
/// <see cref="AppStateStore"/>: round-trip + first-run + corrupt-file safety.
/// Establishes the active-profile persistence contract the shell VM relies on.
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
        var path = System.IO.Path.Combine(Path.GetTempPath(), "magos-state-missing-" + Guid.NewGuid(), "app-state.json");

        Assert.Null(new AppStateStore(path).ActiveProfileId);
    }

    [Fact]
    public void Default_state_path_is_under_app_data()
    {
        var path = AppStateStore.DefaultStatePath();

        Assert.EndsWith(System.IO.Path.Combine("Magos Modificus", "app-state.json"), path);
    }

    private static string TempPath() =>
        System.IO.Path.Combine(Path.GetTempPath(), "magos-state-" + Guid.NewGuid(), "app-state.json");

    private static void Cleanup(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
