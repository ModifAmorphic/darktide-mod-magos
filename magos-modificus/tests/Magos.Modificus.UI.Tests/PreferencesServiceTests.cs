using System.Globalization;
using Magos.Modificus.Config;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Preferences;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// <see cref="PreferencesService"/>: applying a triple mirrors it into the
/// loaded <see cref="MagosConfig"/> singleton, persists via the config loader,
/// and switches the <see cref="LocalizationService"/> culture. The theme +
/// font-scale apply paths target Avalonia's <c>Application.Current</c>, which is
/// <c>null</c> outside a running app; both are guarded no-ops in that case
/// (the test asserts what is testable here; the visual apply is exercised by
/// the operator's visual review).
/// </summary>
public sealed class PreferencesServiceTests
{
    private static readonly LocalizationService Localization = new();

    private static (PreferencesService svc, FakeConfigLoader loader, MagosConfig config) Build()
    {
        var config = MagosConfig.CreateDefault();
        var loader = new FakeConfigLoader { Config = config };
        var svc = new PreferencesService(
            config,
            loader,
            Localization,
            NullLogger<PreferencesService>.Instance);
        return (svc, loader, config);
    }

    [Fact]
    public void ApplyAndPersist_switches_the_localization_culture()
    {
        var (svc, _, _) = Build();

        svc.ApplyAndPersist(ThemeMode.Dark, 1.0, "fr");

        Assert.Equal("fr", Localization.Culture.Name);
    }

    [Fact]
    public void ApplyAndPersist_blank_language_resolves_to_invariant()
    {
        var (svc, _, _) = Build();

        svc.ApplyAndPersist(ThemeMode.System, 1.0, "");

        Assert.Equal(CultureInfo.InvariantCulture, Localization.Culture);
    }

    [Fact]
    public void ApplyAndPersist_unknown_language_does_not_throw()
    {
        // CultureInfo.GetCultureInfo behavior for unknown names varies by
        // platform (Linux ICU may synthesize a culture instead of throwing);
        // the contract is the service never crashes on a bad language and the
        // indexer keeps resolving to the neutral resx.
        var (svc, _, _) = Build();

        var ex = Record.Exception(() => svc.ApplyAndPersist(ThemeMode.System, 1.0, "xx-XX"));

        Assert.Null(ex);
    }

    [Fact]
    public void ApplyAndPersist_mirrors_the_values_into_the_loaded_config()
    {
        var (svc, _, config) = Build();

        svc.ApplyAndPersist(ThemeMode.Light, 1.25, "fr");

        Assert.Equal(ThemeMode.Light, config.Preferences.Theme);
        Assert.Equal(1.25, config.Preferences.FontScale);
        Assert.Equal("fr", config.Preferences.Language);
    }

    [Fact]
    public void ApplyAndPersist_persists_via_the_config_loader()
    {
        var (svc, loader, config) = Build();

        svc.ApplyAndPersist(ThemeMode.Dark, 1.5, "en");

        Assert.Equal(1, loader.SaveCalls);
        Assert.Same(config, loader.LastSaved);
    }

    [Fact]
    public void ApplyAndPersist_does_not_throw_when_application_current_is_null()
    {
        // Unit tests run without an Avalonia Application; the theme + font-scale
        // apply paths must guard against that (the in-memory + persistence paths
        // still run).
        var (svc, loader, _) = Build();

        var ex = Record.Exception(() => svc.ApplyAndPersist(ThemeMode.Dark, 1.5, "en"));

        Assert.Null(ex);
        Assert.Equal(1, loader.SaveCalls);
    }

    [Fact]
    public void Base_font_size_constants_match_the_design_intent()
    {
        // The body + status font sizes are the unscaled anchors the
        // PreferencesService multiplies by the user's font scale. They back the
        // AppFontSize / AppStatusFontSize resources; pinning them guards the
        // status-strip (12) staying a step below the body (14).
        Assert.Equal(14.0, PreferencesService.BaseFontSize);
        Assert.Equal(12.0, PreferencesService.BaseStatusFontSize);
    }
}
