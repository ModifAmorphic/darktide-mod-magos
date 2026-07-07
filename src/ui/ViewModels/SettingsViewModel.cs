using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Settings;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the Settings modal (<see cref="Views.SettingsWindow"/>).
/// Two sections:
/// <list type="bullet">
    /// <item><description><b>Discovery:</b> the user-override paths
    /// (<c>UserSteamInstallPath</c>, <c>UserDarktideGameBinaryPath</c>,
    /// <c>UserCompatdataPath</c>, <c>UserProtonBinaryPath</c>), platform-gated
    /// so Windows renders only the Steam install + Darktide binary rows (the
    /// compatdata + Proton rows are Linux-only: <c>WindowsLaunchStrategy</c>
    /// ignores them, so they would be silently ineffective on Windows). Each
    /// row's TextBox is pre-filled with the current override (or empty when the
    /// field is set to auto-discover). Editing writes the override immediately
    /// via a read-modify-save through <see cref="IConfigLoader"/> (the Track D
    /// Preferences pattern: apply + persist per change). An empty TextBox
    /// clears the override (writes <c>null</c>, so the field falls back to
    /// auto-discovery).</description></item>
/// <item><description><b>Storage:</b> the mod-repository location
/// (<c>ModsFolder</c>). Read-only on open (pre-filled from config); Browse opens
/// a folder picker, and a change runs the relocate flow as a single atomic call:
/// <c>repo.Relocate(new)</c> owns the move + config save + rescan (rolling back
/// the move on save failure). Failures (invalid path, conflicting UUID, IO
/// error, save failure) surface as a <see cref="StatusMessage"/> under the
/// field.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Never holds a cached <see cref="CuratorConfig"/>:</b> each field change
/// calls <see cref="IConfigLoader.Load"/> + <see cref="IConfigLoader.Save"/> (a
/// read-modify-save), so concurrent edits by other surfaces (the escape-hatch)
/// are never clobbered. The config file is tiny; the round-trip is cheap.</para>
/// <para><b>The browse buttons:</b> opening a storage-provider picker is a view
/// concern (it needs the live TopLevel), so the view code-behind opens the
/// picker and either sets the row's <c>Value</c> (discovery) or calls
/// <see cref="ApplyModsFolderCommand"/> (storage). No file paths cross the VM
/// boundary except the picked path itself.</para>
/// </remarks>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigLoader _configLoader;
    private readonly IModRepository _repo;
    private readonly LocalizationService _localization;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>
    /// True during the initial restore (constructing the VM pre-fills the bound
    /// controls from the current config without re-writing each field back). The
    /// values already match what is persisted, so re-writing would be a noisy
    /// no-op.
    /// </summary>
    private bool _suppressApply;

    /// <summary>
    /// Creates the Settings VM, pre-fills the discovery rows (platform-gated:
    /// Steam install + Darktide binary on Windows; all four on Linux) + the
    /// ModsFolder TextBox from the live config, and wires each discovery row's
    /// change callback to the write-through path.
    /// </summary>
    /// <param name="configLoader">The live config reader/writer. Each field change
    /// does a read-modify-save through this.</param>
    /// <param name="repo">The mod repository, used for the relocate flow on a
    /// ModsFolder change.</param>
    /// <param name="localization">The localization service; handed to each
    /// discovery row so its label resolves + refreshes on a culture change.</param>
    /// <param name="logger">Logger for the relocate flow.</param>
    public SettingsViewModel(
        IConfigLoader configLoader,
        IModRepository repo,
        LocalizationService localization,
        ILogger<SettingsViewModel> logger)
    {
        _configLoader = configLoader;
        _repo = repo;
        _localization = localization;
        _logger = logger;

        _suppressApply = true;
        try
        {
            var discovery = _configLoader.Load().Discovery;

            // Each discovery row's change callback is the write-through: the
            // value lands in config (read-modify-save) the moment the TextBox
            // edits (or the Browse picker sets it). Suppressed only during the
            // initial restore, which is what pre-fills the boxes.
            //
            // Platform-gated: on Windows the compatdata + Proton overrides are
            // Linux-only (WindowsLaunchStrategy.ComputeStatus never reads them,
            // so surfacing them would be silently ineffective rows). Only the
            // Steam install + Darktide binary rows render on Windows. The
            // escape-hatch is already correct (it renders only the names in
            // LaunchResult.MissingDiscoveryFields, which on Windows never
            // includes the Linux-only ones).
            DiscoveryRows = new ObservableCollection<DiscoveryFieldRowViewModel>(
                DiscoveryFields.All
                    .Where(field => OperatingSystem.IsLinux() || !IsLinuxOnlyField(field))
                    .Select(field => new DiscoveryFieldRowViewModel(
                        field,
                        InitialValue(field, discovery),
                        _localization,
                        onValueChanged: WriteThroughDiscovery)));
        }
        finally
        {
            _suppressApply = false;
        }

        // Subscribe for the localized section headers + the status message
        // (which embeds a resx key when set); the row VMs each subscribe on
        // their own.
        _localization.PropertyChanged += OnCultureChanged;

        ModsFolder = _configLoader.Load().ModsFolder;
    }

    /// <summary>
    /// The discovery-field rows (Steam install, Darktide binary, compatdata,
    /// Proton binary), platform-gated: Windows renders only the first two (the
    /// compatdata + Proton rows are Linux-only). Bound to an
    /// <c>ItemsControl</c> in the view; each row owns its TextBox value + Browse
    /// button (the browse kind drives which picker opens).
    /// </summary>
    public ObservableCollection<DiscoveryFieldRowViewModel> DiscoveryRows { get; }

    /// <summary>
    /// The mod-repository location (<c>ModsFolder</c>). Pre-filled from config;
    /// editable only through the Browse button (the TextBox is read-only in the
    /// view to prevent free-text entry of an invalid path). A change is applied
    /// via <see cref="ApplyModsFolderCommand"/>, which runs the relocate flow.
    /// </summary>
    [ObservableProperty]
    private string _modsFolder = string.Empty;

    /// <summary>
    /// The localized header for the discovery section. Re-resolves on a culture
    /// change.
    /// </summary>
    public string DiscoverySectionHeader => _localization["Settings_DiscoverySection"];

    /// <summary>
    /// The localized header for the storage section. Re-resolves on a culture
    /// change.
    /// </summary>
    public string StorageSectionHeader => _localization["Settings_StorageSection"];

    /// <summary>
    /// The localized label for the ModsFolder row. Re-resolves on a culture
    /// change.
    /// </summary>
    public string ModsFolderLabel => _localization["Settings_ModRepoLabel"];

    /// <summary>
    /// A non-blocking status message surfaced under the ModsFolder row (red,
    /// visible only when non-empty). Set when the relocate flow fails; cleared
    /// on a successful relocate or when the user clears the field. The text is
    /// the raw exception message (no localization): it is operator-facing
    /// diagnostic detail, not user copy.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Detaches the VM's culture subscription + each row's, so the short-lived
    /// dialog VM is collectable after its window closes (the localization service
    /// is a singleton that outlives the dialog). Called by the Settings window
    /// on close.
    /// </summary>
    public void Detach()
    {
        _localization.PropertyChanged -= OnCultureChanged;
        foreach (var row in DiscoveryRows)
        {
            row.Detach();
        }
    }

    /// <summary>
    /// Re-fires the localized derived strings (section headers + the ModsFolder
    /// label) on a culture change. The per-row labels refresh themselves (each
    /// row subscribes on its own).
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.Culture) or "Item[]"))
        {
            return;
        }

        OnPropertyChanged(nameof(DiscoverySectionHeader));
        OnPropertyChanged(nameof(StorageSectionHeader));
        OnPropertyChanged(nameof(ModsFolderLabel));
    }

    /// <summary>
    /// The relocate flow as a single atomic call: <c>repo.Relocate(newPath)</c>
    /// owns the move (every container directory from the current mods root to
    /// the new one), the config save (<c>ModsFolder = newPath</c>), and the
    /// rescan. On any failure (invalid path, conflicting UUID, IO error, or a
    /// save failure that the repository rolls back), the exception message is
    /// surfaced via <see cref="StatusMessage"/> and the ModsFolder TextBox is
    /// left as-is so the user can see the prior value. A successful relocate
    /// clears <see cref="StatusMessage"/> + updates the TextBox.
    /// </summary>
    /// <param name="newPath">The absolute path the user picked. Caller (the
    /// view) guarantees non-null/non-empty; the repo also validates.</param>
    [RelayCommand]
    private void ApplyModsFolder(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath))
        {
            return;
        }

        // Same-path is a no-op (the repo also short-circuits). Skipping early
        // avoids a confusing save + rescan when the user re-picks the current
        // location.
        if (string.Equals(ModsFolder, newPath, StringComparison.Ordinal))
        {
            StatusMessage = null;
            return;
        }

        try
        {
            // Atomic: the repo owns move + save + rescan (rolling the move back
            // on save failure). See IModRepository.Relocate.
            _repo.Relocate(newPath);

            ModsFolder = newPath;
            StatusMessage = null;

            _logger.LogInformation("Relocated mod repository to {Path}.", newPath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            // Relocate failed (invalid path / conflict / IO / rolled-back save
            // failure). Surface the message; the ModsFolder TextBox keeps its
            // prior value, so the user sees what is currently in effect.
            StatusMessage = ex.Message;
            _logger.LogWarning(ex, "Mod repository relocate to {Path} failed.", newPath);
        }
    }

    /// <summary>
    /// The write-through for a discovery field change: read-modify-save the
    /// matching <c>User*Path</c> in <see cref="DiscoveryConfig"/>. An empty /
    /// whitespace value writes <c>null</c> (clears the override -> auto-discover).
    /// Suppressed during the initial restore.
    /// </summary>
    private void WriteThroughDiscovery(DiscoveryFieldRowViewModel row)
    {
        if (_suppressApply)
        {
            return;
        }

        var value = row.Value;
        var written = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var config = _configLoader.Load();
        SetOverride(config.Discovery, row.Field.FieldName, written);
        _configLoader.Save(config);
    }

    /// <summary>
    /// Maps a discovery field's canonical name to its setter on
    /// <see cref="DiscoveryConfig"/>. The name matches the property names of
    /// <see cref="Steam.DiscoveryResult"/> (the same names that flow through
    /// <c>LaunchResult.MissingDiscoveryFields</c>).
    /// </summary>
    private static void SetOverride(DiscoveryConfig discovery, string fieldName, string? value)
    {
        switch (fieldName)
        {
            case "SteamInstallPath":
                discovery.UserSteamInstallPath = value;
                return;
            case "DarktideGameBinaryPath":
                discovery.UserDarktideGameBinaryPath = value;
                return;
            case "CompatdataPath":
                discovery.UserCompatdataPath = value;
                return;
            case "ProtonBinaryPath":
                discovery.UserProtonBinaryPath = value;
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Reads the current override value for a field from config (or empty when
    /// it is null/whitespace, i.e. set to auto-discover). Used to pre-fill each
    /// row at construction.
    /// </summary>
    private static string InitialValue(DiscoveryField field, DiscoveryConfig discovery) =>
        field.FieldName switch
        {
            "SteamInstallPath" => discovery.UserSteamInstallPath ?? string.Empty,
            "DarktideGameBinaryPath" => discovery.UserDarktideGameBinaryPath ?? string.Empty,
            "CompatdataPath" => discovery.UserCompatdataPath ?? string.Empty,
            "ProtonBinaryPath" => discovery.UserProtonBinaryPath ?? string.Empty,
            _ => string.Empty,
        };

    /// <summary>
    /// Whether a discovery field is Linux-only (the compatdata + Proton
    /// overrides, which <c>WindowsLaunchStrategy</c> ignores). Used to
    /// platform-gate the Settings rows so Windows does not surface
    /// silently-ineffective rows. The catalog is the single source of truth for
    /// field identity; this helper is the only place that knows which of those
    /// fields are Linux-scoped.
    /// </summary>
    private static bool IsLinuxOnlyField(DiscoveryField field) =>
        field.FieldName is "CompatdataPath" or "ProtonBinaryPath";
}
