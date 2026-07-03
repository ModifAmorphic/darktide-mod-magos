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
/// <para><b>Forced recovery is ungated:</b> <see cref="ReconcileActive"/> handles
/// delete-of-active (the active id no longer exists) by falling back to the first
/// remaining profile (or null), regardless of running state. The running game
/// already launched with its staged root, and the pointer must move off a gone id.</para>
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
    /// Forced recovery after CRUD that may have removed the active profile: if the
    /// current active id no longer exists in <see cref="IProfileService.ListProfiles"/>,
    /// falls back to the first remaining profile (or null when none remain) and
    /// persists. <b>Bypasses the running-state gate</b>, because there is nothing
    /// sensible to keep when the active id is gone. A no-op when the active id is
    /// still present, or when no active is set (first run / nothing chosen).
    /// </summary>
    void ReconcileActive();
}
