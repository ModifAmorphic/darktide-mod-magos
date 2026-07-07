using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Settings;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the discovery escape-hatch modal
/// (<see cref="Views.DiscoveryEscapeHatchDialog"/>). Shown when a launch returns
/// <c>LaunchStatus.DiscoveryIncomplete</c>: a focused form that prompts for
/// <em>only</em> the missing fields (the ones <c>LaunchResult</c> listed), with
/// the same shared <see cref="DiscoveryField"/> descriptor the Settings window
/// uses. Submit does one read-modify-save through <see cref="IConfigLoader"/>
/// writing all entered paths into <see cref="DiscoveryConfig"/>; then the dialog
/// closes. There is <b>no auto-retry</b>: the user clicks Launch again to retry
/// (avoids a loop if the entered paths still do not work). Cancel aborts.
/// </summary>
/// <remarks>
/// <para><b>Rows vs. write-through:</b> unlike the Settings VM (which writes
/// each field immediately on change), this VM stages the entered values on the
/// rows and writes them all on submit. A focused escape-hatch is one decision
/// ("here are all the missing paths"), not a series of independent edits, so a
/// single commit fits the UX better.</para>
/// <para><b>Pre-fill:</b> each row is pre-filled with the current override from
/// config (if any). For a missing field the auto-discovered value was null, so
/// the override is usually empty too; but a previously-set override that turned
/// out wrong is shown so the user can edit rather than re-type the whole
/// path.</para>
/// <para><b>Unknown fields are dropped:</b> if <c>LaunchResult</c> ever lists a
/// field name the catalog does not know (a future field), it is silently
/// omitted; the dialog always renders the fields it knows how to label +
/// browse.</para>
/// </remarks>
public partial class DiscoveryEscapeHatchViewModel : ObservableObject
{
    private readonly IConfigLoader _configLoader;
    private readonly LocalizationService _localization;

    /// <param name="missingFields">The discovery field names the launch result
    /// reported missing (the values of <c>LaunchResult.MissingDiscoveryFields</c>,
    /// which match the <see cref="Steam.DiscoveryResult"/> property names).
    /// Empty yields no rows (the dialog should not be shown then anyway).</param>
    /// <param name="configLoader">The live config reader/writer. Submit does one
    /// read-modify-save through this.</param>
    /// <param name="localization">The localization service; handed to each row so
    /// its label resolves + refreshes on a culture change.</param>
    public DiscoveryEscapeHatchViewModel(
        IReadOnlyList<string> missingFields,
        IConfigLoader configLoader,
        LocalizationService localization)
    {
        _configLoader = configLoader;
        _localization = localization;

        var discovery = _configLoader.Load().Discovery;

        // Resolve each missing field name to its catalog descriptor (drop
        // unknowns), then order by the catalog's canonical order (DiscoveryFields.All)
        // so the rows are top-to-bottom Steam, Darktide, compatdata, Proton
        // regardless of the order LaunchResult happened to list them in.
        Rows = new ObservableCollection<DiscoveryFieldRowViewModel>(
            missingFields
                .Select(DiscoveryFields.Find)
                .Where(f => f is not null)
                .Cast<DiscoveryField>()
                .OrderBy(f => CatalogIndex(f))
                .Select(field => new DiscoveryFieldRowViewModel(
                    field,
                    InitialValue(field, discovery),
                    _localization)));

        _localization.PropertyChanged += OnCultureChanged;
    }

    /// <summary>
    /// The rows for the missing fields only (in catalog order, which is the
    /// order <see cref="DiscoveryFields.All"/> lists). Bound to an
    /// <c>ItemsControl</c>; each row's Browse button is wired by the view.
    /// </summary>
    public ObservableCollection<DiscoveryFieldRowViewModel> Rows { get; }

    /// <summary>The localized header (the friendly "couldn't discover everything"
    /// message). Re-resolves on a culture change.</summary>
    public string Header => _localization["EscapeHatch_Header"];

    /// <summary>The localized "click Launch to retry" hint. Re-resolves on a
    /// culture change.</summary>
    public string RetryHint => _localization["EscapeHatch_RetryHint"];

    /// <summary>
    /// The outcome of the dialog: <c>true</c> when the user submitted (the
    /// entered paths are now persisted), <c>false</c> when they cancelled (no
    /// writes). Read by the dialog service after <c>ShowDialog</c> returns.
    /// </summary>
    public bool Result { get; private set; }

    /// <summary>
    /// Detaches the VM's culture subscription + each row's, so the short-lived
    /// dialog VM is collectable after its window closes. Called by the dialog on
    /// close.
    /// </summary>
    public void Detach()
    {
        _localization.PropertyChanged -= OnCultureChanged;
        foreach (var row in Rows)
        {
            row.Detach();
        }
    }

    /// <summary>
    /// Re-fires the localized derived strings (header + retry hint) on a culture
    /// change. The per-row labels refresh themselves.
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.Culture) or "Item[]"))
        {
            return;
        }

        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(RetryHint));
    }

    /// <summary>
    /// Submit: one read-modify-save writing every row's value into the matching
    /// <c>User*Path</c> in <see cref="DiscoveryConfig"/> (an empty value clears
    /// the override -> auto-discover), then marks <see cref="Result"/> true. The
    /// dialog closes on a true result (the view reads it after the command
    /// runs). No auto-retry: the caller (the shell) does not re-launch; the user
    /// clicks Launch again.
    /// </summary>
    [RelayCommand]
    private void Submit()
    {
        var config = _configLoader.Load();
        foreach (var row in Rows)
        {
            var value = row.Value;
            var written = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            SetOverride(config.Discovery, row.Field.FieldName, written);
        }
        _configLoader.Save(config);

        Result = true;
        OnPropertyChanged(nameof(Result));
    }

    /// <summary>
    /// Cancel: marks <see cref="Result"/> false so the dialog closes without a
    /// write. The shell sees <c>false</c> and aborts (no retry).
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        Result = false;
        OnPropertyChanged(nameof(Result));
    }

    /// <summary>
    /// The catalog position of a field (the index in
    /// <see cref="DiscoveryFields.All"/>). Used to order the rows in the
    /// canonical top-to-bottom render order regardless of the input order. A
    /// field not in the catalog (defensive: never happens after the
    /// <see cref="DiscoveryFields.Find"/> filter) sorts last.
    /// </summary>
    private static int CatalogIndex(DiscoveryField field)
    {
        for (var i = 0; i < DiscoveryFields.All.Count; i++)
        {
            if (ReferenceEquals(DiscoveryFields.All[i], field)
                || string.Equals(DiscoveryFields.All[i].FieldName, field.FieldName, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return int.MaxValue;
    }

    /// <summary>
    /// Maps a discovery field's canonical name to its setter on
    /// <see cref="DiscoveryConfig"/>. Mirrors the Settings VM's helper; kept
    /// duplicated to keep the two VMs decoupled (the alternative, a shared
    /// helper, would couple the escape-hatch to the Settings VM, which is a
    /// worse trade than the small duplication).
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
    /// it is null/whitespace). Mirrors the Settings VM's helper.
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
}
