using Modificus.Curator.Nxm;

namespace Modificus.Curator.NxmHandler;

/// <summary>
/// The OS-registered nxm:// scheme handler entry point. A one-liner: all
/// orchestration (URL extraction, IPC connect-or-launch, cold-start retry) lives
/// in <see cref="NxmHandlerRelay.RunAsync"/> so it is unit-testable without a
/// real process or pipe.
/// </summary>
/// <remarks>
/// The handler never parses <c>nxm://</c> semantics itself. It forwards the raw
/// URL string to the running Curator over the fixed named pipe; Curator owns URL
/// semantics via <see cref="NxmUrlParser"/> + <see cref="INxmRouter"/>.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args) =>
        await NxmHandlerRelay.RunAsync(args).ConfigureAwait(false);
}
