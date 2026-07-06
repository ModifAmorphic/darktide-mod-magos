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

    // ---- helpers + fakes ---------------------------------------------------

    /// <summary>
    /// Builds a Nexus-sourced <see cref="ModContainer"/> with a single
    /// IsLatest version imported at <paramref name="importedAt"/>.
    /// </summary>
    private static ModContainer NexusContainer(
        Guid id, int modId, string name, DateTimeOffset importedAt, string version = "1.0") =>
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
    /// A configurable <see cref="INexusClient"/> stub. Only
    /// <see cref="ModUpdatesAsync"/> is exercised by the service; the other
    /// members throw. Tracks the call count + the args for the 1-call contract
    /// + domain/window assertions.
    /// </summary>
    private sealed class FakeNexusClient : INexusClient
    {
        public Response<ModUpdate[]>? ModUpdatesResponse { get; set; }
        public Exception? ModUpdatesThrows { get; set; }
        public int ModUpdatesCallCount { get; private set; }
        public string? LastGameDomain { get; private set; }
        public NexusPeriod? LastPeriod { get; private set; }

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
        public ModContainer AddVersion(Guid containerId, string versionString, Action<string> populateFolder)
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
