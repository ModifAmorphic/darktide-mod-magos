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
    /// destructive actions (e.g. profile delete: real data loss).
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

    /// <summary>
    /// Opens the Preferences modal dialog (theme / font scale / language). Each
    /// change applies immediately through <c>IPreferencesService</c> (which also
    /// persists), so by the time this completes the running app + the persisted
    /// config already reflect the user's choices. The caller has nothing to do
    /// on completion.
    /// </summary>
    Task ShowPreferencesAsync();

    /// <summary>
    /// Shows the per-mod import modal (source chooser + conditional Version +
    /// URL), pre-filled from <paramref name="request"/>. Returns the confirmed
    /// <see cref="ImportModResult"/> (URL parsed to canonical source) when the
    /// user confirms, or <c>null</c> when they cancel / dismiss. The mod-list add
    /// flow calls this once per imported path (sequentially); a <c>null</c>
    /// cancels the remaining batch.
    /// </summary>
    Task<ImportModResult?> ShowImportModAsync(ImportModRequest request);
}
