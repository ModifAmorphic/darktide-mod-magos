using Magos.Modificus.Config;
using Magos.Modificus.General;
using Magos.Modificus.Mods;
using Magos.Modificus.Profiles;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Integrations;

/// <summary>
/// Default <see cref="IUpdateCheckService"/>. Orchestrates a Nexus update check
/// across <see cref="INexusClient"/> (the one-call recently-updated list),
/// <see cref="IProfileService"/> (the active profile's mod list), and
/// <see cref="IModRepository"/> (per-container source + version resolution).
/// Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>One API call per check.</b> The Nexus
/// <c>GET /v1/games/{domain}/mods/updated.json?period=1m</c> endpoint lists
/// every Darktide mod updated in the past month, so the check makes exactly one
/// network call regardless of how many mods the profile has. Per-mod work is
/// pure intersection + comparison against that single response.</para>
/// <para>
/// <b>Best-effort, never throws (except cancellation).</b> A transient API
/// failure, a missing auth config, or an exhausted rate limit all surface as an
/// empty result (with <see cref="UpdateCheckResult.RateLimited"/> set when
/// applicable). The caller fires-and-forgets the check; there is nothing for it
/// to catch. Cancellation (<see cref="OperationCanceledException"/>)
/// propagates so a cancelled check is not misreported as "no updates
/// found".</para>
/// <para>
/// <b>GitHub descoped.</b> GitHub-sourced mods are skipped alongside
/// Untracked + Pinned. The service has no GitHub code paths anywhere (the
/// <see cref="IGitHubClient"/> is never touched).</para>
/// </remarks>
internal sealed class UpdateCheckService : IUpdateCheckService
{
    /// <summary>
    /// The Darktide Nexus game domain. Fixed: Magos supports only Darktide, so
    /// there is no config key for it (introducing one would imply multi-game
    /// support that does not exist).
    /// </summary>
    private const string GameDomain = "warhammer40kdarktide";

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
    /// a check (it would contest this lock); Stage 5's badge refresh is expected
    /// to marshal to the UI thread, which avoids the re-entry. The lock body is
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
    public async Task<UpdateCheckResult> CheckAsync(Guid profileId, CancellationToken ct = default)
    {
        // 1. Auth gate. No auth configured means the user has not signed in; the
        //    Nexus client would refuse the call, so short-circuit to an empty
        //    result without touching the API. Do not throw: the user simply
        //    hasn't configured Nexus yet.
        var authMethod = _configLoader.Load().Integrations.Nexus.AuthMethod;
        if (authMethod == NexusAuthMethod.None)
        {
            _logger.LogDebug("Update check skipped: Nexus auth not configured.");
            return Publish(EmptyResult());
        }

        // 2. Profile mods. Let KeyNotFoundException propagate if the profile id
        //    is unknown; the caller owns passing a valid id.
        var entries = _profiles.GetModList(profileId);

        // 3. Filter to checkable mods: LatestPolicy + NexusSource. Skip
        //    PinnedPolicy (frozen at a specific release), UntrackedSource (no
        //    remote to query), and GitHubSource (descoped from Phase 4).
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
            return Publish(EmptyResult());
        }

        // 4. Query Nexus (1 call). Best-effort: a non-cancellation failure
        //    surfaces as an empty result rather than propagating to the
        //    fire-and-forget caller. Cancellation propagates so a cancelled
        //    check is not misreported as "no updates found".
        Response<ModUpdate[]> response;
        try
        {
            response = await _nexus.ModUpdatesAsync(GameDomain, NexusPeriod.Month, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nexus update check failed; reporting empty result.");
            return Publish(EmptyResult());
        }

        // 5. Rate-limit gate (post-call). Guard against NexusRateLimits.Unknown
        //    (the all-zero fallback when the rate-limit headers are absent):
        //    only treat as rate-limited when a limit was actually reported AND
        //    remaining is zero. A naive Remaining <= 0 check would
        //    false-positive on every response that did not carry the headers
        //    (test stubs, non-rate-limited gateways).
        var limits = response.RateLimits;
        bool rateLimited = (limits.DailyLimit > 0 && limits.DailyRemaining <= 0)
                        || (limits.HourlyLimit > 0 && limits.HourlyRemaining <= 0);
        if (rateLimited)
        {
            _logger.LogInformation(
                "Nexus update check rate-limited (daily {DailyRem}/{DailyLim}, hourly {HourlyRem}/{HourlyLim}); skipping per-mod comparison.",
                limits.DailyRemaining, limits.DailyLimit,
                limits.HourlyRemaining, limits.HourlyLimit);
            return Publish(new UpdateCheckResult(
                Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: true));
        }

        // 6. Intersect + compare. For each checkable mod, find the matching
        //    ModUpdate (by Nexus mod id) and compare the API's latest
        //    file-update time against the imported version's date. Use
        //    LatestFileUpdateUtc (NOT LatestModActivityUtc): the latter counts
        //    page comments/endorsements/edits, not file changes, and would flag
        //    mods that haven't actually gained a new file (false positives).
        var flagged = new List<ModUpdateInfo>();

        // Index the response by mod id so the per-mod lookup is O(1) rather
        // than O(n) per mod (the response may carry dozens of entries).
        var byModId = new Dictionary<long, ModUpdate>();
        if (response.Data is not null)
        {
            foreach (var u in response.Data)
            {
                // First occurrence wins: the API should not duplicate, but be
                // defensive (later duplicates do not overwrite the first).
                byModId.TryAdd(u.ModId, u);
            }
        }

        foreach (var (entry, container, nexus) in checkable)
        {
            // NexusSource.ModId is int; ModUpdate.ModId is long. C# widens the
            // int implicitly on the lookup + comparison.
            if (!byModId.TryGetValue(nexus.ModId, out var update))
            {
                // The mod was not updated in the window; no update to flag.
                continue;
            }

            // Resolve the currently-imported version. For a LatestPolicy mod
            // this is the container's IsLatest version. If null (no versions,
            // or no IsLatest entry), there is nothing to compare against.
            var resolved = container.ResolveVersion(new LatestPolicy());
            if (resolved is null)
            {
                continue;
            }

            if (update.LatestFileUpdateUtc > resolved.ImportedAt)
            {
                flagged.Add(new ModUpdateInfo(
                    entry.ContainerId,
                    nexus.ModId,
                    container.Name,
                    resolved.VersionString,
                    update.LatestFileUpdateUtc));
            }
        }

        // 7. Return + publish.
        _logger.LogInformation(
            "Update check for profile {Profile}: {Count} mod(s) flagged for update.",
            profileId, flagged.Count);
        return Publish(new UpdateCheckResult(flagged, DateTimeOffset.UtcNow, RateLimited: false));
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
    /// A fresh empty non-rate-limited result, stamped at the current time.
    /// Used by every short-circuit path (no auth, no checkable mods, API
    /// failure).
    /// </summary>
    private static UpdateCheckResult EmptyResult() =>
        new(Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false);
}
