using Magos.Modificus.Config;
using Magos.Modificus.General;

namespace Magos.Modificus.General.Tests;

/// <summary>
/// Establishes the config-loader pattern: JSON overrides are bound onto full
/// defaults, and a missing file yields a fully-usable defaulted config.
/// </summary>
public sealed class ConfigLoaderTests
{
    [Fact]
    public void Load_returns_defaults_when_json_file_is_missing()
    {
        // A path that does not exist; optional:true keeps the loader from throwing.
        var loader = new ConfigLoader(Path.Combine(Path.GetTempPath(), "magos-missing-" + Guid.NewGuid() + ".json"));

        var config = loader.Load();

        Assert.NotNull(config.Logging);
        Assert.Equal("Information", config.Logging.Level);
        Assert.False(string.IsNullOrEmpty(config.Logging.LogFile));
        Assert.False(string.IsNullOrEmpty(config.ProfilesBaseFolder));
        Assert.False(string.IsNullOrEmpty(config.SharedModsFolder));
        Assert.False(string.IsNullOrEmpty(config.EnginseerRuntimeDir));
        Assert.EndsWith("profiles", config.ProfilesBaseFolder);
    }

    [Fact]
    public void Load_applies_json_overrides_onto_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "magos-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Logging": { "Level": "Debug" },
              "EnginseerRuntimeDir": "/custom/runtime"
            }
            """);

        try
        {
            var config = new ConfigLoader(configPath).Load();

            Assert.Equal("Debug", config.Logging.Level);
            Assert.Equal("/custom/runtime", config.EnginseerRuntimeDir);
            // Untouched fields keep their defaults.
            Assert.False(string.IsNullOrEmpty(config.ProfilesBaseFolder));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_returns_defaults_when_parent_directory_is_missing()
    {
        // First-run case: neither the config directory nor file exist yet.
        // SetBasePath would throw if the loader didn't guard against it.
        var path = Path.Combine(Path.GetTempPath(), "magos-missing-dir-" + Guid.NewGuid(), "config.json");

        var config = new ConfigLoader(path).Load();

        Assert.Equal("Information", config.Logging.Level);
        Assert.False(string.IsNullOrEmpty(config.ProfilesBaseFolder));
    }

    [Fact]
    public void Default_config_path_is_under_app_data()
    {
        var path = ConfigLoader.DefaultConfigPath();

        Assert.EndsWith(Path.Combine("Magos Modificus", "config.json"), path);
    }
}
