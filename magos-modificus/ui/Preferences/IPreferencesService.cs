using Avalonia;
using Avalonia.Styling;
using Magos.Modificus.Config;
using Magos.Modificus.General;
using Magos.Modificus.UI.Localization;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI.Preferences;

/// <summary>
/// The single authority for applying user-facing preferences (theme, font scale,
/// language) to the running app and persisting them to <see cref="MagosConfig"/>.
/// The composition root applies the loaded config at startup; the
/// <c>PreferencesViewModel</c> calls <see cref="ApplyAndPersist"/> on each
/// change. All three concerns (theme variant, global font scale, UI culture)
/// live behind one method so the values stay consistent: nothing else in the UI
/// touches <c>RequestedThemeVariant</c>, the font-scale resource, or
/// <see cref="LocalizationService.Culture"/> directly.
/// </summary>
public interface IPreferencesService
{
    /// <summary>
    /// Applies <paramref name="theme"/> / <paramref name="fontScale"/> /
    /// <paramref name="language"/> to the running app (theme variant, global
    /// font scale, UI culture), writes them into the loaded
    /// <see cref="MagosConfig"/> singleton's <see cref="MagosConfig.Preferences"/>,
    /// and persists it via <see cref="IConfigLoader"/>.<c>Save</c>. Safe to call
    /// at startup (the values may match the loaded config, which is a no-op apply).
    /// </summary>
    void ApplyAndPersist(ThemeMode theme, double fontScale, string language);
}

/// <summary>
/// Default <see cref="IPreferencesService"/>. Applies the theme via
/// <see cref="Application.RequestedThemeVariant"/>, the font scale via an
/// application-level <c>AppFontSize</c> resource that a Window style binds to
/// (cascading to all controls through inheritance + DynamicResource), and the
/// language via <see cref="LocalizationService"/>.<see cref="LocalizationService.SetCulture"/>.
/// </summary>
public sealed class PreferencesService : IPreferencesService
{
    /// <summary>
    /// The base (unscaled) UI font size in pixels. The applied value is
    /// <see cref="BaseFontSize"/> * <c>FontScale</c>; the resource key
    /// <c>AppFontSize</c> is read by the Window style in App.axaml.
    /// </summary>
    public const double BaseFontSize = 14.0;

    private const string AppFontSizeResourceKey = "AppFontSize";

    private readonly MagosConfig _config;
    private readonly IConfigLoader _configLoader;
    private readonly LocalizationService _localization;
    private readonly ILogger<PreferencesService> _logger;

    public PreferencesService(
        MagosConfig config,
        IConfigLoader configLoader,
        LocalizationService localization,
        ILogger<PreferencesService> logger)
    {
        _config = config;
        _configLoader = configLoader;
        _localization = localization;
        _logger = logger;
    }

    /// <inheritdoc />
    public void ApplyAndPersist(ThemeMode theme, double fontScale, string language)
    {
        // 1. Apply to the running app.
        ApplyTheme(theme);
        ApplyFontScale(fontScale);
        ApplyLanguage(language);

        // 2. Mirror into the loaded config singleton (so anything else reading
        //    MagosConfig.Preferences sees the new value this session).
        _config.Preferences.Theme = theme;
        _config.Preferences.FontScale = fontScale;
        _config.Preferences.Language = language;

        // 3. Persist (best-effort; ConfigLoader.Save swallows write errors).
        _configLoader.Save(_config);

        _logger.LogInformation(
            "Preferences applied + persisted: theme={Theme}; fontScale={FontScale:F2}; language={Language}",
            theme,
            fontScale,
            language);
    }

    /// <summary>
    /// Sets <see cref="Application.RequestedThemeVariant"/> from
    /// <see cref="ThemeMode"/>. <see cref="ThemeMode.System"/> maps to
    /// <see cref="ThemeVariant.Default"/> (follow the OS).
    /// </summary>
    private static void ApplyTheme(ThemeMode theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = theme switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>
    /// Publishes the scaled font size as the <c>AppFontSize</c> application
    /// resource. The Window style in App.axaml binds <c>Window.FontSize</c> to
    /// it (DynamicResource), so all open windows (and their inheriting children)
    /// re-resolve when the resource changes. Non-finite or non-positive scales
    /// fall back to <see cref="BaseFontSize"/> (graceful: a bad value does not
    /// collapse the UI).
    /// </summary>
    private static void ApplyFontScale(double fontScale)
    {
        if (Application.Current is null)
        {
            return;
        }

        var scale = double.IsFinite(fontScale) && fontScale > 0 ? fontScale : 1.0;
        Application.Current.Resources[AppFontSizeResourceKey] = BaseFontSize * scale;
    }

    /// <summary>
    /// Switches the UI culture through <see cref="LocalizationService"/>, which
    /// raises the change event so every <c>{Binding [Key]}</c> refreshes
    /// (dynamic-language path).
    /// </summary>
    private void ApplyLanguage(string language) => _localization.SetCulture(language);
}
