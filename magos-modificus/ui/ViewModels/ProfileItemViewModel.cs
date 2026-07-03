using CommunityToolkit.Mvvm.ComponentModel;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// One row in the "Manage profiles" editable list. Wraps a profile's stable
/// identity + display name, plus the per-row inline-edit state (rename) and the
/// active marker. The parent <see cref="ManageProfilesViewModel"/> owns all CRUD;
/// this row carries state only — it never talks to
/// <see cref="Magos.Modificus.Profiles.IProfileService"/> directly.
/// </summary>
/// <remarks>
/// Identity (<see cref="Id"/>) is immutable; <see cref="Name"/> is updated
/// in place after a successful rename (avoids a full list rebuild, so focus /
/// scroll position are not disturbed). <see cref="IsEditing"/> +
/// <see cref="EditText"/> drive the inline rename TextBox; <see cref="IsActive"/>
/// drives the ● marker; <see cref="IsDeleteEnabled"/> drives the trash button's
/// enabled state (the active profile is locked while Darktide runs, read from the
/// session's authority by the parent dialog).
/// </remarks>
public partial class ProfileItemViewModel : ObservableObject
{
    /// <summary>The profile's stable identity (unchanged across renames).</summary>
    public Guid Id { get; }

    /// <summary>The display name; updated in place after a rename commits.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>Whether this row is the active profile (drives the ● marker).</summary>
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
    /// this row is the active profile held while Darktide runs. Derived from
    /// <see cref="IsDeleteEnabled"/> so it tracks the gate automatically.
    /// </summary>
    public string DeleteTooltip => IsDeleteEnabled
        ? "Delete"
        : "Can't delete the active profile while Darktide is running.";

    /// <summary>
    /// The in-flight rename value. Pre-filled from <see cref="Name"/> on edit
    /// start; committed (if non-empty + changed) on Enter / blur, discarded on Esc.
    /// </summary>
    [ObservableProperty]
    private string _editText = string.Empty;

    public ProfileItemViewModel(Guid id, string name)
    {
        Id = id;
        _name = name;
    }
}
