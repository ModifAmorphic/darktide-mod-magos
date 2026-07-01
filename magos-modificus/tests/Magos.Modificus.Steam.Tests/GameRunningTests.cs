namespace Magos.Modificus.Steam.Tests;

/// <summary>
/// <see cref="ISteamService.IsGameRunning"/> against the injected
/// <see cref="IProcessLookup"/>: known-absent name → false (deterministic on
/// CI), known-present → true. Proves the check is mockable + wired through.
/// </summary>
public sealed class GameRunningTests
{
    [Fact]
    public void Known_absent_process_returns_false()
    {
        using var fx = new SteamFixture();
        // FakeProcessLookup defaults to nothing running.

        Assert.False(fx.Service.IsGameRunning());
    }

    [Fact]
    public void Known_present_process_returns_true()
    {
        using var fx = new SteamFixture();
        fx.Processes.Running.Add("Darktide"); // matches the default GameProcessName

        Assert.True(fx.Service.IsGameRunning());
    }

    [Fact]
    public void Custom_process_name_is_honored()
    {
        // A future Linux-under-Proton refinement might use a different name; prove
        // the option flows through to the lookup.
        using var fx = new SteamFixture(configure: o => o.GameProcessName = "darktide.exe");
        fx.Processes.Running.Add("darktide.exe");

        Assert.True(fx.Service.IsGameRunning());
    }
}
