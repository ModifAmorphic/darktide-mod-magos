namespace Magos.Modificus.Nxm;

/// <summary>
/// Registers / unregisters / queries the OS scheme handler that routes
/// <c>nxm://</c> clicks to the handler exe. A single interface with two
/// platform implementations (Windows writes <c>HKCU\Software\Classes\nxm</c>;
/// Linux writes a <c>.desktop</c> file + <c>xdg-mime default</c>), selected by
/// runtime OS at DI registration time.
/// </summary>
/// <remarks>
/// Stage 1 ships the SERVICE only. The user-facing registration behavior (auto
/// on first run vs. Settings toggle vs. manual) is deferred to Stage 6.
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
}
