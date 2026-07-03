using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Magos.Modificus.UI.Behaviors;

/// <summary>
/// Attached behavior: when a <see cref="TextBox"/> becomes visible, focus it
/// and select all its text. Used by the "Manage profiles" editable list so the
/// inline rename / "+ New profile" entry box grabs focus + selects its text the
/// moment it appears; without this the row would show a TextBox the user must
/// still click into, which defeats inline editing.
/// </summary>
/// <remarks>
/// Observes <see cref="Visual.IsVisibleProperty"/> via
/// <see cref="AvaloniaObject.PropertyChanged"/> (a guaranteed, stable API) rather
/// than a visibility-changed event, so the behavior is robust across Avalonia
/// revisions. View-only: not exercised by VM unit tests.
/// </remarks>
public static class FocusOnVisible
{
    /// <summary>When <c>true</c> on a TextBox, focus + select-all when it shows.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(FocusOnVisible));

    static FocusOnVisible()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(TextBox element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TextBox element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            return;
        }

        box.PropertyChanged += OnPropertyChanged;

        // Already visible when attached (re-templated / shown before the
        // property was set): grab focus now rather than waiting for a toggle.
        if (box.IsVisible)
        {
            GrabFocus(box);
        }
    }

    private static void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Property == Visual.IsVisibleProperty && e.NewValue is true)
        {
            GrabFocus(box);
        }
    }

    private static void GrabFocus(TextBox box)
    {
        box.Focus(NavigationMethod.Pointer, KeyModifiers.None);
        box.SelectAll();
    }
}
