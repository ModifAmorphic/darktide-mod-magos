using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Modificus.Curator.UI.Behaviors;

/// <summary>
/// Attached behavior: when <c>IsEnabled="True"</c> on a <see cref="Window"/>,
/// pressing ESC closes that window (the standard desktop "ESC dismisses the
/// topmost modal" convention). ESC-close calls <see cref="Window.Close"/>,
/// exactly the path the shared <c>DialogTitleBar</c> close button takes, so a
/// dialog's result/cancel contracts are unchanged (ESC is equivalent to
/// clicking the title-bar X).
/// </summary>
/// <remarks>
/// Opt-in: applied per-dialog. The main window does not opt in, so ESC never
/// closes it or exits the app. <c>ProgressDialog</c> deliberately opts out too
/// (it is non-closeable by design: <c>DialogTitleBar.ShowClose="False"</c>,
/// guarding in-flight operations whose partial result would be useless).
/// <para>
/// The behavior subscribes to <see cref="InputElement.KeyDown"/>; ESC bubbles
/// up from focused children (TextBox, ComboBox) to the window. Other keys are
/// ignored (never marked handled). The key decision is factored into
/// <see cref="ShouldClose"/> so it is unit-testable without rendering a window;
/// the KeyDown-to-Close wiring is rendered-UI and covered by code inspection.
/// </para>
/// </remarks>
public static class EscapeClosesBehavior
{
    /// <summary>When <c>true</c> on a Window, ESC closes it.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("IsEnabled", typeof(EscapeClosesBehavior));

    static EscapeClosesBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Window>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(Window element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Window element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>
    /// Whether the pressed key should close the window. Pure (no window needed)
    /// so it is unit-testable without rendering a control.
    /// </summary>
    internal static bool ShouldClose(Key key) => key == Key.Escape;

    private static void OnIsEnabledChanged(Window window, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            window.KeyDown += OnKeyDown;
        }
        else
        {
            window.KeyDown -= OnKeyDown;
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        if (ShouldClose(e.Key))
        {
            // Mark handled first so nothing else runs after the close, then
            // close along the same path as the title-bar X button.
            e.Handled = true;
            window.Close();
        }
    }
}
