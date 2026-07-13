using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the per-mod import modal (<see cref="Views.ImportModDialog"/>).
/// Collects the mod's source provenance (Local / Nexus) + a raw version tag + a
/// URL for the remote source, validates them, and yields an
/// <see cref="ImportModResult"/> on confirm (the URL is parsed to canonical
/// identity via <see cref="ModSourceParser"/>). A cancel yields <c>null</c>.
/// </summary>
/// <remarks>
/// <para><b>One modal per imported path:</b> the mod-list add flow (picker +
/// drag-and-drop) shows this modal sequentially, once per path. The
/// <see cref="ImportModRequest.ModName"/> is pre-filled from the folder / archive
/// stem and is editable; the edited name becomes the canonical mod-store key
/// (the import service upserts).</para>
/// <para><b>Source chooser:</b> a ComboBox over <see cref="ImportSource"/>
/// (Untracked / Nexus). Switching it shows / hides the conditional fields:
/// Nexus requires a Version tag + a URL that parses (the user supplies the
/// release tag; the modal does not fetch it from the remote); Untracked needs
/// nothing extra (the mod imports as <see cref="UntrackedSource"/> with an empty
/// version).</para>
/// <para><b>URL field:</b> Nexus shows one <see cref="Url"/> entry whose label +
/// placeholder adapt to the Nexus source. Untracked needs nothing extra.</para>
/// <para><b>Validation never throws:</b> <see cref="ModSourceParser"/> returns
/// <c>false</c> on malformed input; the modal surfaces a validation message and
/// keeps OK disabled. All logic lives here (unit-tested); the dialog only wires
/// buttons to <see cref="ConfirmCommand"/> / close.</para>
/// </remarks>
public partial class ImportModViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly ImportModRequest _request;

    /// <summary>
    /// Creates the modal VM from the add-flow request. The mod name is pre-filled
    /// from the request (folder / archive stem) and editable; the source defaults
    /// to <see cref="ImportSource.Nexus"/> (most Darktide mods ship on Nexus, so
    /// the common case is a Nexus URL + a release tag; the user can switch to
    /// Untracked when needed).
    /// </summary>
    public ImportModViewModel(ImportModRequest request, LocalizationService localization)
    {
        _localization = localization;
        _request = request;
        _modName = request.ModName;
        _sourceChoice = ImportSource.Nexus;
    }

    /// <summary>
    /// The source provenance options offered by the modal's ComboBox.
    /// </summary>
    public enum ImportSource
    {
        /// <summary>Untracked import (no remote identity, no version).</summary>
        Untracked,

        /// <summary>Nexus Mods (collects a mod URL parsed to a mod id).</summary>
        Nexus,
    }

    /// <summary>
    /// The version-policy options offered by the modal's ComboBox. Latest tracks
    /// the container's newest release (auto-update on re-import); Pinned freezes
    /// the profile entry to the version being imported (the opaque version id is
    /// filled in by the add flow after Import returns it, since the modal cannot
    /// know the id ahead of time).
    /// </summary>
    public enum ImportPolicyChoice
    {
        /// <summary>Track the container's newest release (the default).</summary>
        Latest,

        /// <summary>Pin the profile entry to the version being imported.</summary>
        Pinned,
    }

    /// <summary>
    /// The mod name (editable; pre-filled from the folder / archive stem). The
    /// mod-store key + the on-disk folder name; an edited name becomes the
    /// canonical key (the import service upserts).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _modName;

    /// <summary>
    /// The chosen source. Drives which conditional fields show (Nexus:
    /// Version + URL; Untracked: nothing) and which validation applies.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemote))]
    [NotifyPropertyChangedFor(nameof(IsVersionVisible))]
    [NotifyPropertyChangedFor(nameof(SourceChoiceIndex))]
    [NotifyPropertyChangedFor(nameof(UrlLabel))]
    [NotifyPropertyChangedFor(nameof(UrlPlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(UrlValidationMessage))]
    [NotifyPropertyChangedFor(nameof(VersionValidationMessage))]
    private ImportSource _sourceChoice;

    /// <summary>
    /// Integer adapter for the source ComboBox's <c>SelectedIndex</c> (0 = Local,
    /// 1 = Nexus), so the ComboBox binds two-way without a converter or view
    /// code-behind. Maps to / from <see cref="SourceChoice"/>.
    /// </summary>
    public int SourceChoiceIndex
    {
        get => (int)SourceChoice;
        set
        {
            var choice = (ImportSource)value;
            if (choice != SourceChoice)
            {
                SourceChoice = choice;
            }
        }
    }

    /// <summary>
    /// The raw release tag string (e.g. <c>"1.2"</c>). Required for Nexus
    /// (the user enters the release tag; the modal does not fetch it from the remote); ignored
    /// for Local, which records an empty version. An empty version is recorded as
    /// <c>""</c>. Never parsed or normalized at this layer.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(IsVersionVisible))]
    [NotifyPropertyChangedFor(nameof(VersionValidationMessage))]
    private string _version = string.Empty;

    /// <summary>
    /// The remote source URL (shown for Nexus). Parsed to canonical identity on
    /// confirm. Ignored for Local.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(UrlValidationMessage))]
    private string _url = string.Empty;

    /// <summary>
    /// The version-policy choice (Latest or Pinned). Drives which
    /// <see cref="ModVersionPolicy"/> the profile entry adopts after import.
    /// Default <see cref="ImportPolicyChoice.Latest"/> (the common case: track
    /// the newest release). Pinned is meaningful when the user wants to freeze
    /// the mod at exactly the version they are importing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Policy))]
    private ImportPolicyChoice _policyChoice = ImportPolicyChoice.Latest;

    /// <summary>
    /// Integer adapter for the policy ComboBox's <c>SelectedIndex</c>
    /// (0 = Latest, 1 = Pinned), so the ComboBox binds two-way without a
    /// converter or view code-behind. Maps to / from <see cref="PolicyChoice"/>.
    /// </summary>
    public int PolicyChoiceIndex
    {
        get => (int)PolicyChoice;
        set
        {
            var choice = (ImportPolicyChoice)value;
            if (choice != PolicyChoice)
            {
                PolicyChoice = choice;
            }
        }
    }

    /// <summary>
    /// The derived <see cref="ModVersionPolicy"/> for the chosen
    /// <see cref="PolicyChoice"/>. For Latest this is
    /// <see cref="LatestPolicy"/>; for Pinned this is a placeholder
    /// <see cref="PinnedPolicy"/> with an empty
    /// <see cref="PinnedPolicy.VersionId"/> (the add flow substitutes the
    /// opaque version id returned by <c>IModImportService.Import</c> after the
    /// container/version is created; the modal cannot know the id ahead of
    /// time).
    /// </summary>
    public ModVersionPolicy Policy => PolicyChoice == ImportPolicyChoice.Pinned
        ? new PinnedPolicy()
        : ModVersionPolicy.Latest;

    /// <summary>The outcome of a confirmed modal; <c>null</c> until confirm or
    /// when cancelled. The dialog reads this after <see cref="ConfirmCommand"/>
    /// runs + closes.</summary>
    public ImportModResult? Result { get; private set; }

    /// <summary>Whether a remote source (Nexus) is chosen, driving the Version +
    /// URL fields' visibility.</summary>
    public bool IsRemote => SourceChoice != ImportSource.Untracked;

    /// <summary>Whether the Version field is visible (Nexus). The field is
    /// required for the remote source; it shows so the user must enter the
    /// release tag.</summary>
    public bool IsVersionVisible => IsRemote;

    /// <summary>The localized label for the URL field (Nexus).</summary>
    public string UrlLabel => _localization["Import_NexusUrlLabel"];

    /// <summary>The localized placeholder for the URL field (Nexus).</summary>
    public string UrlPlaceholder => _localization["Import_UrlPlaceholderNexus"];

    /// <summary>
    /// The localized validation message for the URL field when the input is
    /// non-empty but does not parse, or the required message when empty for a
    /// remote source. Empty when there is nothing to show (Local, or a valid
    /// remote URL).
    /// </summary>
    public string UrlValidationMessage
    {
        get
        {
            if (!IsRemote)
            {
                return string.Empty;
            }

            var url = Url?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(url))
            {
                return _localization["Import_UrlRequired"];
            }

            return TryParseUrl(SourceChoice, url, out _)
                ? string.Empty
                : _localization["Import_UrlInvalid"];
        }
    }

    /// <summary>
    /// The localized validation message for the Version field when it is empty /
    /// whitespace for a remote source. Empty when there is nothing to show
    /// (Local, or a non-empty Version). Never throws.
    /// </summary>
    public string VersionValidationMessage
    {
        get
        {
            if (!IsRemote)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(Version)
                ? _localization["Import_VersionRequired"]
                : string.Empty;
        }
    }

    /// <summary>
    /// Whether OK may be enabled. The mod name must be non-empty (it is the
    /// mod-store key); a remote source additionally needs a non-empty
    /// Version + a URL that parses. Local needs only the name.
    /// </summary>
    public bool CanConfirm =>
        !string.IsNullOrWhiteSpace(ModName)
        && (!IsRemote
            || (!string.IsNullOrWhiteSpace(Version)
                && TryParseUrl(SourceChoice, Url ?? string.Empty, out _)));

    /// <summary>
    /// Builds + stores the <see cref="Result"/> (URL parsed to canonical source)
    /// when <see cref="CanConfirm"/>. The dialog closes on a non-null result.
    /// Defense-in-depth: a disabled OK never reaches here, but the CanConfirm
    /// re-check keeps a programmatic call honest.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm)
        {
            return;
        }

        var name = ModName.Trim();
        // Version is required for remote sources and always "" for Untracked.
        // The user supplies the release tag; the modal does not fetch it.
        var recordedVersion = (Version ?? string.Empty).Trim();

        ModSource source;
        if (SourceChoice == ImportSource.Untracked)
        {
            source = new UntrackedSource();
            recordedVersion = string.Empty;
        }
        else if (!TryParseUrl(SourceChoice, Url ?? string.Empty, out var parsed))
        {
            // Unreachable when CanConfirm holds; kept defensive.
            return;
        }
        else
        {
            source = parsed;
        }

        // Write the trimmed edited name back to the request so the add flow uses
        // the canonical (possibly renamed) name as the mod-store key.
        _request.ModName = name;

        Result = new ImportModResult(source, recordedVersion, Policy);
        OnPropertyChanged(nameof(Result));
    }

    /// <summary>
    /// Parses the URL for the chosen source into a canonical <see cref="ModSource"/>.
    /// Local never reaches here. Never throws.
    /// </summary>
    private static bool TryParseUrl(ImportSource source, string url, out ModSource parsed)
    {
        parsed = new UntrackedSource();
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        switch (source)
        {
            case ImportSource.Nexus:
                if (ModSourceParser.TryParseNexus(url, out var nexus))
                {
                    parsed = nexus;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }
}
