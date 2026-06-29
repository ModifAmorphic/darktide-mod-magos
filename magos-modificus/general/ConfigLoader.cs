using Microsoft.Extensions.Configuration;
using Magos.Modificus.Config;

namespace Magos.Modificus.General;

/// <summary>Loads <see cref="MagosConfig"/> from JSON, with full defaults.</summary>
public interface IConfigLoader
{
    /// <summary>Loads the config, applying JSON overrides onto default values.</summary>
    MagosConfig Load();
}

/// <summary>
/// Default <see cref="IConfigLoader"/>. Reads a JSON file via
/// <c>Microsoft.Extensions.Configuration</c> and binds it onto a defaulted
/// <see cref="MagosConfig"/>. A missing or partial file yields a fully-usable
/// config (every field has a default).
/// </summary>
public sealed class ConfigLoader : IConfigLoader
{
    private readonly string _path;

    /// <summary>
    /// Creates a loader for <paramref name="path"/>; <c>null</c> resolves to
    /// <c>&lt;app-data&gt;/config.json</c>.
    /// </summary>
    public ConfigLoader(string? path = null)
    {
        _path = path ?? DefaultConfigPath();
    }

    /// <summary>The path this loader reads from.</summary>
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

    /// <summary>The conventional config-file location: <c>&lt;app-data&gt;/config.json</c>.</summary>
    public static string DefaultConfigPath() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Magos Modificus",
            "config.json");
}
