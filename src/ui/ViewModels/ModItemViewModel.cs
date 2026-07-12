using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// One row in the active profile's mod list. Carries the mod's display state
/// (container id + name + source + version + enabled + order + policy) and the
/// per-row policy edit state (Latest / Pinned). The parent <see cref="ModListViewModel"/>
/// owns all service calls; this row carries state only and never talks to
/// <see cref="Profiles.IProfileService"/> directly (mirrors
/// <see cref="ProfileItemViewModel"/>).
/// </summary>
/// <remarks>
/// <para><b>Identity:</b> <see cref="ContainerId"/> is immutable (the join key
/// against <see cref="IModRepository"/>). <see cref="Name"/> is the resolved
/// display name (joined from the container on reload). <see cref="Enabled"/> is
/// two-way bound to the row's CheckBox; the parent applies the toggle through
/// <c>IProfileService.SetModEnabled</c>. <see cref="Order"/> drives the display
/// sort and the up/down moves (the parent re-persists via
/// <c>IProfileService.SetModOrder</c>).</para>
/// <para><b>Source / version badge:</b> <see cref="Source"/> +
/// <see cref="ActualVersion"/> are joined from the repository by the parent on
/// reload (the resolved version for the row's policy). <see cref="Found"/> flags
/// a mod whose container is absent (a stale profile reference); the badge then
/// reads a "not found" marker (staging warns at launch; resolution is out of
/// scope here).</para>
/// <para><b>Policy editor:</b> <see cref="PolicyChoice"/> (0 = Latest,
/// 1 = Pinned) is two-way bound to the row's policy ComboBox; switching it routes
/// through the view to the parent's <c>SetModPolicy</c> command.
/// <see cref="AvailableVersions"/> + <see cref="SelectedVersion"/> drive a
/// constrained dropdown of the container's versions (the row can only pin to a
/// version that exists in the container): the dropdown shows the readable
/// <see cref="ModVersion.VersionString"/> and stores the <see cref="ModVersion.Folder"/>
/// id, which the parent wraps as <c>PinnedPolicy(selectedVersionId)</c>.</para>
/// <para><b>Localized text is live:</b> <see cref="SourceBadgeText"/> +
/// <see cref="PolicyDisplayText"/> resolve from <see cref="LocalizationService"/>
/// and re-fire on a culture change (the parent calls <see cref="Refresh"/> per
/// row).</para>
/// </remarks>
public partial class ModItemViewModel : ObservableObject
{
    /// <summary>
    /// Policy choice index for the row ComboBox: <c>0</c> = Latest,
    /// <c>1</c> = Pinned.
    /// </summary>
    public const int PolicyLatest = 0;

    /// <summary>Policy choice index for the row ComboBox: Pinned.</summary>
    public const int PolicyPinned = 1;

    private readonly LocalizationService _localization;

    /// <summary>
    /// The mod container's id (immutable); the join key against
    /// <see cref="IModRepository"/> + the value written through
    /// <c>IProfileService.SetModEnabled/SetModPolicy/SetModOrder/RemoveMod</c>.
    /// </summary>
    public Guid ContainerId { get; }

    /// <summary>
    /// The container's display name (joined from the repository by the parent);
    /// shown in the row + used in the remove-confirm message. Empty when the
    /// container is missing. Settable + observable so the parent can refresh it
    /// in place after a check that renamed the container (the name-sync result),
    /// without rebuilding the row.
    /// </summary>
    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Where this mod came from (Untracked / Nexus / GitHub), joined from the
    /// repository by the parent. <see cref="UntrackedSource"/> when the container
    /// is absent.
    /// </summary>
    public ModSource Source { get; }

    /// <summary>
    /// The resolved version tag of the container (joined from the repository for
    /// the row's policy), or <see cref="string.Empty"/> when unknown. Shown in
    /// the policy display text + the source badge; never order-compared.
    /// </summary>
    public string ActualVersion { get; }

    /// <summary>
    /// Whether the mod is active (two-way bound to the row's CheckBox). The parent
    /// applies a user toggle through <c>IProfileService.SetModEnabled</c>.
    /// </summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>
    /// Position within the load order (lower loads first). Drives the display sort
    /// and the up/down move commands (the parent re-persists the order).
    /// </summary>
    public int Order { get; }

    /// <summary>
    /// The mod's current effective version policy. Set by the parent on reload;
    /// drives <see cref="PolicyChoice"/> + <see cref="PolicyDisplayText"/>.
    /// </summary>
    public ModVersionPolicy Policy { get; private set; }

    /// <summary>
    /// Whether the repository had a container for this entry at reload. <c>false</c>
    /// marks a stale profile reference (the badge reads "not found"; staging warns
    /// at launch).
    /// </summary>
    public bool Found { get; }

    /// <summary>
    /// Whether the update check flagged this container as having a newer release
    /// on Nexus than the imported version. Set by the parent
    /// <see cref="ModListViewModel"/> from
    /// <c>IUpdateCheckService.LastResult.Updates</c> (matched by
    /// <see cref="ContainerId"/>) on reload + on the
    /// <c>CheckCompleted</c> event. Drives the drawn update-available marker on
    /// the source badge + (combined with premium + Nexus + Latest) the per-row
    /// Update button's visibility. Always <c>false</c> for Pinned / Untracked /
    /// GitHub rows (the update check skips them).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowUpdateButton))]
    private bool _updateAvailable;

    /// <summary>
    /// Whether the row is currently running a one-click update (the parent's
    /// <c>UpdateCommand</c> set it + <c>AnyRowUpdating</c> on the parent). While
    /// true, the row shows an indeterminate progress affordance in place of the
    /// Update button + every other row's Update button is disabled (one update
    /// at a time). Cleared in the command's finally block on success or failure.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowUpdateButton))]
    private bool _isUpdating;

    /// <summary>
    /// The ComboBox selection for the policy editor (0 = Latest, 1 = Pinned),
    /// two-way bound. Initialized from <see cref="Policy"/> on construction; a user
    /// change routes through the view to the parent's <c>SetModPolicy</c> command.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    private int _policyChoice;

    /// <summary>
    /// The versions the row's pin dropdown can choose between, joined from the
    /// container by the parent on reload. Each entry pairs the readable
    /// <see cref="VersionOption.VersionString"/> (shown in the dropdown) with the
    /// opaque <see cref="VersionOption.VersionId"/> (the value written through
    /// as <c>PinnedPolicy(versionId)</c>). Empty when the container is missing or
    /// has no versions; the dropdown then has nothing to offer (a no-version
    /// container cannot be pinned).
    /// </summary>
    public IReadOnlyList<VersionOption> AvailableVersions { get; }

    /// <summary>
    /// The dropdown's current selection (two-way bound). Pre-selected on
    /// construction: when the policy is Pinned, the entry matching the pin's
    /// versionId; when the policy is Latest, the resolved (<c>IsLatest</c>)
    /// version so a switch to Pinned offers the actual version rather than a
    /// blank. <c>null</c> when <see cref="AvailableVersions"/> is empty. A user
    /// selection routes through the view to the parent's <c>SetPolicyPinned</c>
    /// command with the selected versionId.
    /// </summary>
    [ObservableProperty]
    private VersionOption? _selectedVersion;

    /// <summary>
    /// Whether the row's policy is Pinned (derived from <see cref="PolicyChoice"/>),
    /// driving the inline version dropdown's visibility.
    /// </summary>
    public bool IsPinned => PolicyChoice == PolicyPinned;

    /// <summary>
    /// The source badge text (localized): "Local" / "Nexus #{id}" /
    /// "GitHub {owner}/{repo}", or a "not found" marker when <see cref="Found"/>
    /// is <c>false</c>.
    /// </summary>
    public string SourceBadgeText
    {
        get
        {
            if (!Found)
            {
                return _localization["ModRow_NotFound"];
            }

            return Source switch
            {
                NexusSource n => _localization.Format("ModRow_SourceNexus", n.ModId),
                GitHubSource g => _localization.Format("ModRow_SourceGitHub", g.Owner, g.Repo),
                _ => _localization["ModRow_SourceUntracked"],
            };
        }
    }

    /// <summary>
    /// The policy display text (localized): "Latest", or "Pinned {version}" with
    /// the resolved version's readable tag (falls back to the bare "Pinned" label
    /// when the version is empty, e.g. an orphan pin that no longer resolves).
    /// The version shown is the current effective resolution
    /// (<see cref="ActualVersion"/>, joined from the repository for the row's
    /// policy), not the in-flight <see cref="SelectedVersion"/> edit.
    /// </summary>
    public string PolicyDisplayText
    {
        get
        {
            if (Policy is PinnedPolicy)
            {
                return string.IsNullOrEmpty(ActualVersion)
                    ? _localization["ModRow_PolicyPinned"]
                    : _localization.Format("ModRow_PolicyPinnedDisplay", ActualVersion);
            }

            return _localization["ModRow_PolicyLatest"];
        }
    }

    /// <summary>
    /// The Nexus mod id when the row's source is <see cref="NexusSource"/>, else
    /// <c>null</c>. The parent's update command reads this to call
    /// <c>IModAcquisitionService.AcquireLatestNexusAsync</c> (which takes the mod
    /// id, not the file id). Null for Untracked / GitHub / not-found rows.
    /// </summary>
    public int? NexusModId => Source is NexusSource n ? n.ModId : null;

    /// <summary>
    /// Whether the row is both Nexus-sourced AND on the <see cref="LatestPolicy"/>
    /// (the conjunction the update check requires). The Update button's effective
    /// visibility binds to this AND <see cref="UpdateAvailable"/> AND the list
    /// VM's <c>IsPremiumUser</c>. Pinned / Untracked / GitHub rows are always
    /// <c>false</c>, so the Update button never shows for them.
    /// </summary>
    public bool IsNexusLatest => Source is NexusSource && Policy is LatestPolicy;

    /// <summary>
    /// The row-local half of the Update button's visibility conjunction:
    /// <see cref="IsNexusLatest"/> AND <see cref="UpdateAvailable"/> AND NOT
    /// <see cref="IsUpdating"/>. The list VM's <c>IsPremiumUser</c> (the
    /// premium gate) ANDs with this in the view via a MultiBinding. Splitting
    /// the conjunction this way avoids a 4-way MultiBinding + lets each source
    /// re-fire <see cref="CanShowUpdateButton"/> via
    /// <c>[NotifyPropertyChangedFor]</c>.
    /// </summary>
    public bool CanShowUpdateButton => IsNexusLatest && UpdateAvailable && !IsUpdating;

    /// <summary>
    /// The mod's remote page URL for the source-badge link (the badge is a
    /// hyperlink). Nexus -> the mod page; GitHub -> the repo; Untracked /
    /// not-found -> <c>null</c> (the link is a no-op + the badge reads as plain
    /// metadata). The URL is not localized, so it does not re-resolve on a
    /// culture change; <see cref="Refresh"/> re-fires it only for binding
    /// consistency if the source ever changes (it does not today, but the hook
    /// keeps the contract uniform with the other derived members).
    /// </summary>
    public string? SourceUrl => Source switch
    {
        NexusSource n => $"https://www.nexusmods.com/warhammer40kdarktide/mods/{n.ModId}",
        GitHubSource g => $"https://github.com/{g.Owner}/{g.Repo}",
        _ => null,
    };

    /// <summary>
    /// The mod's Nexus <c>files</c> tab URL (the update-available marker is a
    /// hyperlink to it, so the user's instinct to click the marker lands on the
    /// files page where the new release lives). Nexus -> the mod page with
    /// <c>?tab=files</c>; GitHub / Untracked / not-found -> <c>null</c> (the
    /// marker's <c>NavigateUri</c> is null, so the <c>HyperlinkButton</c>
    /// no-ops; those rows never show the marker anyway). Reuses
    /// <see cref="SourceUrl"/> for the base, so any future change to the page
    /// URL shape lands in one place.
    /// </summary>
    public string? UpdatePageUrl => Source is NexusSource
        ? SourceUrl + "?tab=files"
        : null;

    /// <summary>
    /// Creates a row. The parent (<see cref="ModListViewModel"/>) builds rows on
    /// reload, joining source + version + the version list from the repository.
    /// </summary>
    /// <param name="localization">The localization service, used for the derived
    /// badge + policy text (re-resolves on a culture change).</param>
    /// <param name="containerId">The mod container's id (immutable join key).</param>
    /// <param name="name">The container's display name.</param>
    /// <param name="source">The joined source provenance.</param>
    /// <param name="actualVersion">The joined resolved version tag (readable),
    /// for the policy display text.</param>
    /// <param name="enabled">Whether the mod is active.</param>
    /// <param name="order">The load-order position.</param>
    /// <param name="policy">The current effective version policy.</param>
    /// <param name="versions">The container's versions (joined from the
    /// repository); drives the pin dropdown. Empty when the container is missing
    /// or version-less.</param>
    /// <param name="found">Whether the repository had a container for this entry.</param>
    public ModItemViewModel(
        LocalizationService localization,
        Guid containerId,
        string name,
        ModSource source,
        string actualVersion,
        bool enabled,
        int order,
        ModVersionPolicy policy,
        IReadOnlyList<ModVersion> versions,
        bool found)
    {
        _localization = localization;
        ContainerId = containerId;
        _name = name;
        Source = source;
        ActualVersion = actualVersion;
        _enabled = enabled;
        Order = order;
        Policy = policy;
        Found = found;

        // Build the dropdown source from the container's versions: each entry
        // pairs the readable tag (shown) with the opaque folder id (stored).
        AvailableVersions = versions
            .Select(v => new VersionOption(v.VersionString, v.Folder))
            .ToArray();

        // Seed the policy editor from the effective policy. Pinned selects the
        // Pinned choice + the dropdown entry matching the pin's versionId; Latest
        // selects Latest + pre-selects the resolved (IsLatest) version so a switch
        // to Pinned offers the actual version rather than a blank.
        if (policy is PinnedPolicy pinned)
        {
            _policyChoice = PolicyPinned;
            _selectedVersion = AvailableVersions.FirstOrDefault(o => o.VersionId == pinned.VersionId);
        }
        else
        {
            _policyChoice = PolicyLatest;
            var resolved = versions.FirstOrDefault(v => v.IsLatest);
            _selectedVersion = resolved is null
                ? AvailableVersions.FirstOrDefault()
                : AvailableVersions.FirstOrDefault(o => o.VersionId == resolved.Folder);
        }
    }

    /// <summary>
    /// Re-fires the property-changed events for the localized derived strings so
    /// their bindings re-resolve after a UI culture switch. Called by the parent
    /// when the LocalizationService raises its culture-changed event. The
    /// non-localized derived members (<see cref="SourceUrl"/>,
    /// <see cref="UpdatePageUrl"/>, <see cref="NexusModId"/>,
    /// <see cref="IsNexusLatest"/>) do not change with the culture, but
    /// re-firing <see cref="SourceUrl"/> + <see cref="UpdatePageUrl"/> keeps the
    /// refresh contract uniform across derived members.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SourceBadgeText));
        OnPropertyChanged(nameof(PolicyDisplayText));
        OnPropertyChanged(nameof(SourceUrl));
        OnPropertyChanged(nameof(UpdatePageUrl));
    }
}

/// <summary>
/// One entry in a row's pin dropdown: pairs the readable version tag (shown in
/// the dropdown) with the opaque version id (the <see cref="ModVersion.Folder"/>
/// value written through as <c>PinnedPolicy(versionId)</c>). A value-equal
/// record so Avalonia's ComboBox selection matches by (tag, id), not by
/// reference.
/// </summary>
/// <param name="VersionString">The readable release tag (e.g. <c>"1.2"</c>),
/// shown in the dropdown. Display only.</param>
/// <param name="VersionId">The version's opaque folder id (a
/// <see cref="ModVersion.Folder"/>); the value stored on selection + wrapped as
/// <c>PinnedPolicy(versionId)</c>.</param>
public sealed record VersionOption(string VersionString, string VersionId);
