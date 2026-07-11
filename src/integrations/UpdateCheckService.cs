using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Default <see cref="IUpdateCheckService"/>. Queries the Nexus v2 GraphQL
/// <c>modsByUid</c> batch endpoint for the update status of the active
/// profile's <see cref="LatestPolicy"/> + <see cref="NexusSource"/> mods via
/// <see cref="INexusClient"/>. Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>One API call, all mods.</b> The v2 <c>modsByUid</c> query takes a batch
/// of mod UIDs and returns the server-computed
/// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> field for each: true if
/// the mod has been updated since the viewer (current user) last downloaded it.
/// This eliminates the v1 approach's Month-endpoint intersect, cross-endpoint
/// timestamp tolerance, per-mod reconciliation, and reconciliation pinning. The
/// server tracks the user's downloads and computes the signal directly.</para>
/// <para>
/// <b>Two check shapes, identical logic.</b> Both <see cref="CheckAsync"/>
/// (the periodic / profile-load path) and <see cref="CheckThoroughAsync"/> (the
/// manual "check now" path) run the same v2 batch query; they differ only in
/// the result's <see cref="UpdateCheckResult.Thorough"/> flag (kept for
/// interface compatibility + the mod-list UI's result-surface contract).</para>
/// <para>
/// <b>Best-effort, never throws (except cancellation).</b> A transient API
/// failure, a missing auth config, or an exhausted rate limit all surface as an
/// empty result (with <see cref="UpdateCheckResult.RateLimited"/> set when
/// applicable). The caller fires-and-forgets the check; there is nothing for it
/// to catch. Cancellation (<see cref="OperationCanceledException"/>) propagates
/// so a cancelled check is not misreported as "no updates found".</para>
/// <para>
/// <b>Nexus-only.</b> GitHub-sourced mods are skipped alongside Untracked
/// + Pinned. The service has no GitHub code paths anywhere (the
/// <see cref="IGitHubClient"/> is never touched).</para>
/// </remarks>
internal sealed class UpdateCheckService : IUpdateCheckService
{
    /// <summary>
    /// The Darktide Nexus game domain. Fixed: Curator supports only Darktide, so
    /// there is no config key for it (introducing one would imply multi-game
    /// support that does not exist).
    /// </summary>
    private const string GameDomain = "warhammer40kdarktide";

    /// <summary>
    /// The Darktide Nexus game id (used for v2 GraphQL UID computation:
    /// <c>uid = game_id * 2^32 + mod_id</c>). Fixed for the same reason as
    /// <see cref="GameDomain"/>.
    /// </summary>
    private const int GameId = 4943;

    private readonly INexusClient _nexus;
    private readonly IProfileService _profiles;
    private readonly IModRepository _repository;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<UpdateCheckService> _logger;

    /// <summary>
    /// Guards the <see cref="LastResult"/> write + the
    /// <see cref="CheckCompleted"/> invocation so an event subscriber observes
    /// the result that was just published. See
    /// <see cref="IUpdateCheckService.LastResult"/> for the full rationale.
    /// </summary>
    /// <remarks>
    /// <see cref="CheckCompleted"/> is invoked inside this lock. Subscribers
    /// must not synchronously call back into the service in a way that re-enters
    /// a check (it would contest this lock); the badge refresh is expected to
    /// marshal to the UI thread, which avoids the re-entry. The lock body is
    /// one assignment + one invoke, so the hold time is minimal.
    /// </remarks>
    private readonly object _publishLock = new();

    public UpdateCheckService(
        INexusClient nexus,
        IProfileService profiles,
        IModRepository repository,
        IConfigLoader configLoader,
        ILogger<UpdateCheckService> logger)
    {
        _nexus = nexus ?? throw new ArgumentNullException(nameof(nexus));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public UpdateCheckResult? LastResult { get; private set; }

    /// <inheritdoc />
    public event EventHandler<UpdateCheckResult?>? CheckCompleted;

    /// <inheritdoc />
    public Task<UpdateCheckResult> CheckAsync(Guid profileId, CancellationToken ct = default)
        => RunCheckAsync(profileId, thorough: false, ct);

    /// <inheritdoc />
    public Task<UpdateCheckResult> CheckThoroughAsync(Guid profileId, CancellationToken ct = default)
        => RunCheckAsync(profileId, thorough: true, ct);

    /// <summary>
    /// The shared check logic for both <see cref="CheckAsync"/> + 
    /// <see cref="CheckThoroughAsync"/>. Runs the auth gate, the checkable-mods
    /// filter, the single v2 GraphQL batch query, the rate-limit gate, and the
    /// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> mapping, then
    /// publishes the result with the given <paramref name="thorough"/> flag.
    /// </summary>
    private async Task<UpdateCheckResult> RunCheckAsync(
        Guid profileId, bool thorough, CancellationToken ct)
    {
        // 1. Auth gate. No auth configured means the user has not signed in; the
        //    Nexus client would refuse the call, so short-circuit to an empty
        //    result without touching the API. Do not throw: the user simply
        //    hasn't configured Nexus yet.
        var authMethod = _configLoader.Load().Integrations.Nexus.AuthMethod;
        if (authMethod == NexusAuthMethod.None)
        {
            _logger.LogDebug("Update check skipped: Nexus auth not configured.");
            return Publish(EmptyResult(thorough));
        }

        // 2. Profile mods + checkable filter. Let KeyNotFoundException propagate
        //    if the profile id is unknown; the caller owns passing a valid id.
        var entries = _profiles.GetModList(profileId);

        // Filter to checkable mods: LatestPolicy + NexusSource. Skip
        // PinnedPolicy (frozen at a specific release), UntrackedSource (no
        // remote to query), and GitHubSource (out of scope for the update check).
        var checkable = new List<(ModListEntry Entry, ModContainer Container, NexusSource Nexus)>();
        foreach (var entry in entries)
        {
            if (entry.Policy is not LatestPolicy)
            {
                continue;
            }

            var container = _repository.Get(entry.ContainerId);
            if (container is null)
            {
                continue;
            }

            if (container.Source is not NexusSource nexus)
            {
                continue;
            }

            checkable.Add((entry, container, nexus));
        }

        if (checkable.Count == 0)
        {
            _logger.LogDebug(
                "Update check skipped: no checkable Nexus+Latest mods in profile {Profile}.",
                profileId);
            return Publish(EmptyResult(thorough));
        }

        // 3. Query Nexus v2 GraphQL (1 call for all mods). Best-effort: a
        //    non-cancellation failure surfaces as an empty result rather than
        //    propagating to the fire-and-forget caller. Cancellation propagates
        //    so a cancelled check is not misreported as "no updates found".
        //    A NexusRateLimitException is surfaced specifically as a rate-limited
        //    result (not a generic failure) so the UI can show "check incomplete".
        var modIds = checkable.Select(c => c.Nexus.ModId).ToList();
        Response<ModUpdateStatus[]> response;
        try
        {
            response = await _nexus.CheckUpdatesGraphQlAsync(GameId, modIds, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NexusRateLimitException ex)
        {
            _logger.LogInformation(
                ex,
                "Nexus update check rate-limited; reporting rate-limited result.");
            return Publish(new UpdateCheckResult(
                Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: true, Thorough: thorough));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nexus update check failed; reporting empty result.");
            return Publish(EmptyResult(thorough));
        }

        // 4. Rate-limit gate (post-call, on a successful response). Guard
        //    against NexusRateLimits.Unknown (the all-zero fallback when the
        //    rate-limit headers are absent): only treat as rate-limited when a
        //    limit was actually reported AND remaining is zero. A naive
        //    Remaining <= 0 check would false-positive on every response that
        //    did not carry the headers (test stubs, non-rate-limited gateways).
        var limits = response.RateLimits;
        bool rateLimited = (limits.DailyLimit > 0 && limits.DailyRemaining <= 0)
                        || (limits.HourlyLimit > 0 && limits.HourlyRemaining <= 0);
        if (rateLimited)
        {
            _logger.LogInformation(
                "Nexus update check rate-limited (daily {DailyRem}/{DailyLim}, hourly {HourlyRem}/{HourlyLim}).",
                limits.DailyRemaining, limits.DailyLimit,
                limits.HourlyRemaining, limits.HourlyLimit);
            return Publish(new UpdateCheckResult(
                Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: true, Thorough: thorough));
        }

        // 5. Map viewerUpdateAvailable to the flagged list. Index the response
        //    nodes by UID so the per-checkable-mod lookup is O(1). First
        //    occurrence wins: the API should not duplicate, but be defensive.
        var byUid = new Dictionary<long, ModUpdateStatus>();
        if (response.Data is not null)
        {
            foreach (var status in response.Data)
            {
                byUid.TryAdd(status.Uid, status);
            }
        }

        var flagged = new List<ModUpdateInfo>();
        foreach (var (entry, container, nexus) in checkable)
        {
            var uid = (long)GameId * 4294967296L + nexus.ModId;
            if (!byUid.TryGetValue(uid, out var status))
            {
                // The API did not return a node for this UID (invalid id,
                // removed mod, or a UID that did not resolve). Conservative:
                // do not flag a mod we cannot confirm an update for.
                continue;
            }

            // viewerUpdateAvailable == true flags the mod; false or null does
            // not. Null (server has no download record for the user, e.g. a
            // manually imported mod) is treated as false.
            if (status.ViewerUpdateAvailable != true)
            {
                continue;
            }

            var resolved = container.ResolveVersion(new LatestPolicy());
            flagged.Add(new ModUpdateInfo(
                entry.ContainerId,
                nexus.ModId,
                container.Name,
                resolved?.VersionString ?? string.Empty,
                status.UpdatedAt));
        }

        _logger.LogInformation(
            "Update check for profile {Profile}: {Count} mod(s) flagged for update.",
            profileId, flagged.Count);
        return Publish(new UpdateCheckResult(
            flagged, DateTimeOffset.UtcNow, RateLimited: false, Thorough: thorough));
    }

    /// <summary>
    /// Sets <see cref="LastResult"/> and raises <see cref="CheckCompleted"/>
    /// atomically (under <see cref="_publishLock"/>) so an event subscriber
    /// observes the result that was just published. Returns the result for
    /// caller convenience.
    /// </summary>
    private UpdateCheckResult Publish(UpdateCheckResult result)
    {
        lock (_publishLock)
        {
            LastResult = result;
            CheckCompleted?.Invoke(this, result);
        }
        return result;
    }

    /// <summary>
    /// A fresh empty non-rate-limited result, stamped at the current time, with
    /// the given <paramref name="thorough"/> flag. Used by every short-circuit
    /// path (no auth, no checkable mods, API failure).
    /// </summary>
    private static UpdateCheckResult EmptyResult(bool thorough) =>
        new(Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false, Thorough: thorough);
}
