using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace Magos.Modificus.UI.Converters;

/// <summary>
/// A multi-value converter that ANDs every bound value (treating non-bool /
/// null / unset values as <c>false</c>). Used for the per-row Update button's
/// visibility, which combines list-level state (the parent
/// <c>ModListViewModel.IsPremiumUser</c>) with row-level state
/// (<c>IsNexusLatest</c> + <c>UpdateAvailable</c>): a single Avalonia compiled
/// binding cannot express a cross-VM conjunction, so the view binds all the
/// inputs to a <c>MultiBinding</c> over this converter.
/// </summary>
/// <remarks>
/// Used only for the Update button's <c>IsVisible</c>. The <c>IsEnabled</c>
/// binds directly to the parent's pre-computed <c>IsUpdateEnabled</c> (a single
/// ReflectionBinding, not a MultiBinding). A bound <c>false</c> short-circuits
/// the result to <c>false</c>; an empty binding list returns <c>true</c> (the
/// identity of the AND), though no call site relies on that.
/// </remarks>
public class BoolAllConverter : MarkupExtension, IMultiValueConverter
{
    /// <summary>
    /// ANDs every value. Non-bool values (including <c>null</c> +
    /// <c>AvaloniaProperty.UnsetValue</c> for a failed binding) count as
    /// <c>false</c>, so a binding error collapses the button rather than
    /// showing it spuriously.
    /// </summary>
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is bool b && !b)
            {
                return false;
            }
            if (values[i] is not bool)
            {
                // null, UnsetValue, or a non-bool binding result: treat as
                // false so a failed binding collapses the button.
                return false;
            }
        }
        return true;
    }

    /// <summary>Allows <c>{Converters:BoolAllConverter}</c> in AXAML.</summary>
    public override object ProvideValue(IServiceProvider serviceProvider) => Instance;

    /// <summary>A shared instance (stateless converter).</summary>
    public static readonly BoolAllConverter Instance = new();
}
