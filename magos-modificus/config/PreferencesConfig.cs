namespace Magos.Modificus.Config;

/// <summary>
/// User-facing global preferences (the "Preferences" dialog): the UI theme,
/// the font-scale multiplier, and the display language. Bound from the
/// <c>Preferences</c> section of <see cref="MagosConfig"/> by the config loader
/// in <c>Magos.Modificus.General</c>, and persisted back through
/// <c>ConfigLoader.Save</c> when the user changes a value in the dialog.
/// Every field carries a default so an absent section yields a usable object
/// (first-run safe).
/// </summary>
/// <remarks>
/// <para><b>Theme:</b> Dark / Light / System (System follows the OS theme and
/// is the default).</para>
/// <para><b>FontScale:</b> a continuous multiplier applied to the base UI font,
/// so a slider can scale the whole UI (0.8 to 1.5 typical). 1.0 = no scaling.</para>
/// <para><b>Language:</b> a culture name (e.g. <c>en</c>, <c>fr</c>). English
/// ships; the selector + culture switching are in place, real translations are
/// content added later via translated resx files. Empty / <c>"en"</c> = neutral.</para>
/// </remarks>
public sealed class PreferencesConfig
{
    /// <summary>
    /// The UI theme variant. <see cref="ThemeMode.System"/> follows the OS.
    /// Defaults to <see cref="ThemeMode.System"/>.
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>
    /// The UI font-scale multiplier (1.0 = no scaling). The Preferences dialog
    /// exposes this as a percent slider; the persisted value is the raw double
    /// (e.g. 1.25 for 125%).
    /// </summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>
    /// The display language as a culture name (e.g. <c>en</c>, <c>fr</c>).
    /// Empty or <c>en</c> resolves to the neutral English resources. Switching
    /// this at runtime updates the live UI through the LocalizationService.
    /// </summary>
    public string Language { get; set; } = "en";
}

/// <summary>
/// The UI theme variant, matching <c>Avalonia.Styling.ThemeVariant</c>.
/// <see cref="System"/> follows the OS theme (Avalonia's
/// <c>ThemeVariant.Default</c>).
/// </summary>
public enum ThemeMode
{
    /// <summary>Follow the OS theme (Avalonia's ThemeVariant.Default).</summary>
    System = 0,

    /// <summary>The dark theme (Avalonia's ThemeVariant.Dark).</summary>
    Dark = 1,

    /// <summary>The light theme (Avalonia's ThemeVariant.Light).</summary>
    Light = 2,
}
