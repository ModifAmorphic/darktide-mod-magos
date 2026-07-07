namespace Magos.Modificus.Config;

/// <summary>
/// Nexus Mods authentication + API client settings. Bound from the
/// <c>Integrations.Nexus</c> section of <see cref="MagosConfig"/> by the config
/// loader in <c>Magos.Modificus.General</c>. Every field carries a default so an
/// absent section yields a usable object (auth method <c>None</c>, no
/// credentials).
/// </summary>
/// <remarks>
/// <para>
/// <b>Auth method is the user's explicit choice</b> (set by the Integrations
/// dialog: OAuth login sets <see cref="NexusAuthMethod.OAuth"/>; API-key validate
/// sets <see cref="NexusAuthMethod.ApiKey"/>; sign-out resets to
/// <see cref="NexusAuthMethod.None"/>). The auth message factory selection reads
/// <see cref="AuthMethod"/> directly. There is <b>no fallback</b>: if the
/// selected method's credentials are missing or expired, the client surfaces an
/// auth error for that method rather than silently using the other.</para>
/// <para>
/// <b>Switching methods clears the other method's credentials</b> (clean
/// transition, no stale leftovers) via the Integrations dialog.</para>
/// <para>
/// <b>The OAuth client id is a build-time constant</b> (in
/// <c>Magos.Modificus.Integrations.NexusOAuthConstants</c>), not config and not
/// an env var. Magos Modificus has no env-var pattern; do not introduce one.</para>
/// </remarks>
public sealed class NexusConfig
{
    /// <summary>
    /// The Nexus REST API root, without a trailing slash. Defaults to the public
    /// endpoint; override only for testing.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.nexusmods.com";

    /// <summary>
    /// The Nexus OAuth issuer root, without a trailing slash. The OIDC discovery,
    /// authorize, token, and userinfo endpoints hang off this root. Defaults to
    /// the public endpoint; override only for testing.
    /// </summary>
    public string OAuthBaseUrl { get; set; } = "https://users.nexusmods.com";

    /// <summary>
    /// The user's explicit auth-method choice. Read live by the auth message
    /// factory selector on every request. <see cref="NexusAuthMethod.None"/> is
    /// the default (unauthenticated); API calls fail with a clear error in that
    /// state, callers gate on it.
    /// </summary>
    public NexusAuthMethod AuthMethod { get; set; } = NexusAuthMethod.None;

    /// <summary>
    /// The Nexus API key (sent as the <c>apikey</c> header). Set when
    /// <see cref="AuthMethod"/> is <see cref="NexusAuthMethod.ApiKey"/>; cleared
    /// on sign-out or when switching to OAuth. <c>null</c>/whitespace is treated
    /// as "not configured".
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The persisted OAuth tokens, set when <see cref="AuthMethod"/> is
    /// <see cref="NexusAuthMethod.OAuth"/>; cleared on sign-out or when switching
    /// to API key. <c>null</c> is treated as "not authenticated".
    /// </summary>
    public NexusOAuthTokens? OAuth { get; set; }

    /// <summary>
    /// Whether the periodic background update check runs while a profile is
    /// active. <c>true</c> by default. This gates ONLY the periodic timer; the
    /// profile-load check (startup + active-profile switch) and the manual
    /// "check now" button always run regardless of this toggle. Read live by
    /// <c>UpdateCheckRunner</c> on each timer tick, so a dialog change takes
    /// effect without a restart.
    /// </summary>
    public bool AutoUpdateCheckEnabled { get; set; } = true;

    /// <summary>
    /// The periodic update-check interval, in minutes. <c>10</c> by default. The
    /// runner ticks at a fixed fine granularity (1 minute) and fires a check
    /// when this much time has elapsed since the last check (startup, switch,
    /// periodic, or manual), so a runtime change here takes effect on the next
    /// tick. Honored to a 1-minute granularity; values below 1 are clamped.
    /// </summary>
    public int AutoUpdateCheckIntervalMinutes { get; set; } = 10;
}

/// <summary>
/// The user's explicit Nexus auth-method choice. <see cref="None"/> is the
/// default (unauthenticated); the Integrations dialog sets <see cref="OAuth"/>
/// (loopback OIDC login) or <see cref="ApiKey"/> (paste + validate). Selection is
/// explicit, no fallback.
/// </summary>
public enum NexusAuthMethod
{
    /// <summary>
    /// No auth method configured. API calls fail with a clear error; callers
    /// gate on this (e.g. the Integrations dialog shows "Not signed in").
    /// </summary>
    None,

    /// <summary>
    /// OAuth 2.0 / OIDC via loopback redirect (RFC 8252). Sends
    /// <c>Authorization: Bearer &lt;access_token&gt;</c>; refreshes reactively on
    /// a 401 via the persisted refresh token.
    /// </summary>
    OAuth,

    /// <summary>
    /// API key. Sends <c>apikey: &lt;key&gt;</c>. Static, no refresh; a 401
    /// surfaces a clear "API key invalid/expired" error (no OAuth fallback).
    /// </summary>
    ApiKey,
}

/// <summary>
/// Persisted Nexus OAuth tokens (the access token, refresh token, scope, and
/// expiry). Set on a successful login; updated in place on a token refresh;
/// cleared on sign-out. Immutable; refreshes replace the instance wholesale.
/// </summary>
/// <param name="AccessToken">The OAuth bearer access token sent as
/// <c>Authorization: Bearer &lt;token&gt;</c> on every API request.</param>
/// <param name="RefreshToken">The refresh token used to obtain a new access token
/// when the current one expires (401-reactive refresh). May be <c>null</c>
/// when the server did not issue one (rare; effectively single-session).</param>
/// <param name="Scope">The granted scope string (space-delimited). Persisted for
/// diagnostics; not consulted by the client.</param>
/// <param name="ExpiresAt">When the access token expires (UTC). The factory does
/// <b>not</b> proactively refresh before this; it refreshes reactively on the
/// first 401 after expiry.</param>
public sealed record NexusOAuthTokens(
    string AccessToken,
    string? RefreshToken,
    string Scope,
    DateTimeOffset ExpiresAt);
