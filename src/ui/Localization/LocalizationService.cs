using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Modificus.Curator.UI.Localization;

/// <summary>
/// The single authority for resolving localized strings at runtime. A singleton
/// (registered in DI) that holds the current UI culture, exposes a string
/// indexer used by every XAML binding, and raises property-changed so bindings
/// refresh live when the culture changes (the dynamic-language path).
/// </summary>
/// <remarks>
/// <para><b>Dynamic switching:</b> the indexer property name is <c>Item[]</c>;
/// raising <see cref="INotifyPropertyChanged.PropertyChanged"/> for
/// <c>"Item[]"</c> tells every Avalonia indexer binding (<c>{Binding [Key],
/// Source=...}</c>) to re-evaluate, so the whole UI updates the moment the
/// culture flips (no restart).</para>
/// <para><b>Resolution:</b> a <see cref="ResourceManager"/> over the neutral
/// <c>Strings.resx</c> resolves by culture; a missing key returns the key itself
/// (graceful: visible in the UI so a missed key is obvious, never throws).</para>
/// <para><b>Thread cultures:</b> the service holds its own culture and resolves
/// strings with it directly. It does <em>not</em> mutate the thread's
/// <see cref="CultureInfo.CurrentUICulture"/> (avoiding surprising global
/// side-effects); only the UI text follows the chosen language.</para>
/// </remarks>
public sealed class LocalizationService : INotifyPropertyChanged
{
    /// <summary>
    /// The property-name used to signal "the whole indexer changed" so every
    /// <c>{Binding [Key]}</c> binding re-evaluates. Avalonia (and WPF) treat
    /// <c>"Item[]"</c> as a wildcard for the indexer property.
    /// </summary>
    private static readonly PropertyChangedEventArgs IndexerChangedArgs = new("Item[]");

    private readonly ResourceManager _manager;
    private CultureInfo _culture;

    /// <summary>
    /// Creates the service over the neutral <c>Strings.resx</c> (the resource's
    /// manifest name is <c>Modificus.Curator.UI.Resources.Strings</c>, i.e.
    /// <c>&lt;default-namespace&gt;.Resources.Strings</c>), starting from the
    /// current UI culture.
    /// </summary>
    public LocalizationService()
    {
        _manager = new ResourceManager(
            "Modificus.Curator.UI.Resources.Strings",
            typeof(LocalizationService).Assembly);
        _culture = CultureInfo.CurrentUICulture;
    }

    /// <summary>
    /// Raised when the indexer re-resolves (i.e. after a culture switch). Bind
    /// the indexer with <c>{Binding [Key], Source={StaticResource Loc}}</c>;
    /// this event refreshes all of them at once.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The current UI culture used to resolve strings. Assigning a different
    /// culture name raises <see cref="PropertyChanged"/> for the indexer so
    /// every bound string refreshes. Unknown / null names keep the current
    /// culture (graceful: a missing translation file does not crash the UI).
    /// </summary>
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            // A null or unparseable name is a no-op: stay on the current culture
            // rather than throwing on a bad config value.
            if (value is null || value.Name == _culture.Name)
            {
                return;
            }

            _culture = value;
            PropertyChanged?.Invoke(this, IndexerChangedArgs);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        }
    }

    /// <summary>
    /// Sets the culture by name (e.g. <c>"en"</c>, <c>"fr"</c>). An empty /
    /// unknown name resolves to the invariant culture (the neutral resx). A
    /// name that parses but matches the current culture is a no-op.
    /// </summary>
    public void SetCulture(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Culture = CultureInfo.InvariantCulture;
            return;
        }

        try
        {
            Culture = CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            // Fall back to the neutral / invariant resources rather than
            // throwing: the user picked a language we don't ship yet.
            Culture = CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// Resolves <paramref name="key"/> for the current culture. A missing key
    /// returns the key itself (visible, never throws).
    /// </summary>
    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            try
            {
                return _manager.GetString(key, _culture) ?? key;
            }
            catch
            {
                return key;
            }
        }
    }

    /// <summary>
    /// Resolves <paramref name="key"/> for the current culture and applies
    /// <see cref="string.Format(IFormatProvider, string, object[])"/> with the
    /// supplied args (using the current culture for any number / date
    /// formatting). Use this for parameterized messages (e.g. the delete
    /// confirmation: <c>"Delete profile {0}?…"</c>).
    /// </summary>
    public string Format(string key, params object[] args)
    {
        var value = this[key];
        return args is { Length: > 0 }
            ? string.Format(_culture, value, args)
            : value;
    }
}
