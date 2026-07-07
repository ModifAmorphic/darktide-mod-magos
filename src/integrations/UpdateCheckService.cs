using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Default <see cref="IUpdateCheckService"/>. Orchestrates a Nexus update check
/// across <see cref="INexusClient"/> (the one-call recently-updated list, plus
/// the per-mod thorough pass), <see cref="IProfileService"/> (the active
/// profile's mod list), and <see cref="IModRepository"/> (per-container source
/// + version resolution). Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two check shapes, shared Month path.</b> Both <see cref="CheckAsync"/>
/// (Month-only, cheap) and <see cref="CheckThoroughAsync"/> (adds a per-mod
/// <see cref="INexusClient.ListModFilesAsync"/> pass for mods the Month response
/// missed) share the auth gate, the checkable-mods filter, the Month API call,
/// the Month rate-limit gate, and the intersect+compare over the Month
/// response. The thorough method then walks the checkable mods NOT in the Month
/// response + resolves each one's latest MAIN file to compare. The split keeps
/// the periodic/profile-load path cheap (1 API call) while letting the manual
/// "check now" affordance catch mods whose latest release predates the Month
/// window.</para>
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
    public async Task<UpdateCheckResult> CheckAsync(Guid profileId, CancellationToken ct = default)
    {
        // Month-only: run the shared Month path + flag the intersect. No
        // per-mod pass (mods outside the Month window are NOT caught here).
        var (shortCircuit, month) = await RunMonthCheckAsync(profileId, ct).ConfigureAwait(false);
        if (shortCircuit is not null)
        {
            return Publish(shortCircuit with { Thorough = false });
        }

        var flagged = FlagFromMonth(month!);
        _logger.LogInformation(
            "Update check for profile {Profile}: {Count} mod(s) flagged for update.",
            profileId, flagged.Count);
        return Publish(new UpdateCheckResult(
            flagged, DateTimeOffset.UtcNow, RateLimited: false, Thorough: false));
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckThoroughAsync(Guid profileId, CancellationToken ct = default)
    {
        // Thorough: run the shared Month path, then walk the checkable mods NOT
        // in the Month response + resolve each one's latest MAIN file to
        // compare. Catches mods whose latest release predates the Month window.
        var (shortCircuit, month) = await RunMonthCheckAsync(profileId, ct).ConfigureAwait(false);
        if (shortCircuit is not null)
        {
            // Month-call short-circuit (no auth / no checkable / API failure /
            // Month rate-limit). The per-mod pass can't run without the Month
            // response's byModId index, so this is the thorough result too.
            return Publish(shortCircuit with { Thorough = true });
        }

        var flagged = FlagFromMonth(month!);
        var rateLimited = await FlagFromPerModLookupAsync(month!, flagged, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Thorough update check for profile {Profile}: {Count} mod(s) flagged for update{RateLimited}.",
            profileId, flagged.Count, rateLimited ? " (rate-limited mid-pass)" : string.Empty);
        return Publish(new UpdateCheckResult(
            flagged, DateTimeOffset.UtcNow, rateLimited, Thorough: true));
    }

    // ---- shared Month-check path -------------------------------------------

    /// <summary>
    /// The shared front-half of both checks: the auth gate, the checkable-mods
    /// filter (LatestPolicy + NexusSource), the single
    /// <see cref="INexusClient.ModUpdatesAsync"/> call, the Month rate-limit
    /// gate, and the by-mod-id index. Returns either a short-circuit result
    /// (non-null <c>shortCircuit</c>, null <c>data</c>) for the no-auth /
    /// no-checkable / API-failure / Month-rate-limit paths, or the intermediate
    /// <paramref name="data"/> (null short-circuit) for the caller to intersect
    /// (+ extend, for the thorough method).
    /// </summary>
    private async Task<(UpdateCheckResult? ShortCircuit, MonthData? Data)> RunMonthCheckAsync(
        Guid profileId, CancellationToken ct)
    {
        // 1. Auth gate. No auth configured means the user has not signed in; the
        //    Nexus client would refuse the call, so short-circuit to an empty
        //    result without touching the API. Do not throw: the user simply
        //    hasn't configured Nexus yet.
        var authMethod = _configLoader.Load().Integrations.Nexus.AuthMethod;
        if (authMethod == NexusAuthMethod.None)
        {
            _logger.LogDebug("Update check skipped: Nexus auth not configured.");
            return (EmptyResult(), null);
        }

        // 2. Profile mods. Let KeyNotFoundException propagate if the profile id
        //    is unknown; the caller owns passing a valid id.
        var entries = _profiles.GetModList(profileId);

        // 3. Filter to checkable mods: LatestPolicy + NexusSource. Skip
        //    PinnedPolicy (frozen at a specific release), UntrackedSource (no
        //    remote to query), and GitHubSource (out of v1 for the update check).
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
            return (EmptyResult(), null);
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
            return (EmptyResult(), null);
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
            return (new UpdateCheckResult(
                Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: true, Thorough: false),
                null);
        }

        // 6. Index the response by mod id so the per-mod lookup is O(1) rather
        //    than O(n) per mod (the response may carry dozens of entries).
        //    First occurrence wins: the API should not duplicate, but be
        //    defensive (later duplicates do not overwrite the first).
        var byModId = new Dictionary<long, ModUpdate>();
        if (response.Data is not null)
        {
            foreach (var u in response.Data)
            {
                byModId.TryAdd(u.ModId, u);
            }
        }

        return (null, new MonthData(checkable, byModId));
    }

    /// <summary>
    /// The Month intersect + compare: for each checkable mod in
    /// <paramref name="data"/>, find the matching <see cref="ModUpdate"/> (by
    /// Nexus mod id) and compare the API's latest file-update time against the
    /// imported version's publish date (<see cref="ModVersion.RemoteUploadedAt"/>
    /// with an <see cref="ModVersion.ImportedAt"/> fallback for versions
    /// imported before that field existed). Mutates + returns
    /// <paramref name="flagged"/> for the caller to extend (thorough) or
    /// publish (Month-only).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ModUpdate.LatestFileUpdateUtc"/> (NOT
    /// <see cref="ModUpdate.LatestModActivityUtc"/>): the latter counts page
    /// comments / endorsements / edits, not file changes, and would flag mods
    /// that haven't actually gained a new file (false positives).
    /// </remarks>
    private List<ModUpdateInfo> FlagFromMonth(MonthData data)
    {
        var flagged = new List<ModUpdateInfo>();
        foreach (var (entry, container, nexus) in data.Checkable)
        {
            // NexusSource.ModId is int; ModUpdate.ModId is long. C# widens the
            // int implicitly on the lookup + comparison.
            if (!data.ByModId.TryGetValue(nexus.ModId, out var update))
            {
                // The mod was not updated in the Month window. The thorough
                // method picks these up via the per-mod lookup; the Month-only
                // method skips them.
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

            // Compare the latest file's publish date against the IMPORTED
            // file's publish date (RemoteUploadedAt), NOT against when Curator
            // imported it (ImportedAt). ImportedAt is whenever the user
            // happened to download the file, which has no relationship to when
            // the file was published on Nexus: reinstalling an older file today
            // would set ImportedAt = now, newer than any past upload, and mask
            // the outdated install. RemoteUploadedAt is captured at acquisition
            // (Integrations owns it; manual imports leave it null). The null
            // fallback preserves the prior behavior for versions imported
            // before this field existed (and for non-Nexus, though non-Nexus
            // are filtered out above).
            var versionDate = resolved.RemoteUploadedAt ?? resolved.ImportedAt;
            if (update.LatestFileUpdateUtc > versionDate)
            {
                flagged.Add(new ModUpdateInfo(
                    entry.ContainerId,
                    nexus.ModId,
                    container.Name,
                    resolved.VersionString,
                    update.LatestFileUpdateUtc));
            }
        }
        return flagged;
    }

    /// <summary>
    /// The thorough per-mod pass: for each checkable mod NOT in the Month
    /// response, call <see cref="INexusClient.ListModFilesAsync"/> + resolve the
    /// latest MAIN / non-archived file via <see cref="NexusModFiles.LatestMain"/>,
    /// then compare that file's upload time against the imported version's
    /// publish date. Mutates <paramref name="flagged"/> in place.
    /// </summary>
    /// <returns><c>true</c> if a mid-pass <see cref="NexusRateLimitException"/>
    /// aborted the walk (the caller surfaces <see cref="UpdateCheckResult.RateLimited"/>).
    /// Other per-mod failures are logged + skipped (the walk continues; one
    /// mod's transient failure must not abort the whole pass).</returns>
    private async Task<bool> FlagFromPerModLookupAsync(MonthData data, List<ModUpdateInfo> flagged, CancellationToken ct)
    {
        foreach (var (entry, container, nexus) in data.Checkable)
        {
            // Skip mods the Month response already covered: the Month intersect
            // (FlagFromMonth) handled them.
            if (data.ByModId.ContainsKey(nexus.ModId))
            {
                continue;
            }

            Response<ModFile[]> files;
            try
            {
                files = await _nexus.ListModFilesAsync(GameDomain, nexus.ModId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NexusRateLimitException ex)
            {
                // Stop the walk + return what's flagged so far. The Month-call
                // rate-limit gate (RunMonthCheckAsync) handles the all-window
                // exhaustion; this is the mid-pass per-mod variant.
                _logger.LogInformation(
                    ex,
                    "Thorough update check rate-limited mid-pass at mod {Mod}; returning {Count} flagged mod(s) so far.",
                    nexus.ModId, flagged.Count);
                return true;
            }
            catch (Exception ex)
            {
                // Transient / per-mod failure: log + continue. One mod's bad
                // response must not abort the whole pass (the user gets the
                // rest of the results).
                _logger.LogWarning(
                    ex,
                    "Thorough update check: ListModFilesAsync for mod {Mod} failed; skipping.",
                    nexus.ModId);
                continue;
            }

            var latest = NexusModFiles.LatestMain(files.Data);
            if (latest is null)
            {
                // No MAIN / non-archived file: nothing to compare against.
                continue;
            }

            var resolved = container.ResolveVersion(new LatestPolicy());
            if (resolved is null)
            {
                continue;
            }

            // Same publish-date basis as FlagFromMonth. A zero UploadedTimestamp
            // (the wire default when absent) would compare as epoch 1970,
            // older than any real import, so it would never flag; that's fine
            // (a stub payload shouldn't produce false positives).
            var latestUploaded = DateTimeOffset.FromUnixTimeSeconds(latest.UploadedTimestamp);
            var versionDate = resolved.RemoteUploadedAt ?? resolved.ImportedAt;
            if (latestUploaded > versionDate)
            {
                flagged.Add(new ModUpdateInfo(
                    entry.ContainerId,
                    nexus.ModId,
                    container.Name,
                    resolved.VersionString,
                    latestUploaded));
            }
        }
        return false;
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
    /// failure). <see cref="UpdateCheckResult.Thorough"/> is false here; the
    /// caller overrides it with <c>with</c> when the thorough method hits a
    /// short-circuit.
    /// </summary>
    private static UpdateCheckResult EmptyResult() =>
        new(Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false, Thorough: false);

    /// <summary>
    /// Intermediate state shared between the Month front-half
    /// (<see cref="RunMonthCheckAsync"/>) and the intersect / per-mod back-half
    /// (<see cref="FlagFromMonth"/> / <see cref="FlagFromPerModLookupAsync"/>):
    /// the checkable mods + the Month response indexed by mod id.
    /// </summary>
    private sealed record MonthData(
        List<(ModListEntry Entry, ModContainer Container, NexusSource Nexus)> Checkable,
        Dictionary<long, ModUpdate> ByModId);
}
