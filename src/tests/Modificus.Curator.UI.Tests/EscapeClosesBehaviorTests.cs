using Avalonia.Input;
using Modificus.Curator.UI.Behaviors;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="EscapeClosesBehavior.ShouldClose"/>: the pure key decision behind
/// the ESC-closes-dialogs behavior. The full KeyDown-to-Close wiring is
/// rendered Avalonia UI (a live Window + a real KeyDown event) and the UI test
/// suite is VM-level with no rendered controls, so it is not exercised here;
/// this helper test plus the reviewer's code inspection cover the behavior.
/// </summary>
public sealed class EscapeClosesBehaviorTests
{
    [Theory]
    [InlineData(Key.Escape, true)]
    public void ShouldClose_returns_true_only_for_escape(Key key, bool expected)
    {
        Assert.Equal(expected, EscapeClosesBehavior.ShouldClose(key));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    [InlineData(Key.A)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Delete)]
    [InlineData(Key.Back)]
    public void ShouldClose_returns_false_for_other_keys(Key key)
    {
        Assert.False(EscapeClosesBehavior.ShouldClose(key));
    }
}
