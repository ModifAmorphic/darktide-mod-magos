using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
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

    // ---- helpers + fakes ---------------------------------------------------

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
    /// <paramref name="updatedAt"/>.
    /// </summary>
    private static ModUpdateStatus Status(
        int modId, bool? viewerUpdateAvailable, DateTimeOffset? updatedAt = null) =>
        new()
        {
            Uid = Uid(modId),
            Name = "Mod " + modId,
            Version = "2.0",
            UpdatedAt = updatedAt,
            ViewerUpdateAvailable = viewerUpdateAvailable,
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
        var config = CuratorConfig.CreateDefault();
        config.Integrations.Nexus.AuthMethod = authMethod;
        var configLoader = new FakeConfigLoader { Config = config };
        return new UpdateCheckService(
            nexus, profiles, repository, configLoader, NullLogger<UpdateCheckService>.Instance);
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
