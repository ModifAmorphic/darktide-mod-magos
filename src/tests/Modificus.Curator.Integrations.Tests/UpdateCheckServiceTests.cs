using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Exercises <see cref="UpdateCheckService"/> against canned v2 GraphQL
/// responses and in-memory profile/repository fakes: the LatestPolicy +
/// NexusSource filter, the one-API-call-per-check contract, the
/// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> mapping (true flags,
/// false + null do not), the rate-limit guard (thrown exception + header-based,
/// including the all-zero <see cref="NexusRateLimits.Unknown"/> fallback), the
/// no-auth short-circuit, the no-checkable-mods short-circuit, the best-effort
/// failure path (API exception does not propagate), and the
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

    // Must match UpdateCheckService.GameId (Darktide = 4943).
    private const int GameId = 4943;

    // ---- happy path + flagging --------------------------------------------

    [Fact]
    public async Task CheckAsync_flags_mods_where_viewerUpdateAvailable_is_true()
    {
        // Three mods in the profile (all Nexus + Latest):
        //  (a) viewerUpdateAvailable = true  -> flagged.
        //  (b) viewerUpdateAvailable = true  -> flagged.
        //  (c) viewerUpdateAvailable = false -> not flagged.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[]
                {
                    Status(UpdatedModId, viewerUpdateAvailable: true),
                    Status(UnlistedModId, viewerUpdateAvailable: true),
                    Status(PinnedModId, viewerUpdateAvailable: false),
                },
                NexusRateLimits.Unknown),
        };

        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Updated Mod", "1.0");
        var secondContainer = Guid.NewGuid();
        repository.Containers[secondContainer] =
            NexusContainer(secondContainer, UnlistedModId, "Unlisted Mod", "2.0");
        var thirdContainer = Guid.NewGuid();
        repository.Containers[thirdContainer] =
            NexusContainer(thirdContainer, PinnedModId, "Pinned Mod", "3.0");

        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(secondContainer, new LatestPolicy()),
                Entry(thirdContainer, new LatestPolicy()),
            },
        };

        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Equal(2, result.Updates.Count);
        Assert.Contains(result.Updates, u => u.ContainerId == NexusLatestContainer && u.ModId == UpdatedModId);
        Assert.Contains(result.Updates, u => u.ContainerId == secondContainer && u.ModId == UnlistedModId);
        Assert.DoesNotContain(result.Updates, u => u.ModId == PinnedModId);
        Assert.False(result.RateLimited);

        // Exactly 1 API call regardless of how many mods are in the profile.
        Assert.Equal(1, nexus.GraphQlCallCount);
        // The Darktide game id + the mod ids are passed through.
        Assert.Equal(GameId, nexus.LastGameId);
        Assert.Equal(new[] { UpdatedModId, UnlistedModId, PinnedModId }, nexus.LastModIds);
    }

    [Fact]
    public async Task CheckAsync_returns_empty_when_no_mods_have_updates()
    {
        // All viewerUpdateAvailable = false (or null): nothing flags.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[]
                {
                    Status(UpdatedModId, viewerUpdateAvailable: false),
                    Status(UnlistedModId, viewerUpdateAvailable: null),
                },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var secondContainer = Guid.NewGuid();
        repository.Containers[secondContainer] =
            NexusContainer(secondContainer, UnlistedModId, "Mod 2", "2.0");
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(secondContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public async Task CheckAsync_treats_null_viewerUpdateAvailable_as_no_update()
    {
        // A null viewerUpdateAvailable (server has no download record for the
        // user, e.g. a manually imported mod) is treated as false: not flagged.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: null) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_flags_when_installed_version_differs_even_if_viewerUpdateAvailable_is_false()
    {
        // viewerUpdateAvailable is false (the server's per-user download
        // tracking doesn't reflect the local state: user installed an older
        // version, uses multiple PCs, or imported manually). The version
        // comparison catches this: installed "1.0" vs server "1.2.1" flags.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: false, version: "1.2.1") },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
    }

    [Fact]
    public async Task CheckAsync_does_not_flag_when_versions_match_and_viewerUpdateAvailable_is_false()
    {
        // Both signals agree: no update. viewerUpdateAvailable is false and the
        // installed version matches the server's version.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: false, version: "1.0") },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
    }

    [Fact]
    public async Task CheckAsync_does_not_flag_mods_missing_from_response()
    {
        // The API returns fewer mods than expected (a UID did not resolve):
        // the missing mod is simply not flagged (conservative).
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Found Mod", "1.0");
        repository.Containers[NexusUnlistedContainer] =
            NexusContainer(NexusUnlistedContainer, UnlistedModId, "Missing Mod", "2.0");
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(NexusUnlistedContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        // The missing mod (UnlistedModId) was not flagged.
    }

    [Fact]
    public async Task CheckAsync_populates_LatestUpdateAt_from_updatedAt_field()
    {
        // The v2 updatedAt field is surfaced as ModUpdateInfo.LatestUpdateAt
        // for the UI's display context.
        var updatedAt = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true, updatedAt) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(updatedAt, flagged.LatestUpdateAt);
        Assert.Equal("1.0", flagged.CurrentVersion);
        Assert.Equal("Mod", flagged.ModName);
    }

    // ---- gating: no auth, no checkable mods --------------------------------

    [Fact]
    public async Task CheckAsync_short_circuits_when_no_auth()
    {
        // AuthMethod.None -> short-circuit before the API call. Even though the
        // canned response WOULD flag a mod, the API is never hit.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository, authMethod: NexusAuthMethod.None);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        Assert.Equal(0, nexus.GraphQlCallCount);
    }

    [Fact]
    public async Task CheckAsync_short_circuits_when_no_checkable_mods()
    {
        // All mods are pinned / untracked / github -> nothing to check -> the
        // API is not called (the checkable list is empty).
        var nexus = new FakeNexusClient(); // unset; would serve an empty default if called
        var repository = new FakeModRepository();
        repository.Containers[NexusPinnedContainer] =
            NexusContainer(NexusPinnedContainer, PinnedModId, "Pinned Mod", "1.0");
        repository.Containers[UntrackedContainer] =
            NonNexusContainer(UntrackedContainer, new UntrackedSource(), "Untracked Mod");
        repository.Containers[GitHubContainer] =
            NonNexusContainer(GitHubContainer, new GitHubSource { Owner = "o", Repo = "r" }, "GitHub Mod");
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
        Assert.Equal(0, nexus.GraphQlCallCount);
    }

    // ---- source / policy filter -------------------------------------------

    [Fact]
    public async Task CheckAsync_skips_pinned_and_untracked_and_github()
    {
        // A Nexus + Latest mod (would flag) PLUS a pinned Nexus mod, an untracked
        // mod, and a GitHub mod. Only the Nexus + Latest one flags; the others
        // are never sent to the API. The 1-API-call contract holds regardless
        // of source/policy mix, and only the checkable mod's id is in the
        // request.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[]
                {
                    Status(UpdatedModId, viewerUpdateAvailable: true),
                    Status(PinnedModId, viewerUpdateAvailable: true), // would flag, but pinned
                },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        repository.Containers[NexusPinnedContainer] =
            NexusContainer(NexusPinnedContainer, PinnedModId, "Pinned Mod", "1.0");
        repository.Containers[UntrackedContainer] =
            NonNexusContainer(UntrackedContainer, new UntrackedSource(), "Untracked Mod");
        repository.Containers[GitHubContainer] =
            NonNexusContainer(GitHubContainer, new GitHubSource { Owner = "o", Repo = "r" }, "GitHub Mod");
        var profiles = new FakeProfileService
        {
            Mods = new[]
            {
                Entry(NexusLatestContainer, new LatestPolicy()),
                Entry(NexusPinnedContainer, new PinnedPolicy("v1")),
                Entry(UntrackedContainer, new LatestPolicy()),
                Entry(GitHubContainer, new LatestPolicy()),
            },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(NexusLatestContainer, flagged.ContainerId);
        Assert.Equal(1, nexus.GraphQlCallCount);
        // Only the checkable mod's id was sent (pinned/untracked/github excluded).
        Assert.Equal(new[] { UpdatedModId }, nexus.LastModIds);
    }

    // ---- rate-limit handling ----------------------------------------------

    [Fact]
    public async Task CheckAsync_surfaces_rate_limited_when_client_throws_NexusRateLimitException()
    {
        // The client throws NexusRateLimitException (HTTP 429); the service
        // surfaces it as a rate-limited result (not a generic failure).
        var nexus = new FakeNexusClient
        {
            GraphQlThrows = new NexusRateLimitException(429, NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
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
    public async Task CheckAsync_surfaces_rate_limited_when_daily_remaining_is_zero()
    {
        // DailyRemaining=0 with a real DailyLimit -> rate-limited. The check
        // short-circuits even though the one mod WOULD flag.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 0, DailyReset: null,
                    HourlyLimit: 100, HourlyRemaining: 50, HourlyReset: null)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
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
    public async Task CheckAsync_surfaces_rate_limited_when_hourly_remaining_is_zero()
    {
        // Symmetric proof of the rate-limit guard's other window.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                new NexusRateLimits(
                    DailyLimit: 100, DailyRemaining: 50, DailyReset: null,
                    HourlyLimit: 100, HourlyRemaining: 0, HourlyReset: null)),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
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
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
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

    // ---- best-effort failure -----------------------------------------------

    [Fact]
    public async Task CheckAsync_api_failure_returns_empty_and_does_not_propagate()
    {
        // A NexusApiException from CheckUpdatesGraphQlAsync is caught: the
        // service returns an empty non-rate-limited result, raises
        // CheckCompleted, and sets LastResult. The caller (fire-and-forget)
        // never sees the throw.
        var nexus = new FakeNexusClient
        {
            GraphQlThrows = new NexusApiException(500, "server error"),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
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

    // ---- publishing --------------------------------------------------------

    [Fact]
    public async Task CheckAsync_publishes_result_via_LastResult_and_CheckCompleted()
    {
        // Before the first check, LastResult is null. After a check, LastResult
        // is the returned result and CheckCompleted was raised exactly once
        // with that same result.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
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

    // ---- thorough vs non-thorough result flag ------------------------------

    [Fact]
    public async Task CheckAsync_returns_thorough_false()
    {
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
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
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckThoroughAsync(ProfileId);

        Assert.True(result.Thorough);
    }

    [Fact]
    public async Task CheckThoroughAsync_same_as_CheckAsync()
    {
        // Both CheckAsync and CheckThoroughAsync run the same v2 batch query
        // and produce the same flagged set; they differ only in the Thorough
        // flag. A mod with viewerUpdateAvailable=true flags under both.
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(UpdatedModId, viewerUpdateAvailable: true) },
                NexusRateLimits.Unknown),
        };
        var repository = new FakeModRepository();
        repository.Containers[NexusLatestContainer] =
            NexusContainer(NexusLatestContainer, UpdatedModId, "Nexus Mod", "1.0");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(NexusLatestContainer, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var checkResult = await service.CheckAsync(ProfileId);
        var thoroughResult = await service.CheckThoroughAsync(ProfileId);

        Assert.Single(checkResult.Updates);
        Assert.Single(thoroughResult.Updates);
        Assert.Equal(checkResult.Updates[0].ContainerId, thoroughResult.Updates[0].ContainerId);
        Assert.False(checkResult.Thorough);
        Assert.True(thoroughResult.Thorough);
        // Both made their own API call (2 total).
        Assert.Equal(2, nexus.GraphQlCallCount);
    }

    // ---- tier 3: latest-file-version confirmation -------------------------

    [Fact]
    public async Task CheckAsync_tier3_clears_tier2_false_positive_when_latest_file_matches_installed()
    {
        // The mod-page header version (1.9.1) lags the latest file (1.9.2), and the
        // user has 1.9.2 installed. Tier 2 flags (1.9.2 != 1.9.1); tier 3 resolves
        // the latest MAIN file (1.9.2) and clears the flag because 1.9.2 equals the
        // installed version. This is the mod #1022-style false positive.
        const int modId = 1022;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.9.1") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Laggy Header Mod", "1.9.2");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        Assert.Empty(result.Updates);
        Assert.False(result.RateLimited);
        // Tier 3 ran (one ListModFilesAsync call to confirm the false positive).
        Assert.Equal(1, nexus.ListModFilesCallCount[modId]);
    }

    [Fact]
    public async Task CheckAsync_tier3_keeps_flag_when_latest_file_differs_from_installed()
    {
        // Tier 2 flags (installed 1.9.1 != page 1.9.0). The latest MAIN file is
        // 1.9.2, which differs from the installed 1.9.1, so tier 3 cannot clear it:
        // a real update exists. The flag stays.
        const int modId = 1023;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.9.0") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Real Update Mod", "1.9.1");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(container, flagged.ContainerId);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public async Task CheckAsync_tier3_skips_tier1_flagged_mods()
    {
        // viewerUpdateAvailable == true is authoritative: even though the installed
        // version (1.9.1) matches the latest file (1.9.1), which would clear a
        // tier-2 flag, tier 1 keeps the mod flagged and tier 3 does not run on it.
        const int modId = 1024;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: true, version: "1.9.0") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                // Would clear a tier-2 flag (1.9.1 == installed), but tier 3 must
                // never read it for this tier-1-flagged mod.
                [modId] = new[] { MainFile("1.9.1", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Tier1 Mod", "1.9.1");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(container, flagged.ContainerId);
        // Tier 3 was skipped: ListModFilesAsync was never called for this mod.
        Assert.False(nexus.ListModFilesCallCount.ContainsKey(modId));
    }

    [Fact]
    public async Task CheckAsync_tier3_cache_avoids_repeat_call_for_unchanged_mod()
    {
        // Two checks with the same (modId, pageVersion, updatedAt) for a tier-2-only
        // flag. The first resolves + caches; the second hits the cache. Net: exactly
        // one ListModFilesAsync call across both checks.
        const int modId = 1025;
        var updatedAt = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, updatedAt, version: "1.9.1") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Cached Mod", "1.9.2");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        await service.CheckAsync(ProfileId);
        await service.CheckAsync(ProfileId);

        Assert.Equal(1, nexus.ListModFilesCallCount[modId]);
    }

    [Fact]
    public async Task CheckAsync_tier3_cache_invalidates_when_page_version_changes()
    {
        // First check resolves + caches. The second check observes a different page
        // version (one of the cache-key components), so the key no longer matches
        // and tier 3 re-resolves. The updatedAt component is held constant so only
        // the page version invalidates.
        const int modId = 1026;
        var updatedAt = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Cache Invalidate Mod", "1.9.2");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        nexus.GraphQlResponse = new Response<ModUpdateStatus[]>(
            new[] { Status(modId, viewerUpdateAvailable: false, updatedAt, version: "1.9.1") },
            NexusRateLimits.Unknown);
        await service.CheckAsync(ProfileId);
        Assert.Equal(1, nexus.ListModFilesCallCount[modId]);

        // Page version changed -> cache key differs -> re-resolve.
        nexus.GraphQlResponse = new Response<ModUpdateStatus[]>(
            new[] { Status(modId, viewerUpdateAvailable: false, updatedAt, version: "1.9.0") },
            NexusRateLimits.Unknown);
        await service.CheckAsync(ProfileId);
        Assert.Equal(2, nexus.ListModFilesCallCount[modId]);
    }

    [Fact]
    public async Task CheckAsync_tier3_cache_re_resolves_after_ttl_expires_on_unchanged_key()
    {
        // The tier-3 cache TTL backstop: even when the cache key (modId,
        // pageVersion, updatedAt) is UNCHANGED, an entry older than 24h is
        // re-resolved. This catches the rare case where an author uploads a new
        // MAIN file without bumping the page version or the updated-at timestamp
        // (the two observable key components). The injected clock drives the TTL
        // age deterministically; without it the 24h wait is untestable.
        const int modId = 1028;
        var updatedAt = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, updatedAt, version: "1.9.1") },
                NexusRateLimits.Unknown),
            ModFilesByModId =
            {
                [modId] = new[] { MainFile("1.9.2", uploadedTs: 10) },
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "TTL Backstop Mod", "1.9.2");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository, getNow: () => now);

        // First check: tier 2 flags (installed 1.9.2 != page 1.9.1); tier 3
        // resolves the latest MAIN file (1.9.2) and clears the flag, caching the
        // verdict at the injected clock's "now".
        await service.CheckAsync(ProfileId);
        Assert.Equal(1, nexus.ListModFilesCallCount[modId]);

        // Advance the injected clock past the 24h TTL. The cache key (modId,
        // pageVersion, updatedAt) is unchanged across both checks.
        now = now + TimeSpan.FromHours(25);

        // Second check: same key, but the entry is now older than the TTL, so
        // tier 3 re-resolves despite the unchanged key.
        await service.CheckAsync(ProfileId);
        Assert.Equal(2, nexus.ListModFilesCallCount[modId]);
    }

    [Fact]
    public async Task CheckAsync_tier3_failure_leaves_flag_and_does_not_fail_check()
    {
        // The tier-3 ListModFilesAsync call throws (network, rate-limit, any error).
        // Tier 3 cannot confirm, so it leaves the mod flagged; the check itself does
        // not fail and is not reported as rate-limited (the tier-1+2 baseline holds).
        const int modId = 1027;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.9.0") },
                NexusRateLimits.Unknown),
            ListModFilesThrows =
            {
                [modId] = new NexusApiException(500, "server error"),
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Tier3 Failure Mod", "1.9.1");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(container, flagged.ContainerId);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public async Task CheckAsync_tier3_rate_limit_leaves_flag_and_does_not_promote_to_rate_limited()
    {
        // A NexusRateLimitException from tier 3's ListModFilesAsync is swallowed
        // like any tier-3 failure: tier 3 cannot confirm, so the mod stays
        // flagged, AND the check result's RateLimited flag stays false. A
        // tier-3-only rate limit does NOT promote to the check-level signal (the
        // tier-1+2 baseline holds). This pins the non-obvious design choice; the
        // sibling failure test throws NexusApiException, this one throws the
        // rate-limit subtype to prove the subtype is treated the same way.
        const int modId = 1029;
        var nexus = new FakeNexusClient
        {
            GraphQlResponse = new Response<ModUpdateStatus[]>(
                new[] { Status(modId, viewerUpdateAvailable: false, version: "1.9.0") },
                NexusRateLimits.Unknown),
            ListModFilesThrows =
            {
                [modId] = new NexusRateLimitException(429, NexusRateLimits.Unknown),
            },
        };
        var container = Guid.NewGuid();
        var repository = new FakeModRepository();
        repository.Containers[container] =
            NexusContainer(container, modId, "Tier3 Rate-Limited Mod", "1.9.1");
        var profiles = new FakeProfileService
        {
            Mods = new[] { Entry(container, new LatestPolicy()) },
        };
        var service = CreateService(nexus, profiles, repository);

        var result = await service.CheckAsync(ProfileId);

        var flagged = Assert.Single(result.Updates);
        Assert.Equal(container, flagged.ContainerId);
        Assert.False(result.RateLimited);
    }

    // ---- DI activation (default clock param) ------------------------------

    [Fact]
    public void UpdateCheckService_resolves_from_DI_with_unregistered_optional_clock_param()
    {
        // The clock seam's last constructor parameter (Func<DateTimeOffset>?
        // getNow = null) is intentionally NOT registered in DI. This proves the
        // production registration (AddUpdateCheck's interface-to-impl
        // AddSingleton<IUpdateCheckService, UpdateCheckService>) still activates:
        // Microsoft.Extensions.DependencyInjection honors the parameter's
        // default value for the unresolvable optional param, which the
        // constructor turns into the UtcNow clock. If this ever stops resolving,
        // the registration must switch to a factory delegate that passes
        // getNow: null explicitly (the UpdateCheckRunner registration pattern).
        var nexus = new FakeNexusClient();
        var repository = new FakeModRepository();
        var profiles = new FakeProfileService();
        var configLoader = new FakeConfigLoader { Config = CuratorConfig.CreateDefault() };

        var services = new ServiceCollection();
        services.AddSingleton<INexusClient>(nexus);
        services.AddSingleton<IProfileService>(profiles);
        services.AddSingleton<IModRepository>(repository);
        services.AddSingleton<IConfigLoader>(configLoader);
        services.AddSingleton<ILogger<UpdateCheckService>>(NullLogger<UpdateCheckService>.Instance);
        // Mirrors AddUpdateCheck (the interface-to-impl registration under test);
        // the unregistered Func<DateTimeOffset>? param must fall back to its default.
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IUpdateCheckService>();

        Assert.NotNull(service);
        Assert.IsType<UpdateCheckService>(service);
    }

    // ---- helpers + fakes ---------------------------------------------------

    /// <summary>
    /// Builds a <see cref="ModFile"/> with the given version as a non-archived
    /// MAIN file (category 1) uploaded at <paramref name="uploadedTs"/>. Tier 3
    /// picks the newest MAIN by upload timestamp, so the caller controls which
    /// file "wins" via <paramref name="uploadedTs"/>.
    /// </summary>
    private static ModFile MainFile(string version, long uploadedTs = 0) =>
        new()
        {
            FileId = uploadedTs,
            FileName = version + ".zip",
            Name = version,
            Version = version,
            CategoryId = NexusModFiles.MainFileCategory,
            UploadedTimestamp = uploadedTs,
        };

    /// <summary>
    /// Builds a Nexus-sourced <see cref="ModContainer"/> with a single
    /// IsLatest version.
    /// </summary>
    private static ModContainer NexusContainer(Guid id, int modId, string name, string version) =>
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
                    ImportedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            },
        };

    /// <summary>
    /// Builds a non-Nexus (untracked or github) <see cref="ModContainer"/> with
    /// a single IsLatest version. Used to prove non-Nexus sources are skipped.
    /// </summary>
    private static ModContainer NonNexusContainer(Guid id, ModSource source, string name) =>
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
                    ImportedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            },
        };

    private static ModListEntry Entry(Guid containerId, ModVersionPolicy policy) =>
        new() { ContainerId = containerId, Enabled = true, Order = 0, Policy = policy };

    /// <summary>
    /// Computes the UID the way the service + the client do:
    /// <c>game_id * 2^32 + mod_id</c>.
    /// </summary>
    private static long Uid(int modId) => (long)GameId * 4294967296L + modId;

    /// <summary>
    /// Builds a <see cref="ModUpdateStatus"/> for <paramref name="modId"/> with
    /// the given <paramref name="viewerUpdateAvailable"/> + optional
    /// <paramref name="updatedAt"/> + <paramref name="version"/>. The version
    /// defaults to "" so the version comparison is skipped unless a test
    /// explicitly sets one.
    /// </summary>
    private static ModUpdateStatus Status(
        int modId, bool? viewerUpdateAvailable, DateTimeOffset? updatedAt = null,
        string version = "") =>
        new()
        {
            Uid = Uid(modId),
            Name = "Mod " + modId,
            Version = version,
            UpdatedAt = updatedAt,
            ViewerUpdateAvailable = viewerUpdateAvailable,
        };

    /// <summary>
    /// Constructs the service with the given fakes + a <see cref="FakeConfigLoader"/>
    /// whose Nexus auth method is <paramref name="authMethod"/> (ApiKey by
    /// default, so the auth gate passes and the API is reached). The optional
    /// <paramref name="getNow"/> drives the tier-3 cache TTL clock for the TTL
    /// re-resolve test; it defaults to null (the production UtcNow clock).
    /// </summary>
    private static UpdateCheckService CreateService(
        FakeNexusClient nexus,
        FakeProfileService profiles,
        FakeModRepository repository,
        NexusAuthMethod authMethod = NexusAuthMethod.ApiKey,
        Func<DateTimeOffset>? getNow = null)
    {
        var config = CuratorConfig.CreateDefault();
        config.Integrations.Nexus.AuthMethod = authMethod;
        var configLoader = new FakeConfigLoader { Config = config };
        return new UpdateCheckService(
            nexus, profiles, repository, configLoader, NullLogger<UpdateCheckService>.Instance, getNow);
    }

    /// <summary>
    /// A configurable <see cref="INexusClient"/> stub. <see cref="CheckUpdatesGraphQlAsync"/>
    /// is exercised by both check shapes. The v1 methods throw (the update
    /// check no longer calls them). Tracks the call count + the args for the
    /// 1-call contract + game-id/mod-ids assertions.
    /// </summary>
    private sealed class FakeNexusClient : INexusClient
    {
        public Response<ModUpdateStatus[]>? GraphQlResponse { get; set; }
        public Exception? GraphQlThrows { get; set; }
        public int GraphQlCallCount { get; private set; }
        public int? LastGameId { get; private set; }
        public List<int>? LastModIds { get; private set; }

        // Tier-3 (latest-file-version confirmation) support. ListModFilesAsync is
        // the per-tier-2-only-flagged-mod call the service makes to resolve the
        // newest MAIN file. The response map is keyed by mod id; the throw map lets
        // a test simulate a failure; the call counter backs the cache-avoidance +
        // cache-invalidation assertions.
        public Dictionary<int, ModFile[]> ModFilesByModId { get; } = new();
        public Dictionary<int, Exception> ListModFilesThrows { get; } = new();
        public Dictionary<int, int> ListModFilesCallCount { get; } = new();

        public Task<Response<ModUpdateStatus[]>> CheckUpdatesGraphQlAsync(
            int gameId, IReadOnlyList<int> modIds, CancellationToken ct = default)
        {
            GraphQlCallCount++;
            LastGameId = gameId;
            LastModIds = modIds.ToList();
            if (GraphQlThrows is not null)
            {
                return Task.FromException<Response<ModUpdateStatus[]>>(GraphQlThrows);
            }
            return Task.FromResult(
                GraphQlResponse
                    ?? new Response<ModUpdateStatus[]>(Array.Empty<ModUpdateStatus>(), NexusRateLimits.Unknown));
        }

        public Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<OAuthUserInfo>> GetOAuthUserInfoAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<ModUpdate[]>> ModUpdatesAsync(
            string gameDomain, NexusPeriod period, CancellationToken ct = default)
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
        public Task<Response<ModFile[]>> ListModFilesAsync(
            string gameDomain, int modId, CancellationToken ct = default)
        {
            ListModFilesCallCount[modId] =
                ListModFilesCallCount.TryGetValue(modId, out var c) ? c + 1 : 1;

            if (ListModFilesThrows.TryGetValue(modId, out var ex))
            {
                return Task.FromException<Response<ModFile[]>>(ex);
            }

            if (!ModFilesByModId.TryGetValue(modId, out var files))
            {
                throw new NotImplementedException(
                    $"No ModFiles configured for mod {modId}; set FakeNexusClient.ModFilesByModId.");
            }

            return Task.FromResult(new Response<ModFile[]>(files, NexusRateLimits.Unknown));
        }
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
