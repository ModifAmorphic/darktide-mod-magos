using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magos.Modificus.Profiles;
using Magos.Modificus.UI.Dialogs;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// The view model behind the "Manage profiles…" dialog — an editable list of
/// profiles with per-row inline rename + delete and an "+ New profile" add row.
/// It is deliberately CRUD-only: no mod-list and no launch behavior (those are
/// Tracks B/C). All mutations flow straight to <see cref="IProfileService"/>; the
/// list rebuilds after each.
/// </summary>
/// <remarks>
/// <para><b>Editable-list pattern:</b> each row is a <see cref="ProfileItemViewModel"/>
/// (profile id + name + inline-edit state + active marker). The ✏ pencil flips a
/// row into inline edit; Enter / blur commits (rename), Esc cancels. The 🗑 trash
/// opens the delete-confirm flow. The "+ New profile" row toggles an inline name
/// entry; Enter creates (and the new profile becomes active), Esc cancels. The
/// active profile is marked (●) so it is visible which one a delete would remove.</para>
/// <para><b>Active-profile tracking:</b> the dialog never edits the shell's
/// <c>SelectedProfile</c> directly (the shell owns selection + persistence).
/// Instead it mirrors the current active id, evolves it on the operations that
/// change it (create → the new profile becomes active; delete-of-active → fall
/// back to the first remaining, or none), and exposes the final value via
/// <see cref="ActiveProfileId"/> for the shell to apply on close. Rename leaves
/// it untouched (the id is stable across renames).</para>
/// </remarks>
public partial class ManageProfilesViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private Guid? _activeProfileId;

    /// <param name="profiles">The profile service (CRUD).</param>
    /// <param name="dialogs">The dialog seam — used for the delete confirmation.</param>
    /// <param name="initialActiveProfileId">The active profile id when the dialog
    /// opened; mirrored here and evolved by create / delete-of-active.</param>
    public ManageProfilesViewModel(
        IProfileService profiles,
        IDialogService dialogs,
        Guid? initialActiveProfileId)
    {
        _profiles = profiles;
        _dialogs = dialogs;
        _activeProfileId = initialActiveProfileId;
        Refresh();
    }

    /// <summary>
    /// The active-profile id the shell should apply after the dialog closes.
    /// Evolved by create (→ new id) and delete-of-active (→ first remaining or
    /// <c>null</c>); rename leaves it unchanged.
    /// </summary>
    public Guid? ActiveProfileId => _activeProfileId;

    /// <summary>The editable profile rows (name + active marker + inline actions).</summary>
    [ObservableProperty]
    private IReadOnlyList<ProfileItemViewModel> _items = Array.Empty<ProfileItemViewModel>();

    /// <summary>
    /// Whether the "+ New profile" add-row is showing its inline name-entry box
    /// (false = the add button is shown, true = the entry box is shown).
    /// </summary>
    [ObservableProperty]
    private bool _isAddingNew;

    /// <summary>The name typed into the add-row; Enter creates, Esc cancels.</summary>
    [ObservableProperty]
    private string _newProfileName = string.Empty;

    // ---- inline rename (✏) -------------------------------------------------

    /// <summary>
    /// Flips a row into inline rename mode: any in-flight rename on another row
    /// is cancelled first (only one edit at a time), and the row's edit text is
    /// pre-filled with its current name.
    /// </summary>
    [RelayCommand]
    private void StartRename(ProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        ExitAllEdits();
        item.EditText = item.Name;
        item.IsEditing = true;
    }

    /// <summary>
    /// Commits the inline rename: trims the entry, and if it is non-empty and
    /// different from the current name, calls <see cref="IProfileService.RenameProfile"/>
    /// and updates the row's name in place. An empty / unchanged entry is a
    /// silent revert (no rename, no error) — commit-on-blur semantics. Always
    /// exits edit mode (no-op + safe if already exited, so Enter → blur does not
    /// double-rename).
    /// </summary>
    [RelayCommand]
    private void CommitRename(ProfileItemViewModel? item)
    {
        if (item is null || !item.IsEditing)
        {
            return;
        }

        var name = item.EditText.Trim();
        item.IsEditing = false;

        if (string.IsNullOrEmpty(name) || name == item.Name)
        {
            return;
        }

        _profiles.RenameProfile(item.Id, name);
        item.Name = name;
    }

    /// <summary>Cancels the inline rename — discards the entry, exits edit mode.</summary>
    [RelayCommand]
    private void CancelRename(ProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsEditing = false;
    }

    // ---- delete (🗑) -------------------------------------------------------

    /// <summary>
    /// Deletes the row's profile after a confirmation (real data loss: the
    /// profile config + its owned local mod copies). If the deleted profile was
    /// active, the active mirror falls back to the first remaining profile, or
    /// <c>null</c> when none remain.
    /// </summary>
    [RelayCommand]
    private async Task DeleteProfile(ProfileItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var name = item.Name;
        var confirmed = await _dialogs.ConfirmAsync(
            "Delete profile",
            $"Delete profile {name}? This removes its mod list and any local mod copies in mods/.");

        if (!confirmed)
        {
            return;
        }

        var id = item.Id;
        _profiles.DeleteProfile(id);

        if (_activeProfileId == id)
        {
            var remaining = _profiles.ListProfiles();
            _activeProfileId = remaining.Count > 0 ? remaining[0].Id : null;
        }

        Refresh();
    }

    // ---- create ("+ New profile") -----------------------------------------

    /// <summary>
    /// Flips the add-row into inline name entry: cancels any in-flight rename
    /// first, clears the entry, and shows the box.
    /// </summary>
    [RelayCommand]
    private void StartCreate()
    {
        ExitAllEdits();
        NewProfileName = string.Empty;
        IsAddingNew = true;
    }

    /// <summary>
    /// Commits the add-row: trims the entry, and if non-empty, creates the
    /// profile (which becomes active), then refreshes. An empty entry is a
    /// silent cancel (no create). Always exits add mode (no-op + safe if already
    /// exited, so Enter → blur does not double-create).
    /// </summary>
    [RelayCommand]
    private void CommitCreate()
    {
        if (!IsAddingNew)
        {
            return;
        }

        var name = NewProfileName.Trim();
        IsAddingNew = false;
        NewProfileName = string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var created = _profiles.CreateProfile(name);
        _activeProfileId = created.Id;
        Refresh();
    }

    /// <summary>Cancels the add-row — discards the entry, hides the box.</summary>
    [RelayCommand]
    private void CancelCreate()
    {
        IsAddingNew = false;
        NewProfileName = string.Empty;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Cancels any in-flight inline rename across all rows.</summary>
    private void ExitAllEdits()
    {
        foreach (var item in Items)
        {
            item.IsEditing = false;
        }
    }

    /// <summary>
    /// Rebuilds the row list from the service, marking the active profile's row
    /// so the ● marker renders on it.
    /// </summary>
    private void Refresh()
    {
        var summaries = _profiles.ListProfiles();
        Items = summaries
            .Select(s => new ProfileItemViewModel(s.Id, s.Name) { IsActive = s.Id == _activeProfileId })
            .ToArray();
    }
}
