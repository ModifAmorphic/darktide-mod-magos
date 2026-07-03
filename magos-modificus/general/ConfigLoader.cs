using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Magos.Modificus.Config;

namespace Magos.Modificus.General;

/// <summary>Loads <see cref="MagosConfig"/> from JSON, with full defaults.</summary>
public interface IConfigLoader
{
    /// <summary>Loads the config, applying JSON overrides onto default values.</summary>
    MagosConfig Load();

    /// <summary>
    /// Persists <paramref name="config"/> back to the JSON file the loader reads
    /// from. Used by the Preferences flow (and any future write-back): the
    /// config file is machine-managed, so rewriting it wholesale is fine. A
    /// missing parent directory is created; first-run safe.
    /// </summary>
    void Save(MagosConfig config);
}

/// <summary>
/// Default <see cref="IConfigLoader"/>. Reads a JSON file via
/// <c>Microsoft.Extensions.Configuration</c> and binds it onto a defaulted
/// <see cref="MagosConfig"/>. A missing or partial file yields a fully-usable
/// config (every field has a default). <see cref="Save"/> writes the config
/// back through <see cref="JsonSerializer"/> (the config file is
/// machine-managed; the binder is read-only, so write-back is direct JSON).
/// </summary>
public sealed class ConfigLoader : IConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // POCO enum serialization as strings (mirrors how the binder reads them,
        // so a round-trip stays human-readable + stable across enum renumbering).
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;

    /// <summary>
    /// Creates a loader for <paramref name="path"/>; <c>null</c> resolves to
    /// <c>&lt;app-data&gt;/config.json</c>.
    /// </summary>
    public ConfigLoader(string? path = null)
    {
        _path = path ?? DefaultConfigPath();
    }

    /// <summary>The path this loader reads from + writes to.</summary>
    public string Path => _path;

    /// <inheritdoc />
    public MagosConfig Load()
    {
        var config = MagosConfig.CreateDefault();
        var directory = System.IO.Path.GetDirectoryName(_path);

        // Only build the JSON configuration source when the parent directory
        // actually exists. On a fresh first run neither the directory nor the
        // file exist yet; SetBasePath throws if the directory is missing, so we
        // skip straight to defaults. AddJsonFile(optional:true) already handles
        // a missing file once the directory exists.
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            new ConfigurationBuilder()
                .SetBasePath(directory)
                .AddJsonFile(System.IO.Path.GetFileName(_path), optional: true, reloadOnChange: false)
                .Build()
                .Bind(config);
        }

        return config;
    }

    /// <inheritdoc />
    public void Save(MagosConfig config)
    {
        // The config file is machine-managed; a wholesale write is fine and
        // simpler than tracking which section changed. Best-effort: a write
        // failure (unwritable dir, full disk) is swallowed so a Preferences
        // change never crashes the app mid-interaction (the in-memory config
        // already reflects the change; the persisted copy is best-effort).
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Swallow: the app keeps running with the in-memory value; the
            // persisted copy is not critical.
        }
    }

    /// <summary>The conventional config-file location: <c>&lt;app-data&gt;/config.json</c>.</summary>
    public static string DefaultConfigPath() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Magos Modificus",
            "config.json");
}
