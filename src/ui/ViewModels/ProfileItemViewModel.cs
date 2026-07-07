using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// One row in the "Manage profiles" editable list. Wraps a profile's stable
/// identity + display name, plus the per-row inline-edit state (rename) and the
/// active marker. The parent <see cref="ManageProfilesViewModel"/> owns all CRUD;
/// this row carries state only; it never talks to
/// <see cref="Modificus.Curator.Profiles.IProfileService"/> directly.
/// </summary>
/// <remarks>
/// Identity (<see cref="Id"/>) is immutable; <see cref="Name"/> is updated
/// in place after a successful rename (avoids a full list rebuild, so focus /
/// scroll position are not disturbed). <see cref="IsEditing"/> +
/// <see cref="EditText"/> drive the inline rename TextBox; <see cref="IsActive"/>
/// drives the active marker (drawn <c>&lt;Ellipse&gt;</c>, no glyph);
/// <see cref="IsDeleteEnabled"/> drives the trash button's enabled state (the
/// active profile is locked while Darktide runs, read from the session's
/// authority by the parent dialog). <see cref="DeleteTooltip"/> is localized +
/// re-resolves on a culture change (the parent dialog calls
/// <see cref="RefreshTooltip"/> when the LocalizationService flips).
/// </remarks>
public partial class ProfileItemViewModel : ObservableObject
{
    private readonly LocalizationService _localization;

    /// <summary>The profile's stable identity (unchanged across renames).</summary>
    public Guid Id { get; }

    /// <summary>The display name; updated in place after a rename commits.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>Whether this row is the active profile (drives the active marker).</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Whether this row is currently showing its inline rename TextBox.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>
    /// Whether this row's trash button is enabled: the active profile is locked
    /// while Darktide runs (the parent dialog reads
    /// <see cref="Session.IProfileSession.CanDeleteProfile"/>), so its trash
    /// disables while the game runs. Defaults to <c>true</c>; the dialog sets it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteTooltip))]
    private bool _isDeleteEnabled = true;

    /// <summary>
    /// The trash button tooltip: the normal "Delete", or the lock explanation when
    /// this row is the active profile held while Darktide runs. Localized + derived
    /// from <see cref="IsDeleteEnabled"/> so it tracks the gate automatically.
    /// </summary>
    public string DeleteTooltip => IsDeleteEnabled
        ? _localization["ManageProfiles_DeleteTooltip"]
        : _localization["ManageProfiles_DeleteLockedTooltip"];

    /// <summary>
    /// The in-flight rename value. Pre-filled from <see cref="Name"/> on edit
    /// start; committed (if non-empty + changed) on Enter / blur, discarded on Esc.
    /// </summary>
    [ObservableProperty]
    private string _editText = string.Empty;

    public ProfileItemViewModel(Guid id, string name, LocalizationService localization)
    {
        Id = id;
        _name = name;
        _localization = localization;
    }

    /// <summary>
    /// Re-fires the property-changed event for <see cref="DeleteTooltip"/> so its
    /// binding re-resolves after a UI culture switch. Called by the parent dialog
    /// when the LocalizationService raises its culture-changed event.
    /// </summary>
    public void RefreshTooltip() => OnPropertyChanged(nameof(DeleteTooltip));
}
