using System.Runtime.InteropServices;

namespace Magos.Modificus.Nxm;

/// <summary>
/// Resolves on-disk paths for the OS scheme-handler registration. The handler
/// exe ships as a sibling of the main Magos exe, so its path is derived from
/// the running process's <see cref="AppContext.BaseDirectory"/> and the fixed
/// handler assembly name.
/// </summary>
/// <remarks>
/// <c>Magos.Modificus</c> is the main app's assembly name (the UI project's
/// <c>AssemblyName</c>); <c>Magos.NxmHandler</c> is the handler exe's assembly
/// name. Both ship in the same directory.
/// </remarks>
public static class NxmHandlerPaths
{
    /// <summary>The handler exe's base assembly name, without extension.</summary>
    public const string HandlerExeBaseName = "Magos.NxmHandler";

    /// <summary>The Linux app desktop-file id under <c>applications/</c>.</summary>
    public const string LinuxDesktopFileId = "magos-nxm-handler.desktop";

    /// <summary>
    /// Resolves the absolute path of the handler exe for the current OS.
    /// Windows: <c>&lt;AppContext.BaseDirectory&gt;/Magos.NxmHandler.exe</c>;
    /// Linux: <c>&lt;AppContext.BaseDirectory&gt;/Magos.NxmHandler</c>.
    /// </summary>
    public static string GetHandlerExePath() =>
        Path.Combine(AppContext.BaseDirectory, GetHandlerExeName());

    private static string GetHandlerExeName() =>
        OperatingSystem.IsWindows() ? HandlerExeBaseName + ".exe" : HandlerExeBaseName;
}
