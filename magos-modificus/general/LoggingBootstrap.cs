using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Magos.Modificus.Config;

namespace Magos.Modificus.General;

/// <summary>
/// Builds the structured-logging pipeline from <see cref="MagosConfig.Logging"/>.
/// Uses Serilog (console + file sinks) bridged into
/// <c>Microsoft.Extensions.Logging</c>, honoring the configured level and file.
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
    public static ILoggerFactory CreateLoggerFactory(MagosConfig config)
    {
        var level = ParseLevel(config.Logging.Level);

        // Ensure the log directory exists; the file sink does not create
        // missing parent directories reliably across versions.
        var logFile = config.Logging.LogFile;
        var logDir = System.IO.Path.GetDirectoryName(logFile);
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

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
