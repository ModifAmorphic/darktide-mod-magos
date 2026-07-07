using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// Default <see cref="IProcessLauncher"/>. Spawns the child via
/// <see cref="Process.Start(ProcessStartInfo)"/> using
/// <see cref="ProcessStartInfo.ArgumentList"/> (argv-correct, no shell), so paths
/// containing spaces survive verbatim and there is no shell-injection surface.
/// Environment overrides are applied directly to the child's environment block.
/// </summary>
/// <remarks>
/// <para>
/// Fire-and-forget: <see cref="Start"/> starts the process and returns without
/// waiting -- the launcher + injected shell own their own lifecycle and the game
/// process is intentionally not tracked in v1.</para>
/// <para>
/// Registered as a singleton via <c>AddRelayClient()</c> with <c>TryAdd</c>
/// so tests (and hosts with a custom launch hook) can pre-register an override --
/// the same pattern the Steam library uses for <c>IProcessLookup</c>.</para>
/// </remarks>
internal sealed class ProcessLauncher : IProcessLauncher
{
    private readonly ILogger<ProcessLauncher> _logger;

    public ProcessLauncher(ILogger<ProcessLauncher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool Start(
        string filePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = filePath,
            // UseShellExecute=false is required both to set environment variables
            // and to use ArgumentList (no shell). The launcher is an .exe even on
            // Linux (Proton runs it), so we never want the OS shell in the middle.
            UseShellExecute = false,
        };

        // ArgumentList quotes/escapes per-platform; callers hand us argv form, so
        // add each entry verbatim. A null entry would corrupt the argv layout --
        // coerce to "" defensively rather than throw (the launch façade never
        // produces nulls, but this stays safe for any IProcessLauncher caller).
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg ?? string.Empty);
        }

        if (environmentVariables is not null)
        {
            foreach (var pair in environmentVariables)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                // Process.Start returns null only when a new process is reusing an
                // already-running one's resources -- rare, but treat as "not started."
                _logger.LogWarning("Process.Start returned null for {File}.", filePath);
                return false;
            }

            _logger.LogInformation(
                "Started process {Pid} for {File} ({Count} arguments).",
                process.Id, filePath, arguments.Count);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or FileNotFoundException
                                       or Win32Exception
                                       or PlatformNotSupportedException)
        {
            // The common start failures: missing file, permission denied
            // (Win32Exception), or no platform support. Never throw -- the service
            // maps a false return to LaunchStatus.Error with a clear message.
            _logger.LogWarning(ex, "Failed to start process {File}.", filePath);
            return false;
        }
    }
}
