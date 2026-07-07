using Magos.Modificus.Config;
using Magos.Modificus.General;
using Magos.Modificus.Mods;
using Magos.Modificus.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.Integrations.Tests;

/// <summary>
/// Exercises <see cref="UpdateCheckService"/> against canned Nexus responses
/// and in-memory profile/repository fakes: the LatestPolicy + NexusSource
/// filter, the one-API-call-per-check contract, the file-update vs.
/// mod-activity timestamp distinction (via the fakes only ever populating
/// <see cref="ModUpdate.LatestFileUpdate"/>), the rate-limit guard (including
/// the all-zero <see cref="NexusRateLimits.Unknown"/> fallback), the no-auth
/// short-circuit, the no-checkable-mods short-circuit, the best-effort failure
/// path (API exception does not propagate), and the
/// <see cref="IUpdateCheckService.LastResult"/> +
/// <see cref="IUpdateCheckService.CheckCompleted"/> publishing contract.
/// </summary>
public sealed class UpdateCheckServiceTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();

    private static readonly Guid NexusLatestContainer = Guid.NewGuid();
    private static readonly Guid NexusUnlistedContainer = Guid.NewGuid();
    private static readonly Guid NexusPinnedContainer = Guid.NewGuid();
    private static readonly Guid UntrackedContainer = Guid.NewGuid();
    private static readonly Guid GitHubContainer = Guid.NewGuid();

    private const int UpdatedModId = 100;
    private const int UnlistedModId = 200;
    private const int PinnedModId = 300;

    // ---- happy path + flagging --------------------------------------------

    [Fact]
    public async Task CheckAsync_flags_only_nexus_latest_mods_with_newer_file_update()
    {
        // Three mods in the profile:
        //  (a) Nexus + Latest, in the API response with a newer file update -> flagged.
        //  (b) Nexus + Latest, NOT in the API response -> not flagged.
        //  (c) Nexus + Pinned, in the API response with a newer file update -> not flagged (pinned skipped).
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerUpdate = importedAt.AddDays(1);

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[]
                {
                    Update(UpdatedModId, newerUpdate), // (a) matches + newer
                    Update(PinnedModId, newerUpdate),  // (c) matches + newer, but pinned
                },
                NexusRateLimits.Unknown),
        };

        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Updated Mod", importedAt, "1.0");
        repository.Containers[NexusUnlistedContainer] =
            NexusContainer(NexusUnlistedContainer, UnlistedModId, "Unlisted Mod", importedAt, "1.0");
        repository.Containers[NexusPinnedContainer] =
            NexusContainer(NexusPinnedContainer, PinnedModId, "Pinned Mod", importedAt, "1.0");

        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(NexusUnlistedContainer, new LatestPolicy()),
                Entry(NexusPinnedContainer, new PinnedPolicy("v1")),
            },
        };

        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        Assert.Equal(UpdatedModId, flagged.ModId);
        Assert.Equal("Updated Mod", flagged.ModName);
        Assert.Equal("1.0", flagged.CurrentVersion);
        Assert.Equal(newerUpdate, flagged.LatestUpdateAt);
        Assert.False(result.RateLimited);

        // Exactly 1 API call regardless of how many mods are in the profile.
        Assert.Equal(1, nexus.ModUpdatesCallCount);
        // The Darktide domain + Month window are passed through.
        Assert.Equal("warhammer40kdarktide", nexus.LastGameDomain);
        Assert.Equal(NexusPeriod.Month, nexus.LastPeriod);
    }

    [Fact]
    public async Task CheckAsync_with_empty_api_response_returns_empty_updates()
    {
        // Mods exist but none appear in the API response -> nothing to flag.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public async Task CheckAsync_skips_pinned_mods_even_when_api_reports_update()
    {
        // A pinned mod whose ModId is in the API response (with a newer update)
        // is NOT flagged. A second LatestPolicy + Nexus mod (NOT in the
        // response) forces the API call to happen, so this test exercises the
        // skip rather than the checkable-is-empty short-circuit.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerUpdate = importedAt.AddDays(1);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(PinnedModId, newerUpdate) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusPinnedContainer] =
            NexusContainer(NexusPinnedContainer, PinnedModId, "Pinned Mod", importedAt);
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Latest Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusPinnedContainer, new PinnedPolicy("v1")),
                Entry(NexusLatestContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        // The API WAS called (the Latest mod forced it), so the pinned mod's
        // ModId WAS in the response and still was not flagged.
        Assert.Equal(1, nexus.ModUpdatesCallCount);
    }

    [Fact]
    public async Task CheckAsync_skips_untracked_mods_and_makes_single_api_call()
    {
        // A Nexus + Latest mod (would flag) PLUS an untracked mod. Only the
        // Nexus one flags; the untracked one is never queried. The 1-API-call
        // contract holds regardless of source mix.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerUpdate = importedAt.AddDays(1);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, newerUpdate) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        repository.Containers[UntrackedContainer] =
            NonNexusContainer(UntrackedContainer, new UntrackedSource(), "Untracked Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(UntrackedContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        Assert.Equal(1, nexus.ModUpdatesCallCount);
    }

    [Fact]
    public async Task CheckAsync_skips_github_mods_and_makes_single_api_call()
    {
        // Analogous to the untracked test: a GitHub-sourced mod is skipped
        // alongside the Nexus check. No GitHub code path is touched.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerUpdate = importedAt.AddDays(1);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, newerUpdate) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        repository.Containers[GitHubContainer] =
            NonNexusContainer(GitHubContainer, new GitHubSource { Owner = "o", Repo = "r" }, "GitHub Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(GitHubContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        Assert.Equal(1, nexus.ModUpdatesCallCount);
    }

    // ---- gating: no auth, no checkable mods --------------------------------

    [Fact]
    public async Task CheckAsync_with_no_auth_returns_empty_without_calling_api()
    {
        // AuthMethod.None -> short-circuit before the API call. Even though the
        // canned response WOULD flag a mod, the API is never hit.
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, DateTimeOffset.UtcNow) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", DateTimeOffset.UtcNow.AddDays(-1));
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository, authMethod: NexusAuthMethod.None);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        Assert.Equal(0, nexus.ModUpdatesCallCount);
    }

    [Fact]
    public async Task CheckAsync_with_no_checkable_mods_returns_empty_without_calling_api()
    {
        // All mods are pinned / untracked / github -> nothing to check -> the
        // API is not called (the checkable list is empty before step 4).
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient(); // unset; would serve an empty default if called
        var repository = new FakeModRepository();
        repository.Containers[NexusPinnedContainer] =
            NexusContainer(NexusPinnedContainer, PinnedModId, "Pinned Mod", importedAt);
        repository.Containers[UntrackedContainer] =
            NonNexusContainer(UntrackedContainer, new UntrackedSource(), "Untracked Mod", importedAt);
        repository.Containers[GitHubContainer] =
            NonNexusContainer(GitHubContainer, new GitHubSource { Owner = "o", Repo = "r" }, "GitHub Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusPinnedContainer, new PinnedPolicy("v1")),
                Entry(UntrackedContainer, new LatestPolicy()),
                Entry(GitHubContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        Assert.Equal(0, nexus.ModUpdatesCallCount);
    }

    // ---- rate-limit handling ----------------------------------------------

    [Fact]
    public async Task CheckAsync_reports_rate_limited_when_daily_remaining_is_zero()
    {
        // DailyRemaining=0 with a real DailyLimit -> rate-limited. The per-mod
        // comparison is skipped even though the one mod WOULD flag.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerUpdate = importedAt.AddDays(1);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, newerUpdate) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 0, DailyReset: null,
                    HourlyLimit: 100, HourlyRemaining: 50, HourlyReset: null)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_reports_rate_limited_when_hourly_remaining_is_zero()
    {
        // Symmetric proof of the rate-limit guard's other window: HourlyRemaining=0
        // with a real HourlyLimit -> rate-limited, even though DailyRemaining is
        // healthy. The boolean expression is symmetric across both windows; this
        // test pins the hourly half so a future edit that drops it is caught.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerUpdate = importedAt.AddDays(1);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, newerUpdate) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 50, DailyReset: null,
                    HourlyLimit: 100, HourlyRemaining: 0, HourlyReset: null)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.True(result.RateLimited);
        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_does_not_false_positive_rate_limit_when_headers_absent()
    {
        // NexusRateLimits.Unknown (all zeros, headers absent) must NOT read as
        // rate-limited. A mod that would otherwise flag IS flagged (the > 0
        // guard on the limit prevents a false positive).
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerUpdate = importedAt.AddDays(1);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, newerUpdate) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.False(result.RateLimited);
        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
    }

    // ---- RemoteUploadedAt vs ImportedAt comparison basis ------------------

    [Fact]
    public async Task CheckAsync_uses_RemoteUploadedAt_and_flags_when_latest_file_is_newer()
    {
        // The operator's Power DI scenario: an older file is imported TODAY
        // (ImportedAt = now) for a mod whose latest file was published in the
        // past. The ImportedAt comparison would conclude "up to date" (now >
        // any past upload); the RemoteUploadedAt comparison correctly flags
        // the older install.
        var publishedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var importedAt = publishedAt.AddMonths(6); // imported much later
        var latestFileUpdate = publishedAt.AddDays(15); // a newer file exists

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, latestFileUpdate) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Power DI", importedAt, "1.1.19", publishedAt);

        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        // ImportedAt > latestFileUpdate (would not flag under the old compare),
        // but RemoteUploadedAt < latestFileUpdate, so the mod IS flagged.
        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        Assert.Equal(latestFileUpdate, flagged.LatestUpdateAt);
    }

    [Fact]
    public async Task CheckAsync_does_not_flag_when_RemoteUploadedAt_is_at_or_after_latest_file()
    {
        // The post-update scenario: a one-click update set the imported
        // version's RemoteUploadedAt to the latest file's publish date. The
        // next check must NOT re-flag (latest is not strictly newer than the
        // installed file's publish date).
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var latestFileUpdate = importedAt.AddDays(1);
        var remoteUploadedAt = latestFileUpdate; // same publish date = up to date

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, latestFileUpdate) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", importedAt, "1.0", remoteUploadedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_falls_back_to_ImportedAt_when_RemoteUploadedAt_is_null()
    {
        // Versions imported before RemoteUploadedAt existed (or non-Nexus, though
        // those are filtered out earlier) fall back to the ImportedAt comparison.
        // A mod whose ImportedAt is older than the latest file IS flagged; one
        // whose ImportedAt is newer is NOT. This preserves the prior behavior.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var latestFileUpdate = importedAt.AddDays(1);

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(UpdatedModId, latestFileUpdate) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        // RemoteUploadedAt omitted -> null.
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        // ImportedAt < latestFileUpdate -> flagged under the fallback compare.
        Assert.Single(result.Updates);
    }

    // ---- publishing + best-effort failure ---------------------------------

    [Fact]
    public async Task CheckAsync_publishes_result_via_LastResult_and_CheckCompleted()
    {
        // Before the first check, LastResult is null. After a check, LastResult
        // is the returned result and CheckCompleted was raised exactly once
        // with that same result.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        Assert.Null(service.LastResult);

        var received = new List<UpdateCheckResult?>();
        service.CheckCompleted += (_, r) => received.Add(r);

        var result = await service.CheckAsync(ProfileId);

        Assert.Same(result, service.LastResult);
        var single = Assert.Single(received);
        Assert.Same(result, single);
    }

    [Fact]
    public async Task CheckAsync_when_api_throws_returns_empty_and_does_not_propagate()
    {
        // A NexusApiException from ModUpdatesAsync is caught: the service
        // returns an empty non-rate-limited result, raises CheckCompleted, and
        // sets LastResult. The caller (fire-and-forget) never sees the throw.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            ModUpdatesThrows = new NexusApiException(500, "server error"),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var received = new List<UpdateCheckResult?>();
        service.CheckCompleted += (_, r) => received.Add(r);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        Assert.Same(result, service.LastResult);
        var single = Assert.Single(received);
        Assert.Same(result, single);
    }

    // ---- thorough vs month-only result flag --------------------------------

    [Fact]
    public async Task CheckAsync_returns_thorough_false()
    {
        // The Month-only path stamps Thorough: false on every result branch
        // (here the empty Month response short-circuits the intersect to empty,
        // but Thorough is still false).
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.False(result.Thorough);
    }

    [Fact]
    public async Task CheckThoroughAsync_returns_thorough_true()
    {
        // The thorough path stamps Thorough: true on every result branch (here
        // the Month response is empty + no per-mod lookups are needed, but
        // Thorough is still true).
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        Assert.True(result.Thorough);
    }

    // ---- CheckThoroughAsync: the Numeric UI scenario ----------------------
    //
    // A Nexus+Latest mod whose latest MAIN file is OLDER than the Month window
    // (so the Month response never lists it) but NEWER than the user's imported
    // version. The Month-only check misses it; the thorough per-mod pass catches
    // it via ListModFilesAsync.

    [Fact]
    public async Task CheckThoroughAsync_flags_mod_outside_month_window_when_latest_main_file_is_newer_than_imported()
    {
        // The Numeric UI scenario: the mod was last updated on Nexus ~5 months
        // ago, so it never appears in the Month response. The user has a year-old
        // version imported. The thorough per-mod lookup resolves the latest MAIN
        // file + compares its upload time against the imported version's
        // RemoteUploadedAt, then flags it.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var importedRemoteUploadedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var latestMainUploaded = importedRemoteUploadedAt.AddMonths(1); // newer than imported

        // Empty Month response: the mod is outside the window.
        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        // The per-mod lookup returns one MAIN file uploaded a month after the
        // imported version.
        nexus.ModFilesResponses[UpdatedModId] = new Response<ModFile[]>(
            new[]
            {
                File(fileId: 50, categoryId: NexusModFiles.MainFileCategory, uploaded: latestMainUploaded),
            },
            NexusRateLimits.Unknown);

        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] = NexusContainer(
            NexusLatestContainer, UpdatedModId, "Numeric UI", importedAt, "1.0", importedRemoteUploadedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        Assert.Equal(UpdatedModId, flagged.ModId);
        Assert.Equal(latestMainUploaded, flagged.LatestUpdateAt);
        Assert.True(result.Thorough);
        Assert.False(result.RateLimited);
        // The per-mod lookup was made exactly once for the one checkable mod
        // NOT in the Month response.
        Assert.Equal(new[] { UpdatedModId }, nexus.ListModFilesModIds);
    }

    [Fact]
    public async Task CheckThoroughAsync_does_not_flag_when_latest_main_file_is_older_than_imported()
    {
        // The thorough pass's negative case: the latest MAIN file is older than
        // (or equal to) the imported version's publish date. Nothing flags.
        var importedAt = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var importedRemoteUploadedAt = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var latestMainUploaded = importedRemoteUploadedAt.AddMonths(-1); // older than imported

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        nexus.ModFilesResponses[UpdatedModId] = new Response<ModFile[]>(
            new[]
            {
                File(fileId: 50, categoryId: NexusModFiles.MainFileCategory, uploaded: latestMainUploaded),
            },
            NexusRateLimits.Unknown);

        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] = NexusContainer(
            NexusLatestContainer, UpdatedModId, "Numeric UI", importedAt, "1.0", importedRemoteUploadedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.True(result.Thorough);
        Assert.False(result.RateLimited);
        // The lookup still ran (the mod was not in the Month response).
        Assert.Equal(new[] { UpdatedModId }, nexus.ListModFilesModIds);
    }

    [Fact]
    public async Task CheckThoroughAsync_skips_per_mod_lookup_for_mods_already_in_month_response()
    {
        // A mod the Month response already covered is NOT re-queried: the Month
        // intersect handled it, so ListModFilesAsync is not called for it. A
        // second mod (not in Month) IS queried. Proves the thorough pass
        // complements the Month pass rather than duplicating it.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var monthUpdate = importedAt.AddDays(1); // would flag the in-Month mod

        var inMonthModId = UpdatedModId; // 100
        var outOfMonthModId = UnlistedModId; // 200
        var outOfMonthLatestMain = importedAt.AddMonths(2); // newer than imported

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                new[] { Update(inMonthModId, monthUpdate) },
                NexusRateLimits.Unknown),
        };
        // Only the out-of-Month mod stages a files response; the in-Month mod
        // would throw if it were queried (proving it isn't).
        nexus.ModFilesResponses[outOfMonthModId] = new Response<ModFile[]>(
            new[]
            {
                File(fileId: 60, categoryId: NexusModFiles.MainFileCategory, uploaded: outOfMonthLatestMain),
            },
            NexusRateLimits.Unknown);

        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, inMonthModId, "InMonth Mod", importedAt);
        var outOfMonthContainer = Guid.NewGuid();
        repository.Containers[outOfMonthContainer] =
            NexusContainer(outOfMonthContainer, outOfMonthModId, "OutOfMonth Mod", importedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(outOfMonthContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        // Both flag (in-Month via the intersect, out-of-Month via the lookup).
        Assert.Equal(2, result.Updates.Count);
        // Only the out-of-Month mod's files were queried.
        Assert.Equal(new[] { outOfMonthModId }, nexus.ListModFilesModIds);
    }

    [Fact]
    public async Task CheckThoroughAsync_skips_archived_and_non_main_files_in_latest_resolution()
    {
        // The latest-MAIN filter (NexusModFiles.LatestMain) excludes archived
        // + non-MAIN entries. A mod whose newest file is archived, with an older
        // non-archived MAIN file, resolves to the older MAIN file. If THAT file
        // is older than the imported version, nothing flags.
        var importedAt = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var importedRemoteUploadedAt = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var archivedMainUploaded = importedRemoteUploadedAt.AddMonths(1); // newer, but archived
        var currentMainUploaded = importedRemoteUploadedAt.AddMonths(-1);  // older, not archived

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        nexus.ModFilesResponses[UpdatedModId] = new Response<ModFile[]>(
            new[]
            {
                File(fileId: 70, categoryId: NexusModFiles.MainFileCategory, archived: true, uploaded: archivedMainUploaded),
                File(fileId: 71, categoryId: NexusModFiles.MainFileCategory, archived: false, uploaded: currentMainUploaded),
                File(fileId: 72, categoryId: 3, archived: false, uploaded: archivedMainUploaded.AddDays(1)), // miscellaneous, not MAIN
            },
            NexusRateLimits.Unknown);

        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] = NexusContainer(
            NexusLatestContainer, UpdatedModId, "Mod", importedAt, "1.0", importedRemoteUploadedAt);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        // The archived MAIN (newer) is excluded; the current MAIN (older than
        // imported) doesn't flag. The miscellaneous file is excluded too.
        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckThoroughAsync_rate_limit_mid_pass_stops_and_returns_partial_results()
    {
        // Two mods, both outside the Month window. The first mod's
        // ListModFilesAsync returns a newer MAIN file (flags); the second
        // throws NexusRateLimitException. The pass stops + the result carries
        // the first mod's flag + RateLimited: true. A third mod (which would
        // also be queried) is never reached.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var importedRemoteUploadedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerMain = importedRemoteUploadedAt.AddMonths(1);

        var firstModId = UpdatedModId;   // 100 - flagged before the rate-limit
        var secondModId = UnlistedModId; // 200 - throws NexusRateLimitException
        var thirdModId = PinnedModId;    // 300 - never reached

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        nexus.ModFilesResponses[firstModId] = new Response<ModFile[]>(
            new[] { File(fileId: 50, categoryId: NexusModFiles.MainFileCategory, uploaded: newerMain) },
            NexusRateLimits.Unknown);
        nexus.ModFilesThrows[secondModId] = new NexusRateLimitException(
            429, NexusRateLimits.Unknown);

        var firstContainer = NexusLatestContainer;
        var secondContainer = Guid.NewGuid();
        var thirdContainer = Guid.NewGuid();

        var repository = new FakeModRepository();
        repository.Containers[firstContainer] = NexusContainer(
            firstContainer, firstModId, "First Mod", importedAt, "1.0", importedRemoteUploadedAt);
        repository.Containers[secondContainer] = NexusContainer(
            secondContainer, secondModId, "Second Mod", importedAt, "1.0", importedRemoteUploadedAt);
        repository.Containers[thirdContainer] = NexusContainer(
            thirdContainer, thirdModId, "Third Mod", importedAt, "1.0", importedRemoteUploadedAt);

        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(firstContainer, new LatestPolicy()),
                Entry(secondContainer, new LatestPolicy()),
                Entry(thirdContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        // Partial: only the first mod flagged before the rate-limit aborted.
        var flagged = Assert.Single(result.Updates);
        Assert.Equal(firstContainer, flagged.ContainerId);
        Assert.True(result.RateLimited);
        Assert.True(result.Thorough);
        // The third mod was never queried (the walk stopped at the second).
        Assert.Equal(2, nexus.ListModFilesModIds.Count);
        Assert.DoesNotContain(thirdModId, nexus.ListModFilesModIds);
    }

    [Fact]
    public async Task CheckThoroughAsync_other_per_mod_failures_are_logged_and_skipped()
    {
        // A non-rate-limit, non-cancellation per-mod failure (e.g. a 500) is
        // logged + skipped; the walk continues. The failing mod is NOT flagged;
        // a later mod in the walk still is.
        var importedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var importedRemoteUploadedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newerMain = importedRemoteUploadedAt.AddMonths(1);

        var failingModId = UpdatedModId;   // 100 - throws NexusApiException (not rate-limit)
        var okModId = UnlistedModId;       // 200 - returns a newer MAIN file

        var nexus = new FakeNexusClient
        {
            ModUpdatesResponse = new Response<ModUpdate[]>(
                Array.Empty<ModUpdate>(), NexusRateLimits.Unknown),
        };
        nexus.ModFilesThrows[failingModId] = new NexusApiException(500, "server error");
        nexus.ModFilesResponses[okModId] = new Response<ModFile[]>(
            new[] { File(fileId: 50, categoryId: NexusModFiles.MainFileCategory, uploaded: newerMain) },
            NexusRateLimits.Unknown);

        var failingContainer = NexusLatestContainer;
        var okContainer = Guid.NewGuid();

        var repository = new FakeModRepository();
        repository.Containers[failingContainer] = NexusContainer(
            failingContainer, failingModId, "Failing Mod", importedAt, "1.0", importedRemoteUploadedAt);
        repository.Containers[okContainer] = NexusContainer(
            okContainer, okModId, "OK Mod", importedAt, "1.0", importedRemoteUploadedAt);

        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(failingContainer, new LatestPolicy()),
                Entry(okContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        // The failing mod was skipped; the OK mod flagged. No rate-limit.
        var flagged = Assert.Single(result.Updates);
        Assert.Equal(okContainer, flagged.ContainerId);
        Assert.False(result.RateLimited);
        Assert.True(result.Thorough);
        // Both mods were queried (the walk continued past the failure).
        Assert.Equal(2, nexus.ListModFilesModIds.Count);
    }

    [Fact]
    public async Task CheckThoroughAsync_no_auth_short_circuits_with_thorough_true()
    {
        // The Month-call short-circuit (no auth) propagates to the thorough
        // result too: the per-mod pass can't run without the byModId index, so
        // the result is empty + Thorough: true (the thorough method was the
        // entry point even though it short-circuited at the auth gate).
        var nexus = new FakeNexusClient(); // never called
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", DateTimeOffset.UtcNow);
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository, authMethod: NexusAuthMethod.None);

        var result = await service.CheckThoroughAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.True(result.Thorough);
        Assert.False(result.RateLimited);
        Assert.Equal(0, nexus.ModUpdatesCallCount);
        Assert.Empty(nexus.ListModFilesModIds);
    }

    // ---- helpers + fakes ---------------------------------------------------

    /// <summary>
    /// Builds a Nexus-sourced <see cref="ModContainer"/> with a single
    /// IsLatest version imported at <paramref name="importedAt"/>.
    /// </summary>
    private static ModContainer NexusContainer(
        Guid id, int modId, string name, DateTimeOffset importedAt, string version = "1.0",
        DateTimeOffset? remoteUploadedAt = null) =>
        new()
        {
            Id = id,
            Source = new NexusSource { ModId = modId },
            Name = name,
            Versions = new[]
            {
                new ModVersion
                {
                    Folder = "v1",
                    VersionString = version,
                    IsLatest = true,
                    ImportedAt = importedAt,
                    RemoteUploadedAt = remoteUploadedAt,
                },
            },
        };

    /// <summary>
    /// Builds a non-Nexus (untracked or github) <see cref="ModContainer"/> with
    /// a single IsLatest version. Used to prove non-Nexus sources are skipped.
    /// </summary>
    private static ModContainer NonNexusContainer(
        Guid id, ModSource source, string name, DateTimeOffset importedAt) =>
        new()
        {
            Id = id,
            Source = source,
            Name = name,
            Versions = new[]
            {
                new ModVersion
                {
                    Folder = "v1",
                    VersionString = "1.0",
                    IsLatest = true,
                    ImportedAt = importedAt,
                },
            },
        };

    private static ModListEntry Entry(Guid containerId, ModVersionPolicy policy) =>
        new() { ContainerId = containerId, Enabled = true, Order = 0, Policy = policy };

    /// <summary>
    /// Builds a <see cref="ModUpdate"/> for <paramref name="modId"/> whose
    /// <see cref="ModUpdate.LatestFileUpdateUtc"/> equals
    /// <paramref name="latestFileUpdateUtc"/>. Only the file-update timestamp
    /// is populated (the mod-activity timestamp is left at zero), proving the
    /// service reads file-update, not mod-activity.
    /// </summary>
    private static ModUpdate Update(int modId, DateTimeOffset latestFileUpdateUtc) =>
        new()
        {
            ModId = modId,
            LatestFileUpdate = latestFileUpdateUtc.ToUnixTimeSeconds(),
        };

    /// <summary>
    /// Builds a <see cref="ModFile"/> with the given category / archive flag /
    /// upload time (Unix seconds derived from <paramref name="uploaded"/>).
    /// Used to stage per-mod <c>files.json</c> responses for the thorough pass.
    /// </summary>
    private static ModFile File(
        int fileId, int categoryId, DateTimeOffset uploaded, bool archived = false, string version = "1.0") =>
        new()
        {
            FileId = fileId,
            CategoryId = categoryId,
            IsArchived = archived,
            UploadedTimestamp = uploaded.ToUnixTimeSeconds(),
            Version = version,
            FileName = fileId + ".zip",
            Name = "file " + fileId,
        };

    /// <summary>
    /// Constructs the service with the given fakes + a <see cref="FakeConfigLoader"/>
    /// whose Nexus auth method is <paramref name="authMethod"/> (ApiKey by
    /// default, so the auth gate passes and the API is reached).
    /// </summary>
    private static UpdateCheckService CreateService(
        FakeNexusClient nexus,
        FakeProfileService profiles,
        FakeModRepository repository,
        NexusAuthMethod authMethod = NexusAuthMethod.ApiKey)
    {
        var config = MagosConfig.CreateDefault();
        config.Integrations.Nexus.AuthMethod = authMethod;
        var configLoader = new FakeConfigLoader { Config = config };
        return new UpdateCheckService(
            nexus, profiles, repository, configLoader, NullLogger<UpdateCheckService>.Instance);
    }

    /// <summary>
    /// A configurable <see cref="INexusClient"/> stub. <see cref="ModUpdatesAsync"/>
    /// is exercised by both check shapes; <see cref="ListModFilesAsync"/> is
    /// exercised by the thorough pass. Other members throw. Tracks the call
    /// counts + the args for the 1-call contract + domain/window assertions.
    /// </summary>
    private sealed class FakeNexusClient : INexusClient
    {
        public Response<ModUpdate[]>? ModUpdatesResponse { get; set; }
        public Exception? ModUpdatesThrows { get; set; }
        public int ModUpdatesCallCount { get; private set; }
        public string? LastGameDomain { get; private set; }
        public NexusPeriod? LastPeriod { get; private set; }

        // Per-mod files responses (keyed by mod id). A mod not in the
        // dictionary returns an empty files list (no MAIN file -> nothing to
        // flag). A mod in ModFilesThrows throws instead (used for the
        // mid-pass rate-limit + the per-mod failure tests).
        public Dictionary<int, Response<ModFile[]>> ModFilesResponses { get; } = new();
        public Dictionary<int, Exception> ModFilesThrows { get; } = new();
        public List<int> ListModFilesModIds { get; } = new();

        public Task<Response<ModUpdate[]>> ModUpdatesAsync(
            string gameDomain, NexusPeriod period, CancellationToken ct = default)
        {
            ModUpdatesCallCount++;
            LastGameDomain = gameDomain;
            LastPeriod = period;
            if (ModUpdatesThrows is not null)
            {
                return Task.FromException<Response<ModUpdate[]>>(ModUpdatesThrows);
            }
            return Task.FromResult(
                ModUpdatesResponse
                    ?? new Response<ModUpdate[]>(Array.Empty<ModUpdate>(), NexusRateLimits.Unknown));
        }

        public Task<Response<ModFile[]>> ListModFilesAsync(
            string gameDomain, int modId, CancellationToken ct = default)
        {
            ListModFilesModIds.Add(modId);
            if (ModFilesThrows.TryGetValue(modId, out var ex))
            {
                return Task.FromException<Response<ModFile[]>>(ex);
            }
            if (ModFilesResponses.TryGetValue(modId, out var response))
            {
                return Task.FromResult(response);
            }
            return Task.FromResult(new Response<ModFile[]>(Array.Empty<ModFile>(), NexusRateLimits.Unknown));
        }

        public Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<OAuthUserInfo>> GetOAuthUserInfoAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<DownloadLink[]>> DownloadLinksAsync(
            string gameDomain, int modId, int fileId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<DownloadLink[]>> DownloadLinksAsync(
            string gameDomain, int modId, int fileId,
            string nxmKey, long expiresEpoch, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<ModInfo>> GetModInfoAsync(
            string gameDomain, int modId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// A configurable <see cref="IProfileService"/> stub. Only
    /// <see cref="GetModList"/> is exercised by the service; the other members
    /// throw.
    /// </summary>
    private sealed class FakeProfileService : IProfileService
    {
        public IReadOnlyList<ModListEntry> Mods { get; set; } = Array.Empty<ModListEntry>();

        // Unused stub; only GetModList is exercised. Required by the interface.
        public event EventHandler<ProfileSummary>? ProfileCreated
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ModListEntry> GetModList(Guid id) => Mods;

        public IReadOnlyList<ProfileSummary> ListProfiles()
            => throw new NotImplementedException();
        public Profile GetProfile(Guid id) => throw new NotImplementedException();
        public Profile CreateProfile(string name) => throw new NotImplementedException();
        public void RenameProfile(Guid id, string newName) => throw new NotImplementedException();
        public void DeleteProfile(Guid id) => throw new NotImplementedException();
        public void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder)
            => throw new NotImplementedException();
        public void SetModEnabled(Guid id, Guid containerId, bool enabled)
            => throw new NotImplementedException();
        public void AddMod(Guid id, Guid containerId, ModVersionPolicy policy)
            => throw new NotImplementedException();
        public void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy)
            => throw new NotImplementedException();
        public void RemoveMod(Guid id, Guid containerId)
            => throw new NotImplementedException();
        public ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId)
            => throw new NotImplementedException();
        public string PrepareModRoot(Guid id) => throw new NotImplementedException();
    }

    /// <summary>
    /// A configurable <see cref="IModRepository"/> stub. Only
    /// <see cref="Get(Guid)"/> is exercised by the service; the other members
    /// throw. Lookups are backed by an in-memory dictionary keyed by container
    /// id.
    /// </summary>
    private sealed class FakeModRepository : IModRepository
    {
        public Dictionary<Guid, ModContainer> Containers { get; } = new();

        public ModContainer? Get(Guid containerId) =>
            Containers.TryGetValue(containerId, out var c) ? c : null;

        public IReadOnlyList<ModContainer> List() => throw new NotImplementedException();
        public ModContainer? FindBySource(ModSource source) => throw new NotImplementedException();
        public ModContainer? FindUntrackedByName(string name) => throw new NotImplementedException();
        public ModContainer CreateContainer(ModSource source, string name)
            => throw new NotImplementedException();
        public ModContainer AddVersion(
            Guid containerId, string versionString, Action<string> populateFolder, DateTimeOffset? remoteUploadedAt = null)
            => throw new NotImplementedException();
        public void RemoveVersion(Guid containerId, string versionFolder)
            => throw new NotImplementedException();
        public string GetVersionFolderPath(Guid containerId, string versionFolder)
            => throw new NotImplementedException();
        public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced)
            => throw new NotImplementedException();
        public void Rescan() => throw new NotImplementedException();
        public void Relocate(string newBasePath) => throw new NotImplementedException();
    }
}
