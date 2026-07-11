using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Default <see cref="IUpdateCheckService"/>. Orchestrates a Nexus update check
/// across <see cref="INexusClient"/> (the one-call recently-updated list, plus
/// the per-mod reconciliation + thorough pass), <see cref="IProfileService"/>
/// (the active profile's mod list), and <see cref="IModRepository"/>
/// (per-container source + version resolution + the reconciliation pin).
/// Registered as a singleton.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two check shapes, shared Month path.</b> Both <see cref="CheckAsync"/>
/// (the periodic/profile-load path) and <see cref="CheckThoroughAsync"/> (the
/// manual "check now" path) share the auth gate, the checkable-mods filter,
/// the Month API call, the Month rate-limit gate, and the Month intersect
/// (tolerance-suppressed + flagged, with pinning). Both then reconcile the
/// flagged mods via a per-mod <see cref="INexusClient.ListModFilesAsync"/> call
/// (a same-endpoint comparison that clears false positives the Month
/// cross-endpoint jitter produced). The thorough method additionally walks the
/// checkable mods NOT in the Month response + resolves each one's latest MAIN
/// file to compare (catching mods whose latest release predates the Month
/// window). The split keeps the periodic path bounded (1 Month call plus
/// bounded per-mod reconciliation, suppressed by pinning once reconciled) while
/// the manual affordance also catches Month-missed mods.</para>
/// <para>
/// <b>False-positive suppression.</b> The Month endpoint's
/// <c>latest_file_update</c> and the per-mod <c>files.json</c> endpoint's
/// <c>uploaded_timestamp</c> can disagree by 1+ seconds for the same upload.
/// The Month intersect applies a small tolerance
/// (<see cref="MonthTolerance"/>) when <see cref="ModVersion.RemoteUploadedAt"/>
/// is present (the reliable same-endpoint basis), and the per-mod
/// reconciliation resolves the latest MAIN file from the same
/// <c>files.json</c> endpoint <c>RemoteUploadedAt</c> came from, so a flag the
/// tolerance could not suppress is confirmed or cleared by the reliable
/// same-endpoint comparison. A reconciliation pin
/// (<see cref="ModContainer.ReconciledLatestFileUpdate"/>) records the
/// <c>latest_file_update</c> a mod was last reconciled against, so a mod whose
/// Month timestamp hasn't changed is skipped on the next check (no tolerance
/// check, no per-mod call).</para>
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
    /// The tolerance applied when comparing the Month endpoint's
    /// <see cref="ModUpdate.LatestFileUpdateUtc"/> against the imported
    /// version's <see cref="ModVersion.RemoteUploadedAt"/>. The two values come
    /// from different Nexus API endpoints (<c>updated.json</c> vs
    /// <c>files.json</c>) that can disagree by 1+ seconds for the same upload;
    /// a jitter within this window is treated as "no update" and pinned rather
    /// than flagged + reconciled. Only applied when
    /// <see cref="ModVersion.RemoteUploadedAt"/> is non-null (the
    /// <see cref="ModVersion.ImportedAt"/> fallback has no fixed relationship
    /// to the remote publish date, so it keeps a strict <c>&gt;</c> comparison).
    /// </summary>
    private static readonly TimeSpan MonthTolerance = TimeSpan.FromSeconds(10);

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
        // Month path: tolerance-suppress + flag the intersect, then reconcile the
        // flagged mods via a per-mod ListModFilesAsync call (the same-endpoint
        // comparison that clears false positives the Month cross-endpoint jitter
        // produced). Mods outside the Month window are NOT caught here (use
        // CheckThoroughAsync).
        var (shortCircuit, month) = await RunMonthCheckAsync(profileId, ct).ConfigureAwait(false);
        if (shortCircuit is not null)
        {
            return Publish(shortCircuit with { Thorough = false });
        }

        var flagged = FlagFromMonth(month!);
        var rateLimited = await ReconcileFlaggedAsync(month!, flagged, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Update check for profile {Profile}: {Count} mod(s) flagged for update{RateLimited}.",
            profileId, flagged.Count, rateLimited ? " (rate-limited mid-reconciliation)" : string.Empty);
        return Publish(new UpdateCheckResult(
            flagged, DateTimeOffset.UtcNow, RateLimited: rateLimited, Thorough: false));
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckThoroughAsync(Guid profileId, CancellationToken ct = default)
    {
        // Thorough: run the shared Month path (tolerance-suppress + flag +
        // reconcile, same as CheckAsync), then walk the checkable mods NOT in
        // the Month response + resolve each one's latest MAIN file to compare.
        // Catches mods whose latest release predates the Month window.
        var (shortCircuit, month) = await RunMonthCheckAsync(profileId, ct).ConfigureAwait(false);
        if (shortCircuit is not null)
        {
            // Month-call short-circuit (no auth / no checkable / API failure /
            // Month rate-limit). The per-mod pass can't run without the Month
            // response's byModId index, so this is the thorough result too.
            return Publish(shortCircuit with { Thorough = true });
        }

        var flagged = FlagFromMonth(month!);
        var rateLimited = await ReconcileFlaggedAsync(month!, flagged, ct).ConfigureAwait(false);
        // The Month-missed per-mod pass complements the Month intersect +
        // reconciliation: it covers checkable mods NOT in the Month response
        // (FlagFromMonth + ReconcileFlaggedAsync only touch Month-present mods).
        // Skip when reconciliation already hit the rate limit: the per-mod pass
        // issues a ListModFilesAsync call that would immediately re-trip the
        // limit, wasting one round-trip for no result.
        if (!rateLimited)
        {
            rateLimited = await FlagFromPerModLookupAsync(month!, flagged, ct).ConfigureAwait(false)
                || rateLimited;
        }

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
    /// Nexus mod id) and apply the pin check, the tolerance check, or the
    /// strict comparison. Returns the list of mods that exceed the tolerance
    /// (or the strict threshold when <see cref="ModVersion.RemoteUploadedAt"/>
    /// is null) for the caller to reconcile via
    /// <see cref="ReconcileFlaggedAsync"/>. Has a side effect: tolerance-
    /// suppressed mods are pinned (best-effort
    /// <see cref="IModRepository.SetReconciliation"/>) so the next check skips
    /// them entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <see cref="ModUpdate.LatestFileUpdateUtc"/> (NOT
    /// <see cref="ModUpdate.LatestModActivityUtc"/>): the latter counts page
    /// comments / endorsements / edits, not file changes, and would flag mods
    /// that haven't actually gained a new file (false positives).</para>
    /// <para>
    /// The Month endpoint's <see cref="ModUpdate.LatestFileUpdate"/> and the
    /// per-mod <c>files.json</c> endpoint's <see cref="ModFile.UploadedTimestamp"/>
    /// can disagree by 1+ seconds for the same upload (two different API
    /// endpoints). The tolerance (<see cref="MonthTolerance"/>) suppresses that
    /// jitter when <see cref="ModVersion.RemoteUploadedAt"/> is present; the
    /// flagged remainder is reconciled by <see cref="ReconcileFlaggedAsync"/>
    /// using the reliable same-endpoint comparison. The pin
    /// (<see cref="ModContainer.ReconciledLatestFileUpdate"/>) records the raw
    /// <see cref="ModUpdate.LatestFileUpdate"/> (long) a mod was last evaluated
    /// against, so a mod whose Month timestamp hasn't changed is skipped on the
    /// next check.</para>
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

            // Pin check: this container's latest version was already reconciled
            // against this exact latest_file_update value. Skip re-evaluation
            // (no tolerance check, no per-mod call) but carry forward the
            // previous flag state: a mod that was flagged (genuine update found
            // in a prior reconciliation) stays flagged until the user updates
            // (AddVersion clears the pin) or the mod author publishes new
            // activity (latest_file_update changes, breaking the pin match). A
            // mod that was not flagged stays not flagged. Without this
            // carry-forward, rebuilding the flagged list from scratch each
            // check would clear a genuine update's flag whenever the pin
            // matches. Raw long equality: both values are Unix seconds from
            // the same Month endpoint, so no conversion is needed.
            if (container.ReconciledLatestFileUpdate == update.LatestFileUpdate)
            {
                var previous = LastResult?.Updates?.FirstOrDefault(
                    u => u.ContainerId == entry.ContainerId);
                if (previous is not null)
                {
                    flagged.Add(previous);
                }
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

            // Tolerance check: only when RemoteUploadedAt is present (the
            // reliable same-endpoint basis). The Month endpoint's
            // latest_file_update and the imported version's RemoteUploadedAt
            // come from two different endpoints that can disagree by 1+ seconds
            // for the same upload; a jitter within the tolerance is "no update"
            // + pinned, rather than flagged + reconciled. The tolerance is NOT
            // applied when RemoteUploadedAt is null: ImportedAt has no fixed
            // relationship to the remote publish date, so a strict > comparison
            // preserves the prior behavior for legacy imports.
            if (resolved.RemoteUploadedAt is not null
                && update.LatestFileUpdateUtc <= versionDate + MonthTolerance)
            {
                TrySetReconciliation(container.Id, update.LatestFileUpdate);
                continue;
            }

            // Exceeds the tolerance (or no RemoteUploadedAt + strictly newer).
            // Flag for per-mod reconciliation. ReconcileFlaggedAsync resolves
            // the latest MAIN file via the same files.json endpoint
            // RemoteUploadedAt came from, so a false positive (jitter or
            // non-upload activity bumping latest_file_update) is cleared there.
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
    /// Reconciles the mods <see cref="FlagFromMonth"/> flagged by resolving each
    /// one's latest MAIN file via <see cref="INexusClient.ListModFilesAsync"/>
    /// (the same <c>files.json</c> endpoint <see cref="ModVersion.RemoteUploadedAt"/>
    /// came from) and comparing it against the imported version's publish date.
    /// A same-endpoint comparison is reliable, unlike the Month endpoint's
    /// cross-endpoint <c>latest_file_update</c> that produced the flag. Mutates
    /// <paramref name="flagged"/> in place: a false positive (LatestMain not
    /// newer) is removed; a genuine update is kept. Both outcomes pin the
    /// <c>latest_file_update</c> (best-effort) so the next check skips the mod
    /// unless its Month timestamp changes.
    /// </summary>
    /// <returns><c>true</c> if a mid-pass <see cref="NexusRateLimitException"/>
    /// aborted the reconciliation (the caller surfaces
    /// <see cref="UpdateCheckResult.RateLimited"/>). The mods not yet reconciled
    /// stay flagged and are NOT pinned (the next check retries). Other per-mod
    /// failures are logged + the mod stays flagged + unpinned (the pass
    /// continues; one mod's transient failure must not abort the whole pass).
    /// </returns>
    private async Task<bool> ReconcileFlaggedAsync(
        MonthData data, List<ModUpdateInfo> flagged, CancellationToken ct)
    {
        if (flagged.Count == 0)
        {
            return false;
        }

        // Map each flagged entry back to its container + NexusSource for the
        // per-mod ListModFilesAsync call. The flagged list carries ContainerId
        // + ModId but not the container object; data.Checkable has the full
        // tuple.
        var byContainerId = new Dictionary<Guid, (ModListEntry Entry, ModContainer Container, NexusSource Nexus)>();
        foreach (var c in data.Checkable)
        {
            byContainerId[c.Container.Id] = c;
        }

        // Forward iteration with in-place removal: when a false positive is
        // removed, the next entry shifts into the current index (no increment).
        // A rate-limit abort returns immediately, leaving the current + all
        // remaining mods flagged + unpinned.
        int i = 0;
        while (i < flagged.Count)
        {
            var info = flagged[i];

            if (!byContainerId.TryGetValue(info.ContainerId, out var checkable))
            {
                // Should not happen: FlagFromMonth built flagged from
                // data.Checkable. Leave flagged, advance.
                i++;
                continue;
            }
            var (_, container, nexus) = checkable;

            // The raw latest_file_update (Unix seconds) for pinning after
            // reconciliation, regardless of outcome.
            if (!data.ByModId.TryGetValue(info.ModId, out var update))
            {
                // Not in the Month response (should not happen: FlagFromMonth
                // only flags Month-present mods). Leave flagged, advance.
                i++;
                continue;
            }
            var latestFileUpdate = update.LatestFileUpdate;

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
                // Stop the reconciliation + return what's flagged so far. The
                // current mod + all remaining stay flagged + are NOT pinned
                // (the next check retries them).
                _logger.LogInformation(
                    ex,
                    "Update check reconciliation rate-limited at mod {Mod}; keeping {Count} mod(s) flagged so far.",
                    nexus.ModId, flagged.Count);
                return true;
            }
            catch (Exception ex)
            {
                // Transient / per-mod failure: log + leave flagged + do NOT pin
                // (the next check retries). One mod's bad response must not
                // abort the whole pass.
                _logger.LogWarning(
                    ex,
                    "Update check reconciliation: ListModFilesAsync for mod {Mod} failed; leaving flagged.",
                    nexus.ModId);
                i++;
                continue;
            }

            var latest = NexusModFiles.LatestMain(files.Data);
            if (latest is null)
            {
                // No MAIN / non-archived file to compare against. Leave flagged,
                // do not pin (next check retries).
                i++;
                continue;
            }

            var resolved = container.ResolveVersion(new LatestPolicy());
            if (resolved is null)
            {
                // No resolvable version. Leave flagged, do not pin.
                i++;
                continue;
            }

            // Same-endpoint comparison (files.json uploaded_timestamp vs the
            // imported version's RemoteUploadedAt, also from files.json):
            // reliable, unlike the Month-endpoint cross-endpoint comparison
            // that produced the flag. When RemoteUploadedAt is null (a legacy
            // import that predates the field), the ImportedAt fallback is used
            // instead; ImportedAt is NOT same-endpoint (it is when Curator
            // imported the file, not the remote publish date), but it is the
            // established fallback, consistent with FlagFromMonth and
            // FlagFromPerModLookupAsync. A zero UploadedTimestamp (the wire
            // default when absent) would compare as epoch 1970, older than any
            // real import, so it would never confirm an update; that's fine (a
            // stub payload shouldn't produce a false "update confirmed").
            var latestUploaded = DateTimeOffset.FromUnixTimeSeconds(latest.UploadedTimestamp);
            var versionDate = resolved.RemoteUploadedAt ?? resolved.ImportedAt;
            if (latestUploaded > versionDate)
            {
                // Genuine update: keep flagged + pin so the next check skips it
                // unless the Month timestamp changes.
                TrySetReconciliation(container.Id, latestFileUpdate);
                i++;
            }
            else
            {
                // False positive: the Month endpoint's latest_file_update
                // jittered ahead of the imported file's publish date, but the
                // same-endpoint files.json comparison confirms no newer MAIN
                // file. Remove the flag + pin so the next check skips this
                // latest_file_update value.
                TrySetReconciliation(container.Id, latestFileUpdate);
                flagged.RemoveAt(i);
                // Do not increment: the next entry shifted into index i.
            }
        }
        return false;
    }

    /// <summary>
    /// Best-effort wrapper around <see cref="IModRepository.SetReconciliation"/>:
    /// a write failure is logged + swallowed so the check continues with the
    /// in-memory value. A failed pin is harmless (the next check re-evaluates
    /// the mod against the same latest_file_update, at worst re-doing one
    /// <see cref="INexusClient.ListModFilesAsync"/> call).
    /// </summary>
    private void TrySetReconciliation(Guid containerId, long latestFileUpdate)
    {
        try
        {
            _repository.SetReconciliation(containerId, latestFileUpdate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not persist reconciliation pin for container {Id}; the check continues with the in-memory value.",
                containerId);
        }
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
    /// (<see cref="FlagFromMonth"/> / <see cref="ReconcileFlaggedAsync"/> /
    /// <see cref="FlagFromPerModLookupAsync"/>): the checkable mods + the Month
    /// response indexed by mod id.
    /// </summary>
    private sealed record MonthData(
        List<(ModListEntry Entry, ModContainer Container, NexusSource Nexus)> Checkable,
        Dictionary<long, ModUpdate> ByModId);
}
