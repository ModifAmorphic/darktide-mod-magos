using Magos.Modificus.UI.Session;

namespace Magos.Modificus.UI.Dialogs;

/// <summary>
/// The application's UI-dialog abstraction. Keeps view models free of direct
/// Avalonia <c>Window</c> construction so their logic stays unit-testable: a VM
/// depends on this seam, and tests inject a recording fake instead of a real
/// window. The production implementation (<see cref="DialogService"/>) owns all
/// real <c>Window</c>/<c>ShowDialog</c> wiring.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a modal confirmation prompt. Returns <c>true</c> when the user
    /// confirms, <c>false</c> otherwise (cancel / dismiss). Used to gate
    /// destructive actions (e.g. profile delete — real data loss).
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>
    /// Opens the "Manage profiles…" modal dialog (create / rename / delete).
    /// Active changes are applied live through the <see cref="IProfileSession"/>
    /// during the dialog's session (create requests the new id active, gated by
    /// the session; delete-of-active reconciles), so by the time this completes
    /// the session already reflects whatever the gate allowed. The caller just
    /// refreshes its profile-list snapshot on completion; there is no
    /// returned-active-id for the shell to gate.
    /// </summary>
    Task ShowManageProfilesAsync();
}
