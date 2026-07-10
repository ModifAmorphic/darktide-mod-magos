namespace Modificus.Curator.UI.AppUpdate;

/// <summary>
/// The no-op <see cref="IAppUpdateService"/> registered when
/// <c>CURATOR_VELOPACK</c> is not defined (Linux builds, and Windows dev builds
/// published without <c>CuratorUseVelopack=true</c>). Every member returns the
/// neutral value: <see cref="IsUpdateSupported"/> is <c>false</c>,
/// <see cref="CurrentVersion"/>, <see cref="LastCheckResult"/>, and
/// <see cref="UpdatePendingRestart"/> are <c>null</c>, the check returns a
/// completed <c>null</c> task, and <see cref="ApplyUpdatesAndRestart"/> is a
/// no-op. <see cref="UpdateStateChanged"/> is never raised (with no behavior
/// there is nothing to signal; the empty accessors keep the interface
/// implementation clean).
/// </summary>
/// <remarks>
/// <see cref="DownloadUpdatesAsync"/> throws <see cref="NotSupportedException"/>
/// rather than silently no-op-ing, because the UI gates the download on
/// <see cref="IsUpdateSupported"/> (always <c>false</c> here): reaching the
/// download path in an unsupported build is a wiring mistake worth surfacing
/// loudly. No logger is needed: there is no behavior to record.
/// </remarks>
internal sealed class NoopAppUpdateService : IAppUpdateService
{
    /// <inheritdoc />
    public bool IsUpdateSupported => false;

    /// <inheritdoc />
    public string? CurrentVersion => null;

    /// <inheritdoc />
    public AppUpdateInfo? LastCheckResult => null;

    /// <inheritdoc />
    public AppUpdateInfo? UpdatePendingRestart => null;

    /// <inheritdoc />
    /// <remarks>Never raised. Empty accessors satisfy the interface without a
    /// backing field.</remarks>
    public event EventHandler? UpdateStateChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default) =>
        Task.FromResult<AppUpdateInfo?>(null);

    /// <inheritdoc />
    public Task DownloadUpdatesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "App self-update is not supported in this build (no Velopack install).");

    /// <inheritdoc />
    public void ApplyUpdatesAndRestart()
    {
        // Intentionally a no-op: self-update is unsupported in this build.
    }
}
