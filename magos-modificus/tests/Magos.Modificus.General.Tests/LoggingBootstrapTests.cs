using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.General;

namespace Magos.Modificus.General.Tests;

/// <summary>
/// Proves the structured-logging pipeline is wired and config-honoring: the
/// logger writes to the configured file at or above the configured level.
/// </summary>
public sealed class LoggingBootstrapTests
{
    [Fact]
    public void LoggerFactory_writes_to_configured_file_and_honors_level()
    {
        var dir = Path.Combine(Path.GetTempPath(), "magos-log-" + Guid.NewGuid());
        var logFile = Path.Combine(dir, "sub", "magos.log");

        var config = MagosConfig.CreateDefault();
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
    public void Unknown_level_falls_back_to_information()
    {
        var dir = Path.Combine(Path.GetTempPath(), "magos-log2-" + Guid.NewGuid());
        var logFile = Path.Combine(dir, "magos.log");

        var config = MagosConfig.CreateDefault();
        config.Logging = new LoggingConfig { Level = "NotARealLevel", LogFile = logFile };

        using (var factory = LoggingBootstrap.CreateLoggerFactory(config))
        {
            factory.CreateLogger("test").LogInformation("wrote something");
        }

        Assert.True(File.Exists(logFile));
    }
}
