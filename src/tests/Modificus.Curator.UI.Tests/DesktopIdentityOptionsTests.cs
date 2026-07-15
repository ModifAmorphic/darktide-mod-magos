using Modificus.Curator.UI;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Verifies Curator's explicit X11 WM_CLASS identity without starting Avalonia,
/// loading X11, or requiring <c>DISPLAY</c>. The configured class is the Velopack
/// pack id and the <c>StartupWMClass</c> in the installed/generated desktop
/// entries, so a task manager groups the Curator window under Curator (not
/// Darktide when Curator launched it from its AppImage). The factory returns an
/// <see cref="X11PlatformOptions"/> whose <see cref="X11PlatformOptions.WmClass"/>
/// carries that constant; production binds the same instance via
/// <see cref="AppBuilder.With{T}(T)"/> in <see cref="Program.BuildAvaloniaApp"/>.
/// The factory is a pure construction (it returns the options object without
/// binding it to a window or initializing the X11 platform), so the suite can
/// exercise it on a headless CI runner and on Windows.
/// </summary>
public sealed class DesktopIdentityOptionsTests
{
    [Fact]
    public void WmClass_constant_matches_the_velopack_pack_id()
    {
        Assert.Equal("ModifAmorphic.ModificusCurator", DesktopIdentityOptions.WmClass);
    }

    [Fact]
    public void Build_returns_X11PlatformOptions_with_the_Curator_WmClass()
    {
        var options = DesktopIdentityOptions.Build();

        Assert.NotNull(options);
        Assert.Equal(DesktopIdentityOptions.WmClass, options.WmClass);
    }
}
