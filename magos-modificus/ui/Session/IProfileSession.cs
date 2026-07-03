using System.ComponentModel;
using Magos.Modificus.Profiles;

namespace Magos.Modificus.UI.Session;

/// <summary>
/// The single authority for "which profile is active, can it change, and is the
/// game running." Both the app shell and the Manage-profiles dialog consume this;
/// neither decides the can-change gate nor tracks its own running-state. Owns the
/// active id (observable + persisted), the can-change gate, and the LIVE
/// running-state. Does NOT own profile CRUD (create/rename/delete stay on
/// <see cref="IProfileService"/>, driven by the dialog); it only owns active
/// state, the gate, and running-state.
/// </summary>
/// <remarks>
/// <para><b>The gate lives here, once:</b> <see cref="RequestActive"/> is the
/// sole place a voluntary active change is allowed or rejected (applied only
/// when the game isn't running). Both the dropdown switch and the dialog's
/// create-sets-active route through it, so the two can never diverge.</para>
/// <para><b>Delete-of-active:</b> the active profile is locked while Darktide runs
/// (<see cref="CanDeleteProfile"/> gates the delete), so delete-of-active only
/// happens when the game is stopped. <see cref="ReconcileActive"/> then clears the
/// active id (null) so the user explicitly picks the next; it never auto-selects a
/// remaining profile on someone's behalf.</para>
/// <para><b>Observable:</b> implements <see cref="INotifyPropertyChanged"/> so the
/// shell reacts to live <see cref="IsRunning"/> changes driven by the session's
/// polling timer (status strip + launch-availability + dropdown-enable update
/// within a few seconds of the game starting or stopping).</para>
/// </remarks>
public interface IProfileSession : INotifyPropertyChanged
{
    /// <summary>The current active profile id, or null when none is active.</summary>
    Guid? ActiveProfileId { get; }

    /// <summary>
    /// Whether Darktide is currently running. LIVE, refreshed by a polling timer
    /// (~3s, cheap process scan). The status strip, launch-availability, and the
    /// switch-block gate all read this.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// The SOLE active-change gate. Requests <paramref name="id"/> as the active
    /// profile: applied + persisted only when the game isn't running; otherwise a
    /// no-op (the active stays put). Both the dropdown switch and the dialog's
    /// create-sets-active call this. Rename and delete-of-active do not (rename
    /// leaves the id stable; delete uses <see cref="ReconcileActive"/>).
    /// </summary>
    void RequestActive(Guid id);

    /// <summary>
    /// Whether the profile <paramref name="id"/> may be deleted right now. The
    /// single authority for the delete gate: the active profile is locked while
    /// Darktide runs (<c>false</c> when <paramref name="id"/> is the active id and
    /// the game is running); every other profile is deletable (<c>true</c>). The
    /// Manage-profiles dialog binds each row's trash button to this so the active
    /// row's trash disables while the game runs.
    /// </summary>
    bool CanDeleteProfile(Guid id);

    /// <summary>
    /// Recovery after CRUD that may have removed the active profile: if the current
    /// active id no longer exists in <see cref="IProfileService.ListProfiles"/>,
    /// clears the active id (null) and persists. Delete-of-active is blocked while
    /// the game runs (<see cref="CanDeleteProfile"/>), so by the time this runs the
    /// game is stopped and null is the correct outcome (the user explicitly picks
    /// the next profile; we never auto-select a remaining one). A no-op when the
    /// active id is still present, or when no active is set (first run / nothing
    /// chosen).
    /// </summary>
    void ReconcileActive();
}
