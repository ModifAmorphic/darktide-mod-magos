using System.Text.Json;

namespace Magos.Modificus.General;

/// <summary>
/// Default <see cref="IAppStateStore"/>. Loads + saves a single JSON file at
/// <c>&lt;app-data&gt;/Magos Modificus/app-state.json</c> (<c>{ "ActiveProfileId": "&lt;guid&gt;" | null }</c>).
/// The app-data dir is derived the same way <see cref="ConfigLoader"/> derives
/// its config path. JSON is handled with <see cref="JsonSerializer"/> (direct,
/// read+write) rather than <c>Microsoft.Extensions.Configuration</c>. The
/// latter is binding-oriented and read-only; a tiny writable state file is the
/// wrong fit for it.
/// </summary>
public sealed class AppStateStore : IAppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;

    /// <summary>
    /// Creates a store for <paramref name="path"/>; <c>null</c> resolves to
    /// <see cref="DefaultStatePath"/>.
    /// </summary>
    public AppStateStore(string? path = null)
    {
        _path = path ?? DefaultStatePath();
    }

    /// <summary>The state file this store reads + writes.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public Guid? ActiveProfileId
    {
        get => Load();
        set => Save(value);
    }

    /// <summary>The conventional state-file location: <c>&lt;app-data&gt;/Magos Modificus/app-state.json</c>.</summary>
    public static string DefaultStatePath() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Magos Modificus",
            "app-state.json");

    private Guid? Load()
    {
        // First-run safe: missing or corrupt file → null, never throws.
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            var model = JsonSerializer.Deserialize<StateModel>(json, JsonOptions);
            return model?.ActiveProfileId;
        }
        catch
        {
            // Missing/corrupt/permission-denied: treat as "no state recorded."
            return null;
        }
    }

    private void Save(Guid? value)
    {
        // Best-effort: runtime app-state is non-critical, so a persistence
        // failure must not crash the app mid-interaction.
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(new StateModel { ActiveProfileId = value }, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Swallow: the app keeps working without persisted state.
        }
    }

    private sealed class StateModel
    {
        public Guid? ActiveProfileId { get; set; }
    }
}
