using Modificus.Curator.UI.AppUpdate;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="NoopAppUpdateService"/> unit tests: every member returns the
/// neutral value, <see cref="NoopAppUpdateService.DownloadUpdatesAsync"/>
/// throws <see cref="NotSupportedException"/> (a wiring mistake worth surfacing
/// loudly), and <see cref="IAppUpdateService.UpdateStateChanged"/> is never
/// raised. The no-op is the default registration when CURATOR_VELOPACK is not
/// defined (non-Velopack builds: standalone Linux, dev builds).
/// </summary>
public sealed class NoopAppUpdateServiceTests
{
    [Fact]
    public void IsUpdateSupported_is_false()
    {
        var svc = new NoopAppUpdateService();
        Assert.False(svc.IsUpdateSupported);
    }

    [Fact]
    public void CurrentVersion_is_null()
    {
        var svc = new NoopAppUpdateService();
        Assert.Null(svc.CurrentVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_returns_null()
    {
        var svc = new NoopAppUpdateService();
        var result = await svc.CheckForUpdatesAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task LastCheckResult_is_null_before_and_after_a_check()
    {
        var svc = new NoopAppUpdateService();
        Assert.Null(svc.LastCheckResult);
        await svc.CheckForUpdatesAsync();
        Assert.Null(svc.LastCheckResult);
    }

    [Fact]
    public void UpdatePendingRestart_is_null()
    {
        var svc = new NoopAppUpdateService();
        Assert.Null(svc.UpdatePendingRestart);
    }

    [Fact]
    public async Task DownloadUpdatesAsync_throws_NotSupported()
    {
        // The UI gates the download on IsUpdateSupported (false here), so
        // reaching the download path is a wiring mistake. Surface it loudly
        // rather than silently no-op-ing.
        var svc = new NoopAppUpdateService();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => svc.DownloadUpdatesAsync());
    }

    [Fact]
    public void ApplyUpdatesAndRestart_does_not_throw()
    {
        var svc = new NoopAppUpdateService();
        // A bare call must not throw; unsupported builds simply do nothing.
        svc.ApplyUpdatesAndRestart();
    }

    [Fact]
    public async Task UpdateStateChanged_is_never_raised()
    {
        // The no-op has no behavior, so there is nothing to signal. Attach a
        // handler, exercise every operation, and assert the handler was never
        // invoked. (The no-op's add/remove are empty accessors, so even the
        // subscription is discarded; this test documents the contract.)
        var svc = new NoopAppUpdateService();
        var raised = 0;
        svc.UpdateStateChanged += (_, _) => raised++;

        await svc.CheckForUpdatesAsync();
        svc.ApplyUpdatesAndRestart();

        Assert.Equal(0, raised);
    }
}
