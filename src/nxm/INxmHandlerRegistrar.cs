namespace Modificus.Curator.Nxm;

/// <summary>
/// Registers / unregisters / queries the OS scheme handler that routes
/// <c>nxm://</c> clicks to the handler exe. A single interface with two
/// platform implementations (Windows writes <c>HKCU\Software\Classes\nxm</c>;
/// Linux writes a <c>.desktop</c> file + <c>xdg-mime default</c>), selected by
/// runtime OS.
/// </summary>
/// <remarks>
/// Registration is an explicit user action: the register path confirms first
/// (it is a system-wide change that can affect other mod managers), and the
/// unregister path only releases Curator's own registration by re-checking
/// <see cref="IsRegistered"/> before <see cref="Unregister"/>.
/// <see cref="MaintainRegistration"/> runs best-effort after startup but never
/// auto-registers.
/// </remarks>
public interface INxmHandlerRegistrar
{
    /// <summary>
    /// Whether the OS currently routes <c>nxm://</c> to this handler exe.
    /// </summary>
    bool IsRegistered();

    /// <summary>
    /// Registers the handler exe as the OS <c>nxm://</c> handler (per-user; no
    /// elevation required). Throws on an unrecoverable failure (permission
    /// denied, disk error). Best-effort steps (e.g. a missing <c>xdg-mime</c>
    /// on Linux) are logged, not thrown.
    /// </summary>
    void Register();

    /// <summary>
    /// Removes the registration (deletes the registry key / <c>.desktop</c>
    /// file). Best-effort: idempotent on an absent registration.
    /// </summary>
    void Unregister();

    /// <summary>
    /// Best-effort maintenance of an existing Curator-owned registration. Run
    /// once after the process has established single-instance ownership, so the
    /// fatal process-enumeration check has already succeeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Must never claim ownership.</b> This method refreshes the persistent
    /// handler bytes and the AppImage symlink ONLY when Curator already owns the
    /// active <c>nxm://</c> association (the desktop file exists AND
    /// <c>xdg-mime query default</c> reports Curator's exact desktop id). It
    /// must never call <c>xdg-mime default</c>, create the desktop file, or
    /// replace another mod manager's registration. When Curator does not own the
    /// association, the method is a silent no-op.</para>
    /// <para>
    /// <b>Failure is non-fatal.</b> Any error is logged and swallowed, so
    /// maintenance never blocks Curator startup.</para>
    /// <para>
    /// <b>Platform no-ops.</b> Windows has no AppImage-style temporary mount, so
    /// its implementation is a no-op. The standalone Linux layout (no
    /// <c>$APPIMAGE</c>) is also a no-op: the packaged handler path is already
    /// stable. Only an AppImage run performs work.</para>
    /// </remarks>
    void MaintainRegistration();
}
