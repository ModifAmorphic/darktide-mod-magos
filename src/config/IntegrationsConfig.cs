namespace Modificus.Curator.Config;

/// <summary>
/// External-service integration settings (mod sources). Bound from the
/// <c>Integrations</c> section of <see cref="CuratorConfig"/> by the config loader
/// in <c>Modificus.Curator.General</c>. Every field carries a default so an absent
/// section yields a usable object.
/// </summary>
public sealed class IntegrationsConfig
{
    /// <summary>
    /// Nexus Mods auth + API client settings. The auth method is the user's
    /// explicit choice (set by the Integrations dialog); the auth message factory
    /// selection reads it live, no fallback.
    /// </summary>
    public NexusConfig Nexus { get; set; } = new();
}
