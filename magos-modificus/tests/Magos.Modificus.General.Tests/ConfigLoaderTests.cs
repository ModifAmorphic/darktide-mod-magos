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

    // ---- Preferences section (Phase 3 Track D) -----------------------------

    [Fact]
    public void Load_yields_default_preferences_when_section_is_absent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "magos-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """{ "Logging": { "Level": "Debug" } }""");

        try
        {
            var prefs = new ConfigLoader(configPath).Load().Preferences;

            Assert.Equal(ThemeMode.System, prefs.Theme);
            Assert.Equal(1.0, prefs.FontScale);
            Assert.Equal("en", prefs.Language);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_applies_json_overrides_onto_default_preferences()
    {
        var dir = Path.Combine(Path.GetTempPath(), "magos-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Preferences": {
                "Theme": "Dark",
                "FontScale": 1.25,
                "Language": "fr"
              }
            }
            """);

        try
        {
            var prefs = new ConfigLoader(configPath).Load().Preferences;

            Assert.Equal(ThemeMode.Dark, prefs.Theme);
            Assert.Equal(1.25, prefs.FontScale);
            Assert.Equal("fr", prefs.Language);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_round_trips_preferences_through_a_subsequent_Load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "magos-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);
            var config = MagosConfig.CreateDefault();
            config.Preferences.Theme = ThemeMode.Dark;
            config.Preferences.FontScale = 1.5;
            config.Preferences.Language = "fr";

            loader.Save(config);
            var reloaded = loader.Load();

            Assert.Equal(ThemeMode.Dark, reloaded.Preferences.Theme);
            Assert.Equal(1.5, reloaded.Preferences.FontScale);
            Assert.Equal("fr", reloaded.Preferences.Language);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_creates_the_parent_directory_when_missing()
    {
        // First-run case: neither the config dir nor the file exist yet. Save
        // must create the parent dir + write the file (so the next Load sees it).
        var dir = Path.Combine(Path.GetTempPath(), "magos-cfg-" + Guid.NewGuid());
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);

            loader.Save(MagosConfig.CreateDefault());

            Assert.True(File.Exists(configPath));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Save_preserves_the_other_sections_alongside_preferences()
    {
        // Save writes the whole MagosConfig back; verify it does not drop a
        // previously-set field from a sibling section.
        var dir = Path.Combine(Path.GetTempPath(), "magos-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");
        File.WriteAllText(configPath, """
            {
              "Logging": { "Level": "Warning" },
              "EnginseerRuntimeDir": "/custom/runtime"
            }
            """);

        try
        {
            var loader = new ConfigLoader(configPath);
            var config = loader.Load();
            config.Preferences.Theme = ThemeMode.Light;

            loader.Save(config);
            var reloaded = loader.Load();

            Assert.Equal("Warning", reloaded.Logging.Level);
            Assert.Equal("/custom/runtime", reloaded.EnginseerRuntimeDir);
            Assert.Equal(ThemeMode.Light, reloaded.Preferences.Theme);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_persists_the_theme_enum_as_a_human_readable_string()
    {
        // The JSON-enum converter writes names (not numbers), so the persisted
        // file is human-readable + stable across enum renumbering.
        var dir = Path.Combine(Path.GetTempPath(), "magos-cfg-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "config.json");

        try
        {
            var loader = new ConfigLoader(configPath);
            var config = MagosConfig.CreateDefault();
            config.Preferences.Theme = ThemeMode.Dark;

            loader.Save(config);

            var json = File.ReadAllText(configPath);
            Assert.Contains("\"theme\": \"dark\"", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
