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
    /// Opens the "Manage profiles…" modal dialog (create / rename / delete),
    /// scoped to the given <paramref name="currentActiveProfileId"/>. Returns
    /// the active-profile id the shell should apply after the dialog closes:
    /// unchanged when the user only renames or does nothing; the newly-created
    /// id after a create; or a fallback (first remaining / <c>null</c>) when the
    /// active profile itself was deleted. The shell owns <c>SelectedProfile</c>
    /// + persistence — this only reports the requested id.
    /// </summary>
    Task<Guid?> ShowManageProfilesAsync(Guid? currentActiveProfileId);
}
