using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.RelayClient;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.Mods;
using Modificus.Curator.Steam;
using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Preferences;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Hand-rolled test doubles for the shell/manage/mod-list VMs' dependencies. No
/// mock library is used anywhere in the repo; these recording fakes match that
/// style and keep the test project dependency-free.
/// </summary>
internal static class TestDoubles
{
    public static FakeProfileService Profiles(params ProfileSummary[] seed) => new(seed);

    /// <summary>
    /// Builds a <see cref="DmfPromptService"/> wired to the supplied (or default)
    /// fakes. Defaults share the test's profiles/session so the create trigger
    /// fires through the same fake the test asserts on. The dialog fake defaults
    /// to confirm=true (the Yes/No case 1 + 2 confirm) + a successful acquisition.
    /// </summary>
    public static DmfPromptService BuildDmfPromptService(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeModRepository? repo = null,
        FakeModAcquisitionService? acquisition = null,
        FakeNexusAuthService? auth = null,
        FakeConfigLoader? configLoader = null,
        FakeDialogService? dialogs = null,
        LocalizationService? localization = null,
        FakeNxmHandlerRegistrar? nxmRegistrar = null)
    {
        profiles ??= Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        acquisition ??= new FakeModAcquisitionService();
        auth ??= new FakeNexusAuthService();
        configLoader ??= new FakeConfigLoader();
        dialogs ??= new FakeDialogService();
        localization ??= new LocalizationService();
        return new DmfPromptService(
            profiles,
            session,
            repo,
            acquisition,
            auth,
            configLoader,
            dialogs,
            localization,
            NullLogger<DmfPromptService>.Instance,
            nxmRegistrar);
    }

    /// <summary>
    /// Builds a <see cref="ModListViewModel"/> wired to the supplied (or default)
    /// fakes. The defaults share one repository between the store + import fake
    /// so the add flow's reload joins the freshly imported source + version
    /// (mirrors the real import service's behavior).
    /// </summary>
    public static ModListViewModel BuildModList(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeModRepository? repo = null,
        FakeModImportService? importService = null,
        IModOrderResolver? orderResolver = null,
        FakeDialogService? dialogs = null,
        LocalizationService? localization = null,
        FakeUpdateCheckService? updateCheck = null,
        FakeModAcquisitionService? acquisition = null,
        FakeNexusAuthService? auth = null,
        FakeConfigLoader? configLoader = null,
        Action<Action>? invokeOnUi = null)
    {
        profiles ??= Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        importService ??= new FakeModImportService(repo);
        orderResolver ??= new IdentityModOrderResolver();
        dialogs ??= new FakeDialogService();
        localization ??= new LocalizationService();
        updateCheck ??= new FakeUpdateCheckService();
        acquisition ??= new FakeModAcquisitionService();
        auth ??= new FakeNexusAuthService();
        configLoader ??= new FakeConfigLoader();
        invokeOnUi ??= static action => action();
        // The runner wires the manual CheckNow path; constructed with the
        // test's fakes + no periodic timer (the manual trigger does not depend
        // on the timer being started).
        var runner = new UpdateCheckRunner(
            session,
            updateCheck,
            configLoader,
            NullLogger<UpdateCheckRunner>.Instance);
        return new ModListViewModel(
            profiles,
            session,
            repo,
            importService,
            orderResolver,
            dialogs,
            localization,
            updateCheck,
            acquisition,
            auth,
            runner,
            invokeOnUi,
            NullLogger<ModListViewModel>.Instance);
    }
}

/// <summary>
/// In-memory <see cref="IProfileService"/> for VM tests: backs the profile CRUD +
/// listing surface AND the per-profile mod-list surface (Track B). Records calls
/// so tests can assert on them. <c>PrepareModRoot</c> throws (staging is out of
/// scope for VM tests).
/// </summary>
internal sealed class FakeProfileService : IProfileService
{
    private readonly List<ProfileSummary> _profiles;
    private readonly Dictionary<Guid, List<ModListEntry>> _modLists = new();

    public FakeProfileService(IEnumerable<ProfileSummary> seed) =>
        _profiles = new List<ProfileSummary>(seed);

    /// <inheritdoc />
    /// <remarks>Raised from <see cref="CreateProfile"/>. The DMF prompt
    /// coordinator subscribes; tests that drive the new-profile trigger
    /// simulate a create through <see cref="CreateProfile"/> (the event fires)
    /// + a call to <c>DmfPromptService.ProcessPendingAsync</c>.</remarks>
    public event EventHandler<ProfileSummary>? ProfileCreated;

    public IReadOnlyList<string> CreatedNames { get; } = new List<string>();
    public IReadOnlyList<(Guid Id, string Name)> Renames { get; } = new List<(Guid, string)>();
    public IReadOnlyList<Guid> DeletedIds { get; } = new List<Guid>();

    // ---- per-profile mod-list recording -----------------------------------

    /// <summary>Per-profile mod lists (in stored order); tests seed directly.</summary>
    public Dictionary<Guid, List<ModListEntry>> ModLists => _modLists;

    public IReadOnlyList<(Guid Id, Guid ContainerId, bool Enabled)> SetModEnabledCalls { get; } = new List<(Guid, Guid, bool)>();
    public IReadOnlyList<IReadOnlyList<Guid>> SetModOrderCalls { get; } = new List<IReadOnlyList<Guid>>();
    public IReadOnlyList<(Guid Id, Guid ContainerId, ModVersionPolicy Policy)> SetModPolicyCalls { get; } = new List<(Guid, Guid, ModVersionPolicy)>();
    public IReadOnlyList<(Guid Id, Guid ContainerId, ModVersionPolicy Policy)> AddModCalls { get; } = new List<(Guid, Guid, ModVersionPolicy)>();

    /// <summary>
    /// Optional exception thrown by the next <see cref="AddMod"/> call (after the
    /// call is recorded). Default <c>null</c> = no throw. Used by the nxm-handler
    /// test to simulate AddMod failing after a successful acquisition.
    /// </summary>
    public Exception? AddModThrows { get; set; }

    public IReadOnlyList<(Guid Id, Guid ContainerId)> RemoveModCalls { get; } = new List<(Guid, Guid)>();
    /// <summary>Seeds a profile's mod list (replaces any prior). Test helper.</summary>
    public FakeProfileService WithMods(Guid id, params ModListEntry[] mods)
    {
        _modLists[id] = mods.Select(m => m with { }).ToList();
        return this;
    }

    private List<ModListEntry> EnsureList(Guid id)
    {
        if (!_modLists.TryGetValue(id, out var list))
        {
            list = new List<ModListEntry>();
            _modLists[id] = list;
        }
        return list;
    }

    public IReadOnlyList<ProfileSummary> ListProfiles() =>
        _profiles.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    public Profile GetProfile(Guid id)
    {
        var summary = _profiles.FirstOrDefault(p => p.Id == id)
            ?? throw new KeyNotFoundException($"No profile {id}");
        return new Profile { Id = summary.Id, Name = summary.Name };
    }

    public Profile CreateProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name required", nameof(name));
        }

        var created = new ProfileSummary(Guid.NewGuid(), name);
        _profiles.Add(created);
        ((List<string>)CreatedNames).Add(name);
        // Mirror the production service: raise ProfileCreated AFTER the profile
        // is added to the list so a subscriber that re-lists sees it.
        ProfileCreated?.Invoke(this, created);
        return new Profile { Id = created.Id, Name = created.Name };
    }

    public void RenameProfile(Guid id, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("name required", nameof(newName));
        }

        var idx = _profiles.FindIndex(p => p.Id == id);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No profile {id}");
        }

        _profiles[idx] = _profiles[idx] with { Name = newName };
        ((List<(Guid, string)>)Renames).Add((id, newName));
    }

    public void DeleteProfile(Guid id)
    {
        var idx = _profiles.FindIndex(p => p.Id == id);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No profile {id}");
        }

        _profiles.RemoveAt(idx);
        _modLists.Remove(id);
        ((List<Guid>)DeletedIds).Add(id);
    }

    // ---- mod-list surface --------------------------------------------------

    public IReadOnlyList<ModListEntry> GetModList(Guid id) => EnsureList(id).ToArray();

    public void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder)
    {
        ((List<IReadOnlyList<Guid>>)SetModOrderCalls).Add(containerIdsInOrder);

        var list = EnsureList(id);
        var ordered = new List<ModListEntry>();
        var remaining = list.ToList();
        foreach (var cid in containerIdsInOrder)
        {
            var match = remaining.FirstOrDefault(m => m.ContainerId == cid);
            if (match is not null)
            {
                ordered.Add(match);
                remaining.Remove(match);
            }
        }
        ordered.AddRange(remaining);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i] = ordered[i] with { Order = i };
        }
        list.Clear();
        list.AddRange(ordered);
    }

    public void SetModEnabled(Guid id, Guid containerId, bool enabled)
    {
        ((List<(Guid, Guid, bool)>)SetModEnabledCalls).Add((id, containerId, enabled));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.ContainerId == containerId);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No container {containerId} in profile {id}");
        }
        list[idx] = list[idx] with { Enabled = enabled };
    }

    public void AddMod(Guid id, Guid containerId, ModVersionPolicy policy)
    {
        ((List<(Guid, Guid, ModVersionPolicy)>)AddModCalls).Add((id, containerId, policy));

        if (AddModThrows is not null)
        {
            throw AddModThrows;
        }

        var list = EnsureList(id);
        if (list.Any(m => m.ContainerId == containerId))
        {
            return; // idempotent
        }
        list.Add(new ModListEntry
        {
            ContainerId = containerId,
            Enabled = true,
            Order = list.Count,
            Policy = policy,
        });
    }

    public void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy)
    {
        ((List<(Guid, Guid, ModVersionPolicy)>)SetModPolicyCalls).Add((id, containerId, policy));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.ContainerId == containerId);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No container {containerId} in profile {id}");
        }
        list[idx] = list[idx] with { Policy = policy };
    }

    public void RemoveMod(Guid id, Guid containerId)
    {
        ((List<(Guid, Guid)>)RemoveModCalls).Add((id, containerId));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.ContainerId == containerId);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No container {containerId} in profile {id}");
        }
        list.RemoveAt(idx);
    }

    /// <summary>The (profileId, baseName, excludeContainerId) triples passed to
    /// <see cref="GetBaseNameCollision"/>, in call order. Tests assert on
    /// <c>ExcludeContainerId</c> to verify the add flow carried the re-add
    /// container id through.</summary>
    public IReadOnlyList<(Guid ProfileId, string BaseName, Guid? ExcludeContainerId)> GetBaseNameCollisionCalls { get; }
        = new List<(Guid, string, Guid?)>();

    /// <summary>
    /// The <see cref="ModListEntry"/> returned by the next
    /// <see cref="GetBaseNameCollision"/> call (default <c>null</c> = no
    /// collision). The fake does no real base-name resolution (that is exercised
    /// against the real service in <c>Profiles.Tests</c>); a VM test sets this to
    /// simulate a collision.
    /// </summary>
    public ModListEntry? GetBaseNameCollisionResult { get; set; }

    /// <summary>
    /// Records the call (for the exclude-container assertion) + returns
    /// <see cref="GetBaseNameCollisionResult"/>. The real resolution lives in
    /// <c>ProfileService</c> + is tested there.
    /// </summary>
    public ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId)
    {
        ((List<(Guid, string, Guid?)>)GetBaseNameCollisionCalls).Add((id, baseName, excludeContainerId));
        return GetBaseNameCollisionResult;
    }

    public string PrepareModRoot(Guid id) => throw new NotImplementedException();
}

/// <summary>Records <see cref="IAppStateStore"/> reads/writes for assertion.</summary>
internal sealed class FakeAppStateStore : IAppStateStore
{
    public int SetCount { get; private set; }
    public Guid? ActiveProfileId { get; set; } = null;

    Guid? IAppStateStore.ActiveProfileId
    {
        get => ActiveProfileId;
        set
        {
            ActiveProfileId = value;
            SetCount++;
        }
    }
}

/// <summary>
/// Configurable dialog fake. <see cref="ConfirmResult"/> drives
/// <see cref="ConfirmAsync"/>; <see cref="OnManageProfiles"/> runs when the
/// manage-profiles dialog is opened (lets a test simulate the dialog creating /
/// deleting profiles and routing active changes through the session). For the
/// import modal, either a single <see cref="ImportResult"/> is returned for every
/// call, or a per-call <see cref="ImportResultQueue"/> is dequeued (so a test can
/// cancel mid-batch by enqueuing a <c>null</c>). The Settings, escape-hatch, and
/// alert calls are recorded for assertion; the escape-hatch also exposes its
/// drive flag (<see cref="EscapeHatchResult"/>) so a test can simulate a submit
/// vs. a cancel. Records the requests so tests can assert on the sequence.
/// </summary>
internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public Action? OnManageProfiles { get; set; }
    public Action? OnPreferences { get; set; }
    public Action? OnSettings { get; set; }
    public Action? OnIntegrations { get; set; }
    public int ConfirmCalls { get; private set; }
    public string? LastConfirmMessage { get; private set; }
    public int ManageProfilesCalls { get; private set; }
    public int PreferencesCalls { get; private set; }
    public int SettingsCalls { get; private set; }
    public int IntegrationsCalls { get; private set; }

    /// <summary>
    /// The result returned by the next escape-hatch call: <c>true</c> = the
    /// user submitted, <c>false</c> = cancelled. Default <c>false</c>.
    /// </summary>
    public bool EscapeHatchResult { get; set; }

    /// <summary>The missing-field lists the shell asked the escape-hatch to show,
    /// in call order. Tests assert on this to verify which fields the launch
    /// reported missing.</summary>
    public IReadOnlyList<IReadOnlyList<string>> EscapeHatchCalls { get; } = new List<IReadOnlyList<string>>();

    /// <summary>The (title, message) pairs passed to <see cref="ShowAlertAsync"/>,
    /// in call order.</summary>
    public IReadOnlyList<(string Title, string Message)> AlertCalls { get; } = new List<(string, string)>();

    /// <summary>
    /// The result returned for every import modal call when
    /// <see cref="ImportResultQueue"/> is empty / unset. <c>null</c> by default
    /// (simulates the user cancelling).
    /// </summary>
    public ImportModResult? ImportResult { get; set; }

    /// <summary>
    /// Optional per-call queue: each import modal call dequeues one result
    /// (a <c>null</c> cancels that modal + the remaining batch). When empty /
    /// unset, <see cref="ImportResult"/> is returned.
    /// </summary>
    public Queue<ImportModResult?>? ImportResultQueue { get; set; }

    public IReadOnlyList<ImportModRequest> ImportRequests { get; } = new List<ImportModRequest>();
    public int ImportCalls { get; private set; }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        ConfirmCalls++;
        LastConfirmMessage = message;
        return Task.FromResult(ConfirmResult);
    }

    public Task ShowManageProfilesAsync()
    {
        ManageProfilesCalls++;
        OnManageProfiles?.Invoke();
        return Task.CompletedTask;
    }

    public Task ShowPreferencesAsync()
    {
        PreferencesCalls++;
        OnPreferences?.Invoke();
        return Task.CompletedTask;
    }

    public Task ShowSettingsAsync()
    {
        SettingsCalls++;
        OnSettings?.Invoke();
        return Task.CompletedTask;
    }

    public Task ShowIntegrationsAsync()
    {
        IntegrationsCalls++;
        OnIntegrations?.Invoke();
        return Task.CompletedTask;
    }

    public Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields)
    {
        ((List<IReadOnlyList<string>>)EscapeHatchCalls).Add(missingFields);
        return Task.FromResult(EscapeHatchResult);
    }

    public Task ShowAlertAsync(string title, string message)
    {
        ((List<(string, string)>)AlertCalls).Add((title, message));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The work passed to <see cref="ShowProgressAsync{T}"/>, in call order.
    /// Tests assert on this to verify the DMF download path was driven through
    /// the spinner. Each entry is invoked (awaited) so the work's result /
    /// exception surfaces to the caller as in production.
    /// </summary>
    public IReadOnlyList<(string Title, string Message, Delegate Work)> ProgressCalls { get; }
        = new List<(string, string, Delegate)>();

    public async Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work)
    {
        ((List<(string, string, Delegate)>)ProgressCalls).Add((title, message, work));
        // Drive the work so the caller sees its result / exception as in
        // production. No real spinner in tests; just await the work.
        return await work();
    }

    public Task<ImportModResult?> ShowImportModAsync(ImportModRequest request)
    {
        ImportCalls++;
        ((List<ImportModRequest>)ImportRequests).Add(request);

        var result = ImportResultQueue is { Count: > 0 }
            ? ImportResultQueue.Dequeue()
            : ImportResult;
        return Task.FromResult(result);
    }
}

/// <summary><see cref="ISteamService"/> with a configurable running flag +
/// discovery result.</summary>
internal sealed class FakeSteamService : ISteamService
{
    public bool Running { get; set; }
    public DiscoveryResult? Discovery { get; set; }
    public bool IsGameRunning() => Running;
    public DiscoveryResult Discover() =>
        Discovery ?? throw new NotImplementedException();
}

/// <summary>
/// In-memory <see cref="IProfileSession"/> for shell / dialog tests. Mirrors the
/// real session's gate (<see cref="RequestActive"/> no-ops when running), delete
/// gate (<see cref="CanDeleteProfile"/> locks the active id while running), and
/// recovery (<see cref="ReconcileActive"/> clears the active id when it no longer
/// exists). Raises <see cref="INotifyPropertyChanged.PropertyChanged"/> so the shell
/// + dialog react to live <see cref="IsRunning"/> changes the way the real polling
/// timer drives.
/// </summary>
internal sealed class FakeProfileSession : ObservableObject, IProfileSession
{
    private readonly Func<IReadOnlyList<ProfileSummary>>? _listProfiles;
    private Guid? _activeProfileId;
    private bool _isRunning;

    public FakeProfileSession(Func<IReadOnlyList<ProfileSummary>>? listProfiles = null)
    {
        _listProfiles = listProfiles;
    }

    public Guid? ActiveProfileId
    {
        get => _activeProfileId;
        set => SetProperty(ref _activeProfileId, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public int RequestActiveCalls { get; private set; }
    public Guid? LastRequestedId { get; private set; }

    public void RequestActive(Guid id)
    {
        RequestActiveCalls++;
        LastRequestedId = id;
        if (IsRunning)
        {
            return;
        }

        ActiveProfileId = id;
    }

    public int CanDeleteProfileCalls { get; private set; }

    /// <summary>Mirrors the real session: the active id is locked while running.</summary>
    public bool CanDeleteProfile(Guid id)
    {
        CanDeleteProfileCalls++;
        return !(id == ActiveProfileId && IsRunning);
    }

    public int ReconcileCalls { get; private set; }

    public void ReconcileActive()
    {
        ReconcileCalls++;
        if (_listProfiles is null || _activeProfileId is not Guid id)
        {
            return;
        }

        var existing = _listProfiles();
        if (existing.Any(p => p.Id == id))
        {
            return;
        }

        ActiveProfileId = null;
    }

    /// <summary>Number of times <see cref="Refresh"/> was called.</summary>
    public int RefreshCalls { get; private set; }

    /// <summary>
    /// Optional callback invoked on each <see cref="Refresh"/>; tests use it to
    /// drive a deterministic running-state change (e.g. flip <see cref="IsRunning"/>
    /// to <c>true</c> to simulate the game having just started).
    /// </summary>
    public Action? OnRefresh { get; set; }

    /// <summary>
    /// Records the call + runs the optional <see cref="OnRefresh"/> callback so a
    /// test can simulate the running-state change a real Refresh would observe.
    /// </summary>
    public void Refresh()
    {
        RefreshCalls++;
        OnRefresh?.Invoke();
    }
}

/// <summary>
/// Configurable <see cref="IRelayLaunchService"/> for shell-VM launch tests.
/// <see cref="NextResult"/> is returned for every Launch call (default:
/// Launched). <see cref="LaunchCalls"/> records the ids the shell asked to
/// launch.
/// </summary>
internal sealed class FakeLaunchService : IRelayLaunchService
{
    public LaunchResult NextResult { get; set; } =
        new(LaunchStatus.Launched, null, Array.Empty<string>());

    public IReadOnlyList<Guid> LaunchCalls { get; } = new List<Guid>();

    public LaunchResult Launch(Guid profileId)
    {
        ((List<Guid>)LaunchCalls).Add(profileId);
        return NextResult;
    }
}

/// <summary>
/// In-memory <see cref="IModRepository"/> for VM tests: backs the lookup surface
/// the mod-list VM joins source + version from, plus the path-derivation helper
/// used by staging tests. Tests seed containers directly; mutations update the
/// in-memory store.
/// </summary>
internal class FakeModRepository : IModRepository
{
    private readonly Dictionary<Guid, ModContainer> _byId = new();
    private readonly Dictionary<string, Guid> _untrackedByName = new(StringComparer.Ordinal);
    private readonly string _fakeRoot = Path.Combine(Path.GetTempPath(), "curator-fakerepo-" + Guid.NewGuid());

    public IReadOnlyList<ModContainer> List() => _byId.Values.ToArray();

    public ModContainer? Get(Guid containerId) =>
        _byId.TryGetValue(containerId, out var c) ? c : null;

    public ModContainer? FindBySource(ModSource source)
    {
        if (source is UntrackedSource)
        {
            return null;
        }
        return source switch
        {
            NexusSource n => _byId.Values.FirstOrDefault(c =>
                c.Source is NexusSource ns && ns.ModId == n.ModId),
            GitHubSource g => _byId.Values.FirstOrDefault(c =>
                c.Source is GitHubSource gs
                && string.Equals(gs.Owner, g.Owner, StringComparison.Ordinal)
                && string.Equals(gs.Repo, g.Repo, StringComparison.Ordinal)),
            _ => null,
        };
    }

    public ModContainer? FindUntrackedByName(string name) =>
        _untrackedByName.TryGetValue(name, out var id) ? Get(id) : null;

    public ModContainer CreateContainer(ModSource source, string name)
    {
        var container = new ModContainer
        {
            Id = Guid.NewGuid(),
            Source = source,
            Name = name,
            Versions = Array.Empty<ModVersion>(),
        };
        _byId[container.Id] = container;
        if (source is UntrackedSource)
        {
            _untrackedByName[name] = container.Id;
        }
        return container;
    }

    public ModContainer AddVersion(
        Guid containerId, string versionString, Action<string> populateFolder, DateTimeOffset? remoteUploadedAt = null)
    {
        if (!_byId.TryGetValue(containerId, out var container))
        {
            throw new KeyNotFoundException($"No container {containerId}");
        }

        var existing = container.Versions.FirstOrDefault(v => v.VersionString == versionString);
        List<ModVersion> versions;
        if (existing is not null)
        {
            // Mirror the production repo: dedup refreshes RemoteUploadedAt.
            var refreshed = existing with { RemoteUploadedAt = remoteUploadedAt };
            versions = container.Versions.Select(v => ReferenceEquals(v, existing) ? refreshed : v).ToList();
        }
        else
        {
            var entry = new ModVersion
            {
                Folder = Guid.NewGuid().ToString("N"),
                VersionString = versionString,
                IsLatest = true,
                ImportedAt = DateTimeOffset.UtcNow,
                RemoteUploadedAt = remoteUploadedAt,
            };
            versions = container.Versions
                .Select(v => v with { IsLatest = false })
                .Append(entry)
                .ToList();
        }
        var updated = container with { Versions = versions };
        _byId[containerId] = updated;
        return updated;
    }

    public void RemoveVersion(Guid containerId, string versionFolder)
    {
        if (!_byId.TryGetValue(containerId, out var container))
        {
            return;
        }
        var updated = container with
        {
            Versions = container.Versions.Where(v => v.Folder != versionFolder).ToArray(),
        };
        _byId[containerId] = updated;
    }

    public string GetVersionFolderPath(Guid containerId, string versionFolder) =>
        Path.Combine(_fakeRoot, containerId.ToString(), versionFolder);

    public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced)
    {
        // Minimal fake: drop unreferenced versions + empty containers, mirroring
        // the real repository's behavior.
        foreach (var container in _byId.Values.ToArray())
        {
            var keep = container.Versions
                .Where(v => referenced.Contains((container.Id, v.Folder)))
                .ToArray();
            if (keep.Length == 0)
            {
                _byId.Remove(container.Id);
            }
            else
            {
                _byId[container.Id] = container with { Versions = keep };
            }
        }
    }

    // Rescan + Relocate are repository-lifecycle operations exercised by the
    // Mods-layer tests; the VM tests never drive them. Recorded as no-ops so a
    // future VM test that wires them can assert on the call.
    public int RescanCalls { get; private set; }
    public virtual void Rescan() => RescanCalls++;

    public IReadOnlyList<string> RelocateArgs { get; } = new List<string>();
    public virtual void Relocate(string newBasePath) =>
        ((List<string>)RelocateArgs).Add(newBasePath);

    /// <summary>Test helper: seed a container with a single latest version.</summary>
    public ModContainer Seed(ModSource source, string name, string versionString = "1.0")
    {
        var container = CreateContainer(source, name);
        return AddVersion(container.Id, versionString, _ => { });
    }
}

/// <summary>
/// Recording <see cref="IModImportService"/> for VM tests. Captures each Import
/// call (source path, mod name, parsed source, version) so tests can assert the
/// add flow recorded the right metadata. Optionally upserts a wired
/// <see cref="IModRepository"/> so the add flow's reload joins the freshly
/// imported source + version (mirrors the real import service's behavior). A
/// per-call exception queue lets a test simulate an import failure (an invalid
/// source) to exercise the add flow's catch + alert + abort path.
/// </summary>
internal sealed class FakeModImportService : IModImportService
{
    private readonly IModRepository? _repo;

    public FakeModImportService(IModRepository? repo = null) => _repo = repo;

    public IReadOnlyList<(string SourcePath, string ModName, ModSource Source, string Version)> Imports { get; }
        = new List<(string, string, ModSource, string)>();

    /// <summary>
    /// Optional per-call queue: each Import call dequeues one exception and
    /// throws it (after recording the call), simulating an invalid source. A
    /// <c>null</c> slot means "succeed for this call". When empty / unset, Import
    /// proceeds normally. Mirrors <see cref="FakeDialogService.ImportResultQueue"/>.
    /// </summary>
    public Queue<Exception?>? ImportExceptionQueue { get; set; }

    public (Guid ContainerId, string VersionId) Import(
        string sourcePath, string modName, ModSource source, string version, DateTimeOffset? remoteUploadedAt = null)
    {
        ((List<(string, string, ModSource, string)>)Imports).Add((sourcePath, modName, source, version));

        if (ImportExceptionQueue is { Count: > 0 })
        {
            var ex = ImportExceptionQueue.Dequeue();
            if (ex is not null)
            {
                throw ex;
            }
        }

        if (_repo is null)
        {
            // No wired repository: return a synthetic container id + version id
            // so the add flow has something to feed AddMod. Each call gets fresh
            // ids so distinct imports land as distinct entries.
            return (Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        }

        // Mirror the real import service: resolve-or-create the container, then
        // add the version. This keeps the VM's reload join working in tests +
        // yields the version's opaque folder id (the real service's new return).
        ModContainer container;
        if (source is UntrackedSource)
        {
            container = _repo.FindUntrackedByName(modName) ?? _repo.CreateContainer(source, modName);
        }
        else
        {
            container = _repo.FindBySource(source) ?? _repo.CreateContainer(source, modName);
        }
        var updated = _repo.AddVersion(container.Id, version, _ => { }, remoteUploadedAt);
        var versionId = updated.Versions.First(v => v.VersionString == version).Folder;
        return (container.Id, versionId);
    }

    /// <summary>The source paths passed to <see cref="GetBaseName"/>, in order.</summary>
    public IReadOnlyList<string> GetBaseNameCalls { get; } = new List<string>();

    /// <summary>
    /// Optional override for <see cref="GetBaseName"/>: receives the source path
    /// and returns the base name (or throws, to simulate an invalid source).
    /// When unset, the base name is derived from the path (folder name or
    /// <c>.zip</c> stem), never throwing.
    /// </summary>
    public Func<string, string>? GetBaseNameFunc { get; set; }

    /// <summary>
    /// Peeks the base folder name (mirrors <see cref="IModImportService.GetBaseName"/>).
    /// The default derivation never throws; a test that needs an invalid-source
    /// failure sets <see cref="GetBaseNameFunc"/> to throw.
    /// </summary>
    public string GetBaseName(string sourcePath)
    {
        ((List<string>)GetBaseNameCalls).Add(sourcePath);
        if (GetBaseNameFunc is not null)
        {
            return GetBaseNameFunc(sourcePath);
        }
        var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        const string zip = ".zip";
        if (name.EndsWith(zip, StringComparison.OrdinalIgnoreCase) && name.Length > zip.Length)
        {
            name = name.Substring(0, name.Length - zip.Length);
        }
        return name;
    }

    /// <summary>The (source, modName) pairs passed to
    /// <see cref="FindExistingContainer"/>, in order.</summary>
    public IReadOnlyList<(ModSource Source, string ModName)> FindExistingContainerCalls { get; }
        = new List<(ModSource, string)>();

    /// <summary>
    /// Mirrors <see cref="IModImportService.FindExistingContainer"/>: resolves the
    /// container an import would dedup to, against the wired repo, without
    /// creating anything. Returns <c>null</c> when no repo is wired or no
    /// existing container matches.
    /// </summary>
    public ModContainer? FindExistingContainer(ModSource source, string modName)
    {
        ((List<(ModSource, string)>)FindExistingContainerCalls).Add((source, modName));
        if (_repo is null)
        {
            return null;
        }
        return source is UntrackedSource
            ? _repo.FindUntrackedByName(modName)
            : _repo.FindBySource(source);
    }
}

/// <summary>
/// Recording <see cref="IPreferencesService"/> for tests. Captures the last
/// applied (theme, fontScale, language) triple + the number of apply calls so
/// tests can assert the Preferences VM routes changes through the authority.
/// </summary>
internal sealed class FakePreferencesService : IPreferencesService
{
    public int ApplyCalls { get; private set; }
    public ThemeMode LastTheme { get; private set; } = ThemeMode.System;
    public double LastFontScale { get; private set; } = 1.0;
    public string LastLanguage { get; private set; } = "en";

    public void ApplyAndPersist(ThemeMode theme, double fontScale, string language)
    {
        ApplyCalls++;
        LastTheme = theme;
        LastFontScale = fontScale;
        LastLanguage = language;
    }
}

/// <summary>
/// Recording <see cref="IConfigLoader"/> for tests. <see cref="Save"/> captures
/// the last-written config AND mirrors the real loader's round-trip by
/// promoting it to the live <see cref="Config"/> (so a subsequent
/// <see cref="Load"/> returns what was saved, like the real on-disk file).
/// Returns a configurable config from <see cref="Load"/> (defaults to a fresh
/// <see cref="CuratorConfig.CreateDefault"/>).
/// </summary>
internal sealed class FakeConfigLoader : IConfigLoader
{
    public CuratorConfig Config { get; set; } = CuratorConfig.CreateDefault();
    public int SaveCalls { get; private set; }
    public CuratorConfig? LastSaved { get; private set; }

    public CuratorConfig Load() => Config;

    public void Save(CuratorConfig config)
    {
        SaveCalls++;
        LastSaved = config;
        // Promote to the live Config so a subsequent Load returns the saved
        // state (mirrors the real loader's round-trip through the disk file).
        Config = config;
    }
}

/// <summary>
/// Configurable <see cref="IUpdateCheckService"/> shared by the runner tests
/// (call recording) + the mod-list VM tests (settable LastResult +
/// <see cref="RaiseCheckCompleted"/>). <see cref="CheckAsync"/> records the
/// profile id (Month-only path), optionally throws, sets <see cref="LastResult"/>,
/// + raises <see cref="CheckCompleted"/> (mirrors the real service's atomic
/// publish). <see cref="CheckThoroughAsync"/> mirrors that for the thorough
/// path. Tests that drive the badge refresh directly set
/// <see cref="LastResult"/> + call <see cref="RaiseCheckCompleted"/> without
/// invoking either method.
/// </summary>
internal sealed class FakeUpdateCheckService : IUpdateCheckService
{
    private readonly ConcurrentQueue<Guid> _calls = new();
    private readonly ConcurrentQueue<Guid> _thoroughCalls = new();

    /// <summary>The number of <see cref="CheckAsync"/> (Month-only) calls
    /// recorded so far. Thread-safe; safe to poll from the test thread while
    /// the runner fires on a thread-pool task.</summary>
    public int CallCount => _calls.Count;

    /// <summary>The profile ids passed to <see cref="CheckAsync"/>, in call
    /// order. A snapshot (<see cref="ConcurrentQueue{T}.ToArray"/>); safe to
    /// read after <see cref="Calls"/>/<see cref="CallCount"/> reach the expected
    /// count.</summary>
    public IReadOnlyList<Guid> Calls => _calls.ToArray();

    /// <summary>The number of <see cref="CheckThoroughAsync"/> calls recorded
    /// so far. Thread-safe.</summary>
    public int ThoroughCallCount => _thoroughCalls.Count;

    /// <summary>The profile ids passed to <see cref="CheckThoroughAsync"/>, in
    /// call order.</summary>
    public IReadOnlyList<Guid> ThoroughCalls => _thoroughCalls.ToArray();

    /// <summary>
    /// When set, thrown synchronously from every <see cref="CheckAsync"/> +
    /// <see cref="CheckThoroughAsync"/> call, after the call is recorded. Lets
    /// the exception-safety test assert the call was made AND that the runner
    /// swallowed the throw.
    /// </summary>
    public Exception? ThrowOnCheck { get; set; }

    /// <summary>
    /// The last check result, or <c>null</c> before the first check. Public
    /// setter so the mod-list VM tests can stage a result without invoking
    /// <see cref="CheckAsync"/>; <see cref="CheckAsync"/> +
    /// <see cref="CheckThoroughAsync"/> also set this on a real call (mirrors
    /// the real service).
    /// </summary>
    public UpdateCheckResult? LastResult { get; set; }

    public event EventHandler<UpdateCheckResult?>? CheckCompleted;

    public Task<UpdateCheckResult> CheckAsync(Guid profileId, CancellationToken ct = default)
    {
        _calls.Enqueue(profileId);

        if (ThrowOnCheck is not null)
        {
            throw ThrowOnCheck;
        }

        LastResult ??= new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false, Thorough: false);

        // Mirror the real service's contract: CheckCompleted is raised exactly
        // once per call. Also keeps the event field used (CS0067).
        CheckCompleted?.Invoke(this, LastResult);

        return Task.FromResult(LastResult);
    }

    public Task<UpdateCheckResult> CheckThoroughAsync(Guid profileId, CancellationToken ct = default)
    {
        _thoroughCalls.Enqueue(profileId);

        if (ThrowOnCheck is not null)
        {
            throw ThrowOnCheck;
        }

        LastResult = new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false, Thorough: true);
        CheckCompleted?.Invoke(this, LastResult);
        return Task.FromResult(LastResult);
    }

    /// <summary>
    /// Sets <see cref="LastResult"/> + raises <see cref="CheckCompleted"/> so a
    /// test can simulate a check landing without invoking
    /// <see cref="CheckAsync"/>/<see cref="CheckThoroughAsync"/>. The mod-list
    /// VM's handler reads <see cref="LastResult"/> via ApplyUpdateCheckResult.
    /// </summary>
    public void RaiseCheckCompleted(UpdateCheckResult? result)
    {
        LastResult = result;
        CheckCompleted?.Invoke(this, result);
    }
}

/// <summary>
/// Recording <see cref="IModAcquisitionService"/> for the mod-list VM tests.
/// Captures each <see cref="AcquireLatestNexusAsync"/> call + optionally throws
/// to simulate a failed update. The base <see cref="AcquireFromNexusAsync"/> is
/// wired to the same recorder (tests assert on the unified call list).
/// </summary>
internal sealed class FakeModAcquisitionService : IModAcquisitionService
{
    public List<(string GameDomain, int ModId)> LatestNexusCalls { get; } = new();
    public (Guid ContainerId, string VersionId) NextResult { get; set; } =
        (Guid.NewGuid(), Guid.NewGuid().ToString("N"));
    public Exception? ThrowNext { get; set; }

    public Task<(Guid ContainerId, string VersionId)> AcquireFromNexusAsync(
        string gameDomain, int modId, int fileId,
        string? nxmKey = null, long? nxmExpires = null,
        IProgress<long>? progress = null, CancellationToken ct = default) =>
        throw new NotImplementedException("AcquireFromNexusAsync is not exercised by the mod-list VM tests");

    public Task<(Guid ContainerId, string VersionId)> AcquireLatestNexusAsync(
        string gameDomain, int modId,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        LatestNexusCalls.Add((gameDomain, modId));
        if (ThrowNext is not null)
        {
            return Task.FromException<(Guid, string)>(ThrowNext);
        }
        return Task.FromResult(NextResult);
    }
}

/// <summary>
/// Configurable <see cref="INexusAuthService"/> for the mod-list VM tests. The
/// mod-list VM reads <see cref="GetCurrentStateAsync"/> once at construction
/// for the premium flag; this fake returns the configured
/// <see cref="State"/> (default a premium user; set null / non-premium to test
/// the gating). The login / sign-out methods are not exercised by the mod-list
/// VM + throw NotImplemented. <see cref="AuthStateChanged"/> is wired for the
/// DMF prompt coordinator tests.
/// </summary>
internal sealed class FakeNexusAuthService : INexusAuthService
{
    /// <summary>The state returned by the next GetCurrentStateAsync call.
    /// Default a premium OAuth user so the Update button is visible by default;
    /// tests that exercise non-premium gating set this to a non-premium
    /// state.</summary>
    public NexusAuthState? State { get; set; } = new(NexusAuthMethod.OAuth, "tester", IsPremium: true);

    /// <inheritdoc />
    public event EventHandler? AuthStateChanged;

    /// <summary>
    /// Raises <see cref="AuthStateChanged"/> with this sender. Used by the DMF
    /// prompt coordinator tests to simulate the auth-configured signal that the
    /// production service raises from its login / sign-out methods.
    /// </summary>
    public void RaiseAuthStateChanged() => AuthStateChanged?.Invoke(this, EventArgs.Empty);

    public Task<NexusAuthState?> GetCurrentStateAsync(CancellationToken ct = default) =>
        Task.FromResult(State);

    public Task<NexusAuthResult> LoginWithOAuthAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<NexusAuthResult> LoginWithApiKeyAsync(string apiKey, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task SignOutAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();
}

/// <summary>
/// Recording <see cref="INxmHandlerRegistrar"/> for the Integrations + DMF +
/// shell tests. The real registrar probes the OS; this one returns a settable
/// <see cref="Registered"/> flag and records Register/Unregister calls. Can be
/// configured to throw on Register to exercise the failure path.
/// </summary>
internal sealed class FakeNxmHandlerRegistrar : INxmHandlerRegistrar
{
    /// <summary>The value returned by <see cref="IsRegistered"/>.</summary>
    public bool Registered { get; set; }

    /// <summary>When set, thrown from <see cref="Register"/> (after the call is
    /// recorded) so tests can exercise the register-failure path.</summary>
    public Exception? ThrowOnRegister { get; set; }

    /// <summary>When set, thrown from <see cref="Unregister"/> (after the call is
    /// recorded) so tests can exercise the unregister-failure path.</summary>
    public Exception? ThrowOnUnregister { get; set; }

    public int IsRegisteredCalls { get; private set; }
    public int RegisterCalls { get; private set; }
    public int UnregisterCalls { get; private set; }

    public bool IsRegistered()
    {
        IsRegisteredCalls++;
        return Registered;
    }

    public void Register()
    {
        RegisterCalls++;
        if (ThrowOnRegister is not null)
        {
            throw ThrowOnRegister;
        }
        Registered = true;
    }

    public void Unregister()
    {
        UnregisterCalls++;
        if (ThrowOnUnregister is not null)
        {
            throw ThrowOnUnregister;
        }
        Registered = false;
    }
}

/// <summary>
/// Configurable <see cref="IAppUpdateService"/> for the app self-update runner
/// tests. <see cref="CheckAsync"/> records the call, optionally throws (after
/// recording), and returns a settable result while raising
/// <see cref="UpdateStateChanged"/> (mirrors the real service's atomic publish).
/// The download + apply members record their calls for assertion but are not
/// driven by the runner (the runner only fires the check). Thread-safe recording
/// so the runner's thread-pool dispatch can be polled from the test thread.
/// </summary>
internal sealed class FakeAppUpdateService : IAppUpdateService
{
    private readonly ConcurrentQueue<int> _checkCalls = new();
    private readonly ConcurrentQueue<int> _downloadCalls = new();
    private readonly ConcurrentQueue<int> _applyCalls = new();

    /// <summary>The number of <see cref="CheckForUpdatesAsync"/> calls recorded
    /// so far. Thread-safe; safe to poll from the test thread while the runner
    /// fires on a thread-pool task.</summary>
    public int CheckCallCount => _checkCalls.Count;

    /// <summary>The number of <see cref="DownloadUpdatesAsync"/> calls recorded
    /// so far. Thread-safe.</summary>
    public int DownloadCallCount => _downloadCalls.Count;

    /// <summary>The number of <see cref="ApplyUpdatesAndRestart"/> calls recorded
    /// so far. Thread-safe.</summary>
    public int ApplyCallCount => _applyCalls.Count;

    /// <summary>
    /// The value returned by the next <see cref="CheckForUpdatesAsync"/> call
    /// (default <c>null</c> = no update). When non-null, it is also published on
    /// <see cref="LastCheckResult"/> and announced via
    /// <see cref="UpdateStateChanged"/>.
    /// </summary>
    public AppUpdateInfo? NextCheckResult { get; set; }

    /// <summary>
    /// When set, thrown synchronously from every
    /// <see cref="CheckForUpdatesAsync"/> call, after the call is recorded. Lets
    /// the exception-safety test assert the call was made AND that the runner
    /// swallowed the throw.
    /// </summary>
    public Exception? ThrowOnCheck { get; set; }

    /// <summary>
    /// When set, thrown from every <see cref="DownloadUpdatesAsync"/> call
    /// (after recording) so the shell/settings VM tests can exercise the
    /// download-failure alert path. Default <c>null</c> = success.
    /// </summary>
    public Exception? ThrowOnDownload { get; set; }

    /// <summary>
    /// The supported / installed flag exposed by the fake. Defaults to
    /// <c>true</c> so the runner's check is not short-circuited; tests that
    /// exercise the unsupported path set this to <c>false</c>.
    /// </summary>
    public bool IsUpdateSupported { get; set; } = true;

    public string? CurrentVersion { get; set; } = "1.0.0";

    /// <summary>
    /// The last check result exposed by the fake. Public setter so the
    /// shell/settings VM tests can stage a result without invoking a check
    /// (mirrors <see cref="FakeUpdateCheckService.LastResult"/>).
    /// </summary>
    public AppUpdateInfo? LastCheckResult { get; set; }

    public AppUpdateInfo? UpdatePendingRestart { get; private set; }

    public event EventHandler? UpdateStateChanged;

    /// <summary>
    /// Raises <see cref="UpdateStateChanged"/> (mirrors how the real service
    /// publishes from its background check + how
    /// <see cref="FakeUpdateCheckService.RaiseCheckCompleted"/> works). Used by
    /// the shell/settings VM tests to simulate a check landing without invoking
    /// <see cref="CheckForUpdatesAsync"/>.
    /// </summary>
    public void RaiseUpdateStateChanged() => UpdateStateChanged?.Invoke(this, EventArgs.Empty);

    public Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        _checkCalls.Enqueue(1);
        if (ThrowOnCheck is not null)
        {
            throw ThrowOnCheck;
        }

        LastCheckResult = NextCheckResult;
        UpdateStateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(NextCheckResult);
    }

    public Task DownloadUpdatesAsync(CancellationToken ct = default)
    {
        _downloadCalls.Enqueue(1);

        // When set, thrown before the download is recorded as successful so the
        // caller's download flow surfaces the failure (the shell/settings VM
        // tests exercise the alert path). Defaults to null (success).
        if (ThrowOnDownload is not null)
        {
            return Task.FromException(ThrowOnDownload);
        }

        UpdatePendingRestart = LastCheckResult;
        UpdateStateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void ApplyUpdatesAndRestart() => _applyCalls.Enqueue(1);
}
