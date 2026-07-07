using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;
using Modificus.Curator.General;

namespace Modificus.Curator.General.Tests;

/// <summary>
/// Proves the structured-logging pipeline is wired and config-honoring: the
/// logger writes to the configured file at or above the configured level.
/// </summary>
public sealed class LoggingBootstrapTests
{
    [Fact]
    public void LoggerFactory_writes_to_configured_file_and_honors_level()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-log-" + Guid.NewGuid());
        var logFile = Path.Combine(dir, "sub", "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "Information", LogFile = logFile };

        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            var logger = factory.CreateLogger("test");
            logger.LogInformation("structured hello {Token}", "world");
            logger.LogDebug("this is below the level and should be dropped");
        }

        Assert.True(File.Exists(logFile));
        var contents = File.ReadAllText(logFile);
        Assert.Contains("structured hello", contents);
        Assert.Contains("world", contents);
        Assert.DoesNotContain("below the level", contents);
    }

    [Fact]
    public void Log_file_is_truncated_on_each_startup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-trunc-" + Guid.NewGuid());
        var logFile = Path.Combine(dir, "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "Information", LogFile = logFile };

        // First startup: write a line and flush.
        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            factory.CreateLogger("run1").LogInformation("first run content");
        }
        Assert.Contains("first run content", File.ReadAllText(logFile));

        // Second startup. We don't read the file inside this block: on Windows
        // the Serilog writer holds the file open and File.ReadAllText throws a
        // sharing violation. (If a future test must read a live log, open it via
        // a FileStream with FileShare.ReadWrite.) Truncation is proven by the
        // post-disposal assertion below instead.
        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            factory.CreateLogger("run2").LogInformation("second run content");
        }

        // Truncation proof: the second run's content is present, the first run's
        // is not (append behavior would have kept both).
        var finalContents = File.ReadAllText(logFile);
        Assert.Contains("second run content", finalContents);
        Assert.DoesNotContain("first run content", finalContents);
    }

    [Fact]
    public void Unknown_level_falls_back_to_information()
    {
        var dir = Path.Combine(Path.GetTempPath(), "curator-log2-" + Guid.NewGuid());
        var logFile = Path.Combine(dir, "curator.log");

        var config = CuratorConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "NotARealLevel", LogFile = logFile };

        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            factory.CreateLogger("test").LogInformation("wrote something");
        }

        Assert.True(File.Exists(logFile));
    }
}
