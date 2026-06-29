namespace Magos.Modificus.Config;

/// <summary>
/// Logging-related global configuration. Honored by the logging bootstrap
/// in <c>Magos.Modificus.General</c> when the Serilog logger is built.
/// </summary>
public sealed class LoggingConfig
{
    /// <summary>
    /// The minimum log level, as a Serilog level name
    /// (<c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>,
    /// <c>Error</c>, <c>Fatal</c>). Unknown values fall back to
    /// <c>Information</c>.
    /// </summary>
    public string Level { get; set; } = "Information";

    /// <summary>The file the structured log is written to.</summary>
    public string LogFile { get; set; } = AppPaths.DefaultLogFile;
}
