namespace Magos.Modificus.EnginseerClient;

/// <summary>
/// The platform the launch path branches on: Windows = native launcher, Linux =
/// Proton-wrapped. Production resolves this once from the runtime OS (via
/// <see cref="System.Runtime.InteropServices.RuntimeInformation"/>); tests force
/// it to exercise both branches on any CI OS. Darktide ships on Windows (native)
/// and Linux (Proton) only.
/// </summary>
internal enum LaunchPlatform
{
    /// <summary>Windows: launch <c>magos_launcher.exe</c> directly (native).</summary>
    Windows,

    /// <summary>Linux: invoke <c>&lt;proton&gt; run magos_launcher.exe</c> with the Steam compat env vars.</summary>
    Linux,
}
