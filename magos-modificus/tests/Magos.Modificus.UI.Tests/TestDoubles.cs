using CommunityToolkit.Mvvm.ComponentModel;
using Magos.Modificus.Config;
using Magos.Modificus.EnginseerClient;
using Magos.Modificus.General;
using Magos.Modificus.Profiles;
using Magos.Modificus.Mods;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Preferences;
using Magos.Modificus.UI.Session;
using Magos.Modificus.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Hand-rolled test doubles for the shell/manage/mod-list VMs' dependencies. No
/// mock library is used anywhere in the repo; these recording fakes match that
/// style and keep the test project dependency-free.
/// </summary>
internal static class TestDoubles
{
    public static FakeProfileService Profiles(params ProfileSummary[] seed) => new(seed);

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
        LocalizationService? localization = null)
    {
        profiles ??= Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        importService ??= new FakeModImportService(repo);
        orderResolver ??= new IdentityModOrderResolver();
        dialogs ??= new FakeDialogService();
        localization ??= new LocalizationService();
        return new ModListViewModel(
            profiles,
            session,
            repo,
            importService,
            orderResolver,
            dialogs,
            localization,
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
    public int ConfirmCalls { get; private set; }
    public string? LastConfirmMessage { get; private set; }
    public int ManageProfilesCalls { get; private set; }
    public int PreferencesCalls { get; private set; }
    public int SettingsCalls { get; private set; }

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
/// Configurable <see cref="IEnginseerLaunchService"/> for shell-VM launch tests.
/// <see cref="NextResult"/> is returned for every Launch call (default:
/// Launched). <see cref="LaunchCalls"/> records the ids the shell asked to
/// launch.
/// </summary>
internal sealed class FakeLaunchService : IEnginseerLaunchService
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
    private readonly string _fakeRoot = Path.Combine(Path.GetTempPath(), "magos-fakerepo-" + Guid.NewGuid());

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

    public ModContainer AddVersion(Guid containerId, string versionString, Action<string> populateFolder)
    {
        if (!_byId.TryGetValue(containerId, out var container))
        {
            throw new KeyNotFoundException($"No container {containerId}");
        }

        var existing = container.Versions.FirstOrDefault(v => v.VersionString == versionString);
        List<ModVersion> versions;
        if (existing is not null)
        {
            versions = container.Versions.ToList();
        }
        else
        {
            var entry = new ModVersion
            {
                Folder = Guid.NewGuid().ToString("N"),
                VersionString = versionString,
                IsLatest = true,
                ImportedAt = DateTimeOffset.UtcNow,
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

    public (Guid ContainerId, string VersionString) Import(string sourcePath, string modName, ModSource source, string version)
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
            // No wired repository: return a synthetic container id so the add flow
            // has something to feed AddMod. Each call gets a fresh id so distinct
            // imports land as distinct entries.
            return (Guid.NewGuid(), version);
        }

        // Mirror the real import service: resolve-or-create the container, then
        // add the version. This keeps the VM's reload join working in tests.
        ModContainer container;
        if (source is UntrackedSource)
        {
            container = _repo.FindUntrackedByName(modName) ?? _repo.CreateContainer(source, modName);
        }
        else
        {
            container = _repo.FindBySource(source) ?? _repo.CreateContainer(source, modName);
        }
        _repo.AddVersion(container.Id, version, _ => { });
        return (container.Id, version);
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
/// <see cref="MagosConfig.CreateDefault"/>).
/// </summary>
internal sealed class FakeConfigLoader : IConfigLoader
{
    public MagosConfig Config { get; set; } = MagosConfig.CreateDefault();
    public int SaveCalls { get; private set; }
    public MagosConfig? LastSaved { get; private set; }

    public MagosConfig Load() => Config;

    public void Save(MagosConfig config)
    {
        SaveCalls++;
        LastSaved = config;
        // Promote to the live Config so a subsequent Load returns the saved
        // state (mirrors the real loader's round-trip through the disk file).
        Config = config;
    }
}
