using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// One editable environment-variable row in the launch-settings modal: a name +
/// value pair plus the inline localized validation message derived from the
/// current state of all rows (so a duplicate or reserved-name error is reported
/// live as the user types). The parent <see cref="LaunchSettingsViewModel"/>
/// owns the validation pass; this row carries state only.
/// </summary>
public partial class EnvVarRow : ObservableObject
{
    /// <summary>The environment-variable name (editable). Validated by the
    /// parent VM (non-empty, no <c>=</c>/NUL, not reserved, unique
    /// case-insensitively).</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The environment-variable value (editable, stored exactly).
    /// Validated by the parent VM (no NUL).</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>
    /// The localized inline validation message for this row (empty when valid).
    /// Computed + pushed by the parent <see cref="LaunchSettingsViewModel"/> on
    /// every edit, so it tracks duplicate/reserved-name state across rows live.
    /// </summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public EnvVarRow(string name, string value)
    {
        _name = name;
        _value = value;
    }
}

/// <summary>
/// One editable game-argument row in the launch-settings modal: a single exact
/// argv value (any string is legal; Relay owns the final quoting). No
/// validation -- a game argument is opaque to Curator.
/// </summary>
public partial class GameArgRow : ObservableObject
{
    /// <summary>The game argument (editable, stored exactly verbatim).</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    public GameArgRow(string value) => _value = value;
}

/// <summary>
/// The view model behind the per-profile launch-settings modal
/// (<see cref="Views.LaunchSettingsWindow"/>). Owns two editable sections
/// (environment-variable name/value rows + ordered game-argument rows, each
/// add/remove) with inline localized validation, plus a Save (persists via
/// <see cref="IProfileService.SetLaunchSettings"/>, closes only on success) +
/// Cancel (no change). Existing settings load via
/// <see cref="IProfileService.GetLaunchSettings"/> at construction.
/// </summary>
/// <remarks>
/// <para><b>Validation never throws:</b> the inline pass recomputes each env
/// row's <see cref="EnvVarRow.ErrorMessage"/> on every edit (name + value +
/// duplicate + reserved), and Save is disabled while any row is invalid. Save
/// still calls the authoritative <see cref="IProfileService.SetLaunchSettings"/>
/// (defense-in-depth); an <see cref="ArgumentException"/> from it surfaces a
/// localized error and keeps the modal open.</para>
/// <para><b>Editing unlocked while Darktide runs:</b> these are a
/// <c>profile.json</c> write that does not touch the running process, and the
/// launch path reads them fresh at <c>Launch()</c> time. The modal shows a
/// static next-launch hint.</para>
/// <para><b>No <c>ConfigureAwait(false)</c></b> anywhere: the UI layer stays on
/// the captured UI context (repo convention).</para>
/// </remarks>
public partial class LaunchSettingsViewModel : ObservableObject
{
    private readonly Guid _profileId;
    private readonly IProfileService _profiles;
    private readonly LocalizationService _localization;

    public LaunchSettingsViewModel(Guid profileId, IProfileService profiles, LocalizationService localization)
    {
        _profileId = profileId;
        _profiles = profiles;
        _localization = localization;

        Load();
    }

    /// <summary>The editable environment-variable rows.</summary>
    public ObservableCollection<EnvVarRow> EnvironmentVariables { get; } = new();

    /// <summary>The editable game-argument rows (one exact argv value each).</summary>
    public ObservableCollection<GameArgRow> GameArguments { get; } = new();

    /// <summary>
    /// A top-level error from the authoritative <see cref="Save"/> call (empty
    /// when there is nothing to show). Cleared on any edit so a stale error does
    /// not linger after the user fixes the input.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _saveError = string.Empty;

    /// <summary>
    /// Whether Save may be enabled: no env row carries an inline error and no
    /// top-level save error is showing. The OK button binds here; a programmatic
    /// Save still re-checks defensively.
    /// </summary>
    public bool CanSave => string.IsNullOrEmpty(SaveError)
        && (_envRowsValid ?? ComputeEnvRowsValid());

    /// <summary>True when Save completed successfully; the window closes only on
    /// a true result. Stays false on Cancel / dismiss.</summary>
    public bool SaveResult { get; private set; }

    // Cached validity over env rows, invalidated on any edit and recomputed by
    // the CanSave getter lazily. Kept as a backing field so CanSave is cheap for
    // the button's IsEnabled binding (re-evaluated on every property change).
    private bool? _envRowsValid;

    // ---- load ---------------------------------------------------------------

    /// <summary>
    /// Loads the profile's existing launch settings into the editable rows. Each
    /// loaded row is subscribed so an edit recomputes the inline validation.
    /// </summary>
    private void Load()
    {
        var settings = _profiles.GetLaunchSettings(_profileId);

        EnvironmentVariables.Clear();
        foreach (var ev in settings.EnvironmentVariables)
        {
            var row = new EnvVarRow(ev.Name, ev.Value);
            Watch(row);
            EnvironmentVariables.Add(row);
        }

        GameArguments.Clear();
        foreach (var arg in settings.GameArguments)
        {
            GameArguments.Add(new GameArgRow(arg));
        }

        RecomputeValidation();
    }

    // ---- add / remove rows --------------------------------------------------

    /// <summary>Adds a new empty environment-variable row (subscribed for live
    /// validation).</summary>
    [RelayCommand]
    private void AddEnvVar()
    {
        var row = new EnvVarRow(string.Empty, string.Empty);
        Watch(row);
        EnvironmentVariables.Add(row);
        RecomputeValidation();
    }

    /// <summary>Removes an environment-variable row.</summary>
    [RelayCommand]
    private void RemoveEnvVar(EnvVarRow? row)
    {
        if (row is null)
        {
            return;
        }
        EnvironmentVariables.Remove(row);
        RecomputeValidation();
    }

    /// <summary>Adds a new empty game-argument row.</summary>
    [RelayCommand]
    private void AddGameArg() => GameArguments.Add(new GameArgRow(string.Empty));

    /// <summary>Removes a game-argument row.</summary>
    [RelayCommand]
    private void RemoveGameArg(GameArgRow? row)
    {
        if (row is null)
        {
            return;
        }
        GameArguments.Remove(row);
    }

    // ---- save / cancel ------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="LaunchSettings"/> from the rows, runs the
    /// authoritative <see cref="IProfileService.SetLaunchSettings"/>, and sets
    /// <see cref="SaveResult"/> on success (the window closes on a true result).
    /// Defense-in-depth: a disabled Save never reaches here, but the CanSave
    /// re-check + the try/catch keep a programmatic call honest. An
    /// <see cref="ArgumentException"/> surfaces a localized error and keeps the
    /// modal open.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        // A fresh recompute before reading the rows so the latest edits are
        // reflected even if a PropertyChanged has not fired yet.
        RecomputeValidation();
        if (!CanSave)
        {
            return;
        }

        var settings = new LaunchSettings
        {
            EnvironmentVariables = EnvironmentVariables
                .Select(r => new EnvVar(r.Name, r.Value))
                .ToArray(),
            GameArguments = GameArguments
                .Select(r => r.Value)
                .ToArray(),
        };

        try
        {
            _profiles.SetLaunchSettings(_profileId, settings);
        }
        catch (ArgumentException)
        {
            // Defense-in-depth: the inline pass should have caught any violation
            // (the Save button is disabled while invalid), so reaching here means
            // the inline validator and the service diverged. Keep the modal open
            // with a generic, localized message; never surface the raw service
            // message (it is non-localized English, and any rule-specific text
            // would be actively misleading for a cause the inline pass did not
            // cover). The offending row's inline message, if any, is already
            // shown by RecomputeValidation; this just blocks the close.
            SaveError = _localization["LaunchSettings_ErrSaveFailed"];
            return;
        }

        SaveResult = true;
        OnPropertyChanged(nameof(SaveResult));
    }

    /// <summary>Cancel: the window closes with <see cref="SaveResult"/> still
    /// false (no persist). The view binds Cancel/close to closing the window
    /// without running this; this method is a programmatic hook only.</summary>
    [RelayCommand]
    private void Cancel()
    {
        // No-op by intent: nothing to do (the view just closes). Kept so the
        // view's Cancel button can bind to a command for symmetry with Save.
    }

    // ---- validation ---------------------------------------------------------

    /// <summary>
    /// Subscribes to an env row's <c>Name</c>/<c>Value</c> changes so the inline
    /// validation recomputes live as the user types.
    /// </summary>
    private void Watch(EnvVarRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EnvVarRow.Name) or nameof(EnvVarRow.Value))
            {
                RecomputeValidation();
            }
        };
    }

    /// <summary>
    /// Recomputes every env row's <see cref="EnvVarRow.ErrorMessage"/> from the
    /// current set of rows: name (empty / <c>=</c> / NUL / reserved /
    /// duplicate), then value (NUL). Clears the top-level save error so a stale
    /// error does not linger after an edit, and invalidates the cached
    /// <see cref="CanSave"/> so the Save button re-enables.
    /// </summary>
    private void RecomputeValidation()
    {
        // Count non-empty names (trimmed) case-insensitively for duplicate
        // detection across rows.
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in EnvironmentVariables)
        {
            var n = (r.Name ?? string.Empty).Trim();
            if (n.Length == 0)
            {
                continue;
            }
            nameCounts.TryGetValue(n, out var c);
            nameCounts[n] = c + 1;
        }

        foreach (var row in EnvironmentVariables)
        {
            row.ErrorMessage = ComputeEnvError(row, nameCounts);
        }

        SaveError = string.Empty;
        _envRowsValid = null; // invalidate; CanSave recomputes lazily
        OnPropertyChanged(nameof(CanSave));
    }

    private string ComputeEnvError(EnvVarRow row, IReadOnlyDictionary<string, int> nameCounts)
    {
        var name = row.Name ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            return _localization["LaunchSettings_ErrNameRequired"];
        }

        if (name.IndexOf('=') >= 0 || name.IndexOf('\0') >= 0)
        {
            return _localization["LaunchSettings_ErrNameInvalid"];
        }

        if (LaunchSettings.ReservedEnvironmentNames.Contains(name))
        {
            return _localization.Format("LaunchSettings_ErrNameReserved", name);
        }

        if (nameCounts.TryGetValue(name.Trim(), out var count) && count > 1)
        {
            return _localization["LaunchSettings_ErrNameDuplicate"];
        }

        var value = row.Value ?? string.Empty;
        if (value.IndexOf('\0') >= 0)
        {
            return _localization["LaunchSettings_ErrValueInvalid"];
        }

        return string.Empty;
    }

    /// <summary>Computes whether every env row is valid (no inline error).</summary>
    private bool ComputeEnvRowsValid()
    {
        foreach (var row in EnvironmentVariables)
        {
            if (!string.IsNullOrEmpty(row.ErrorMessage))
            {
                _envRowsValid = false;
                return false;
            }
        }
        _envRowsValid = true;
        return true;
    }
}
