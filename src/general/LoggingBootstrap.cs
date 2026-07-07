using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Modificus.Curator.Config;

namespace Modificus.Curator.General;

/// <summary>
/// Builds the structured-logging pipeline from <see cref="CuratorConfig.Logging"/>.
/// Uses Serilog (console + file sinks) bridged into
/// <c>Microsoft.Extensions.Logging</c>, honoring the configured level and file.
/// The log file is truncated on each startup (no rolling/retention/backup).
/// </summary>
public static class LoggingBootstrap
{
    /// <summary>
    /// Creates an <see cref="ILoggerFactory"/> wired to a Serilog logger that
    /// writes to the console and to <see cref="LoggingConfig.LogFile"/>,
    /// filtered to <see cref="LoggingConfig.Level"/>.
    /// </summary>
    /// <remarks>
    /// Disposing the returned factory disposes the underlying Serilog logger
    /// (flushing the file sink). The Serilog logger is also assigned to
    /// <see cref="Log.Logger"/> for any static/global logging.
    /// </remarks>
    public static ILoggerFactory CreateLoggerFactory(CuratorConfig config)
    {
        var level = ParseLevel(config.Logging.Level);
        var logFile = config.Logging.LogFile;

        // Ensure the log directory exists; the file sink does not create
        // missing parent directories reliably across versions.
        var logDir = System.IO.Path.GetDirectoryName(logFile);
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        // Truncate on each startup: no rolling, retention, or backup -- matches
        // curator_launcher's curator_enginseer.log pattern. The sink then creates a
        // fresh file for this run. File.Delete is a no-op when the file is absent.
        File.Delete(logFile);

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(logFile)
            .CreateLogger();

        Log.Logger = serilogLogger;

        var factory = new LoggerFactory();
        factory.AddSerilog(serilogLogger, dispose: true);
        return factory;
    }

    private static LogEventLevel ParseLevel(string? level) =>
        Enum.TryParse(level, ignoreCase: true, out LogEventLevel parsed)
            ? parsed
            : LogEventLevel.Information;
}
