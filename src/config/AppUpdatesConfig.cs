namespace Modificus.Curator.Config;

/// <summary>
/// App self-update settings (the in-app Curator update check). Bound from JSON
/// by the config loader; every field has a sensible default.
/// </summary>
public sealed class AppUpdatesConfig
{
    /// <summary>
    /// Whether Curator checks for a new version of itself on startup.
    /// <c>true</c> by default. Gates ONLY the automatic startup check
    /// (<c>AppUpdateCheckRunner</c>); the manual "Check for Updates" button
    /// in Settings always works regardless. When <c>false</c>, no startup check
    /// runs and the status-strip update notice is suppressed entirely (even if a
    /// manual check populates <c>LastCheckResult</c>, the manual Settings check
    /// stays self-contained with its own Download-and-Restart button). Read live
    /// on startup; the shell also re-reads it when the Settings dialog closes so
    /// the notice visibility tracks a runtime toggle without a restart.
    /// </summary>
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>
    /// Optional override for the Velopack update source: a local directory path
    /// (containing a <c>releases.win.json</c> feed) or a URL. <c>null</c> (the
    /// default) uses the production GitHub Releases source. Used for local update
    /// testing and for self-hosted update feeds; set it in <c>config.json</c>
    /// under <c>AppUpdates</c>. Read once at <c>VelopackAppUpdateService</c>
    /// construction.
    /// </summary>
    public string? SourceOverride { get; set; }
}
