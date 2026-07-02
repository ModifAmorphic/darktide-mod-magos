using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magos.Modificus.Profiles;
using Magos.Modificus.UI.Dialogs;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// The view model behind the "Manage profiles…" dialog. Owns CRUD-only profile
/// editing (create / rename / delete) — it deliberately exposes no mod-list and
/// no launch behavior (those are Tracks B/C). All mutations flow straight to
/// <see cref="IProfileService"/>; the list + selection refresh after each.
/// </summary>
/// <remarks>
/// <para><b>Active-profile tracking:</b> the dialog never edits the shell's
/// <c>SelectedProfile</c> directly (the shell owns selection + persistence).
/// Instead it mirrors the current active id, updates its local mirror on the
/// operations that change it (create → the new profile becomes active; delete of
/// the active → fall back to the first remaining, or none), and exposes the
/// final value via <see cref="ActiveProfileId"/> for the shell to apply on
/// close. Rename leaves it untouched (the id is stable across renames).</para>
/// </remarks>
public partial class ManageProfilesViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private Guid? _activeProfileId;

    /// <param name="profiles">The profile service (CRUD).</param>
    /// <param name="dialogs">The dialog seam — used for the delete confirmation.</param>
    /// <param name="initialActiveProfileId">The active profile id when the dialog
    /// opened; mirrored here and evolved by create/delete-of-active.</param>
    public ManageProfilesViewModel(
        IProfileService profiles,
        IDialogService dialogs,
        Guid? initialActiveProfileId)
    {
        _profiles = profiles;
        _dialogs = dialogs;
        _activeProfileId = initialActiveProfileId;
        Refresh(selectId: _activeProfileId);
    }

    /// <summary>
    /// The active-profile id the shell should apply after the dialog closes.
    /// Evolved by create (→ new id) and delete-of-active (→ first remaining or
    /// <c>null</c>); rename leaves it unchanged.
    /// </summary>
    public Guid? ActiveProfileId => _activeProfileId;

    /// <summary>The profiles shown in the dialog's list.</summary>
    [ObservableProperty]
    private IReadOnlyList<ProfileSummary> _profileList = Array.Empty<ProfileSummary>();

    /// <summary>The currently-selected list row; drives Rename/Delete enablement.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameProfileCommand))]
    private ProfileSummary? _selectedProfile;

    /// <summary>The name typed into the "new profile" field.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProfileCommand))]
    private string _newProfileName = string.Empty;

    /// <summary>
    /// The editable rename field. Pre-filled from the current selection (rename
    /// is inline, not a prompt — keeps it testable without an extra dialog).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameProfileCommand))]
    private string _renameName = string.Empty;

    /// <summary>Pre-fills the rename field whenever the selection changes.</summary>
    partial void OnSelectedProfileChanged(ProfileSummary? value)
    {
        RenameName = value?.Name ?? string.Empty;
    }

    /// <summary>Creates a new profile; the new profile becomes active + selected.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private void CreateProfile()
    {
        var name = NewProfileName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var created = _profiles.CreateProfile(name);
        NewProfileName = string.Empty;
        _activeProfileId = created.Id;
        Refresh(selectId: created.Id);
    }

    private bool CanCreateProfile() => !string.IsNullOrWhiteSpace(NewProfileName);

    /// <summary>Renames the selected profile (display label only; id unchanged).</summary>
    [RelayCommand(CanExecute = nameof(CanRenameProfile))]
    private void RenameProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var name = RenameName.Trim();
        if (string.IsNullOrEmpty(name) || name == SelectedProfile.Name)
        {
            return;
        }

        var id = SelectedProfile.Id;
        _profiles.RenameProfile(id, name);
        Refresh(selectId: id);
    }

    private bool CanRenameProfile() =>
        SelectedProfile is not null && !string.IsNullOrWhiteSpace(RenameName);

    /// <summary>
    /// Deletes the selected profile after a confirmation (real data loss: the
    /// profile config + its owned local mod copies). If the deleted profile was
    /// active, the active mirror falls back to the first remaining profile, or
    /// <c>null</c> when none remain.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var name = SelectedProfile.Name;
        var confirmed = await _dialogs.ConfirmAsync(
            "Delete profile",
            $"Delete profile {name}? This removes its mod list and any local mod copies in mods/.");

        if (!confirmed)
        {
            return;
        }

        var id = SelectedProfile.Id;
        _profiles.DeleteProfile(id);

        if (_activeProfileId == id)
        {
            var remaining = _profiles.ListProfiles();
            _activeProfileId = remaining.Count > 0 ? remaining[0].Id : null;
        }

        Refresh(selectId: _activeProfileId);
    }

    private bool CanDeleteProfile() => SelectedProfile is not null;

    /// <summary>
    /// Reloads the list from the service and selects the requested id, falling
    /// back to the first row (or <c>null</c>) when it is absent — so a stale id
    /// or a <c>null</c> id still lands the selection on a sensible row.
    /// </summary>
    private void Refresh(Guid? selectId)
    {
        ProfileList = _profiles.ListProfiles();
        var requested = selectId is Guid id
            ? ProfileList.FirstOrDefault(p => p.Id == id)
            : null;
        SelectedProfile = requested ?? (ProfileList.Count > 0 ? ProfileList[0] : null);
    }
}
