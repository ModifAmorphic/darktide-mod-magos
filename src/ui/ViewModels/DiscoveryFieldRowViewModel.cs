using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.UI.Localization;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// One editable discovery-path row shared by the Settings window and the
/// discovery escape-hatch. Carries the immutable <see cref="Field"/> metadata
/// (from <see cref="Settings.DiscoveryField"/>), the localized <see cref="Label"/>
/// (which refreshes on a culture change), and the editable <see cref="Value"/>
/// string the TextBox two-way binds. The browse button (folder / file picker)
/// lives in the view code-behind and sets <see cref="Value"/> directly after a
/// pick; the parent VM decides what a change means: the Settings window writes
/// through immediately (Preferences pattern), the escape-hatch stages the
/// values and writes them all on submit.
/// </summary>
/// <remarks>
/// <para><b>Optional change callback:</b> when supplied, the row invokes it on
/// every genuine Value change (after the initial restore). The Settings VM uses
/// it for its write-through; the escape-hatch VM passes <c>null</c> and reads
/// <see cref="Value"/> at submit time. Either is fine; the row has no opinion
/// about persistence.</para>
/// <para><b>Localized label is live:</b> <see cref="Label"/> resolves through
/// the <see cref="LocalizationService"/> and re-fires on a culture change so a
/// language switch mid-dialog refreshes the field labels alongside the rest of
/// the UI.</para>
/// </remarks>
public sealed class DiscoveryFieldRowViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly Action<DiscoveryFieldRowViewModel>? _onValueChanged;
    private string _value;

    /// <param name="field">The immutable discovery-field metadata.</param>
    /// <param name="initialValue">The pre-filled value (the current override
    /// from config, or empty when none is set). Null is treated as empty.</param>
    /// <param name="localization">The localization service; the label resolves
    /// through it and refreshes on a culture change.</param>
    /// <param name="onValueChanged">Optional callback invoked on every genuine
    /// Value change. Not invoked for the initial value. The Settings VM uses it
    /// for write-through; the escape-hatch VM passes <c>null</c>.</param>
    public DiscoveryFieldRowViewModel(
        Settings.DiscoveryField field,
        string initialValue,
        LocalizationService localization,
        Action<DiscoveryFieldRowViewModel>? onValueChanged = null)
    {
        Field = field;
        _value = initialValue ?? string.Empty;
        _localization = localization;
        _onValueChanged = onValueChanged;
        _localization.PropertyChanged += OnCultureChanged;
    }

    /// <summary>
    /// The immutable field metadata (canonical name, label resx key, browse
    /// kind). Bound by the view to drive the Browse button's picker kind.
    /// </summary>
    public Settings.DiscoveryField Field { get; }

    /// <summary>
    /// The localized human-readable label for the field. Re-resolves on a
    /// culture change (the row subscribes to the localization service).
    /// </summary>
    public string Label => _localization[Field.LabelResxKey];

    /// <summary>
    /// The TextBox value. An empty / whitespace string clears the override
    /// (the parent VM maps empty back to auto-discover by writing
    /// <c>null</c> into <c>Discovery.User*Path</c>). Setting to a new value
    /// invokes the optional change callback after the property-changed event
    /// fires.
    /// </summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                _onValueChanged?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// Detaches the culture-change subscription so this short-lived row is
    /// collectable after its window closes (the localization service is a
    /// singleton that outlives any dialog). The owning VM should call this on
    /// window close for each row.
    /// </summary>
    public void Detach() => _localization.PropertyChanged -= OnCultureChanged;

    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalizationService.Culture) or "Item[]")
        {
            OnPropertyChanged(nameof(Label));
        }
    }
}
