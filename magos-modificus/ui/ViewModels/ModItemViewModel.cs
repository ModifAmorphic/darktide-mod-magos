using CommunityToolkit.Mvvm.ComponentModel;
using Magos.Modificus.SharedMods;
using Magos.Modificus.UI.Localization;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// One row in the active profile's mod list. Carries the mod's display state
/// (name + source + version + enabled + order + policy) and the per-row policy
/// edit state (Latest / Pinned). The parent <see cref="ModListViewModel"/> owns
/// all service calls; this row carries state only and never talks to
/// <see cref="Profiles.IProfileService"/> directly (mirrors
/// <see cref="ProfileItemViewModel"/>).
/// </summary>
/// <remarks>
/// <para><b>Identity:</b> <see cref="Name"/> is immutable (the shared-store key +
/// the <c>mods.lst</c> entry). <see cref="Enabled"/> is two-way bound to the row's
/// CheckBox; the parent applies the toggle through
/// <c>IProfileService.SetModEnabled</c>. <see cref="Order"/> drives the display
/// sort and the up/down moves (the parent re-persists via
/// <c>IProfileService.SetModOrder</c>).</para>
/// <para><b>Source / version badge:</b> <see cref="Source"/> +
/// <see cref="ActualVersion"/> are joined from the shared store by the parent on
/// reload. <see cref="Found"/> flags a mod whose shared entry is absent (a stale
/// profile reference); the badge then reads a "not found" marker (staging warns
/// at launch; resolution is out of scope here).</para>
/// <para><b>Policy editor:</b> <see cref="PolicyChoice"/> (0 = Latest,
/// 1 = Pinned) is two-way bound to the row's policy ComboBox; switching it routes
/// through the view to the parent's <c>SetModPolicy</c> command.
/// <see cref="PinnedVersion"/> is the editable raw tag, pre-filled from the
/// current pin or the shared entry's <see cref="ActualVersion"/>.</para>
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

    /// <summary>The mod folder name (immutable); the shared-store key + the
    /// <c>mods.lst</c> entry.</summary>
    public string Name { get; }

    /// <summary>
    /// Where this mod came from (Local / Nexus / GitHub), joined from the shared
    /// store by the parent. <see cref="NoneSource"/> when the shared entry is
    /// absent or untracked.
    /// </summary>
    public ModSource Source { get; }

    /// <summary>
    /// The raw release tag of the shared copy (joined from the shared store), or
    /// <see cref="string.Empty"/> when unknown / local. Shown in the source badge;
    /// never order-compared.
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
    /// Whether the shared store had an entry for this mod at reload. <c>false</c>
    /// marks a stale profile reference (the badge reads "not found"; staging warns
    /// at launch).
    /// </summary>
    public bool Found { get; }

    /// <summary>
    /// The ComboBox selection for the policy editor (0 = Latest, 1 = Pinned),
    /// two-way bound. Initialized from <see cref="Policy"/> on construction; a user
    /// change routes through the view to the parent's <c>SetModPolicy</c> command.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    private int _policyChoice;

    /// <summary>
    /// The editable pinned-version string (raw release tag). Pre-filled from the
    /// current pin's version, or from <see cref="ActualVersion"/> when the policy
    /// is Latest (so switching to Pinned offers the shared copy's version). Applied
    /// by the parent's <c>SetModPolicy</c> command when the user confirms.
    /// </summary>
    [ObservableProperty]
    private string _pinnedVersion = string.Empty;

    /// <summary>
    /// Whether the row's policy is Pinned (derived from <see cref="PolicyChoice"/>),
    /// driving the inline version editor's visibility.
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
                _ => _localization["ModRow_SourceLocal"],
            };
        }
    }

    /// <summary>
    /// The policy display text (localized): "Latest", or "Pinned {version}" with
    /// the pinned tag (falls back to the bare "Pinned" label when the version is
    /// empty). The version shown is the current effective pin (<see cref="Policy"/>),
    /// not the in-flight <see cref="PinnedVersion"/> edit.
    /// </summary>
    public string PolicyDisplayText
    {
        get
        {
            if (Policy is PinnedPolicy pinned)
            {
                return string.IsNullOrEmpty(pinned.Version)
                    ? _localization["ModRow_PolicyPinned"]
                    : _localization.Format("ModRow_PolicyPinnedDisplay", pinned.Version);
            }

            return _localization["ModRow_PolicyLatest"];
        }
    }

    /// <summary>
    /// Creates a row. The parent (<see cref="ModListViewModel"/>) builds rows on
    /// reload, joining source + version from the shared store.
    /// </summary>
    /// <param name="localization">The localization service, used for the derived
    /// badge + policy text (re-resolves on a culture change).</param>
    /// <param name="name">The mod folder name (immutable key).</param>
    /// <param name="source">The joined source provenance.</param>
    /// <param name="actualVersion">The joined raw release tag.</param>
    /// <param name="enabled">Whether the mod is active.</param>
    /// <param name="order">The load-order position.</param>
    /// <param name="policy">The current effective version policy.</param>
    /// <param name="found">Whether the shared store had an entry for this mod.</param>
    public ModItemViewModel(
        LocalizationService localization,
        string name,
        ModSource source,
        string actualVersion,
        bool enabled,
        int order,
        ModVersionPolicy policy,
        bool found)
    {
        _localization = localization;
        Name = name;
        Source = source;
        ActualVersion = actualVersion;
        _enabled = enabled;
        Order = order;
        Policy = policy;
        Found = found;

        // Seed the policy editor from the effective policy: Pinned selects the
        // Pinned choice + the pin's version; Latest selects Latest + pre-fills the
        // version editor with the shared copy's tag (so a switch to Pinned offers
        // the actual version rather than a blank box).
        if (policy is PinnedPolicy pinned)
        {
            _policyChoice = PolicyPinned;
            _pinnedVersion = pinned.Version;
        }
        else
        {
            _policyChoice = PolicyLatest;
            _pinnedVersion = actualVersion;
        }
    }

    /// <summary>
    /// Re-fires the property-changed events for the localized derived strings so
    /// their bindings re-resolve after a UI culture switch. Called by the parent
    /// when the LocalizationService raises its culture-changed event.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SourceBadgeText));
        OnPropertyChanged(nameof(PolicyDisplayText));
    }
}
