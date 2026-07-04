using CommunityToolkit.Mvvm.ComponentModel;
using Magos.Modificus.Config;
using Magos.Modificus.EnginseerClient;
using Magos.Modificus.General;
using Magos.Modificus.Profiles;
using Magos.Modificus.SharedMods;
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
    /// fakes. The defaults share one shared store between the store + import fake
    /// so the add flow's reload joins the freshly imported source + version
    /// (mirrors the real import service's upsert).
    /// </summary>
    public static ModListViewModel BuildModList(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeSharedModStore? sharedStore = null,
        FakeModImportService? importService = null,
        IModOrderResolver? orderResolver = null,
        FakeDialogService? dialogs = null,
        LocalizationService? localization = null)
    {
        profiles ??= Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        sharedStore ??= new FakeSharedModStore();
        importService ??= new FakeModImportService(sharedStore);
        orderResolver ??= new IdentityModOrderResolver();
        dialogs ??= new FakeDialogService();
        localization ??= new LocalizationService();
        return new ModListViewModel(
            profiles,
            session,
            sharedStore,
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

    public IReadOnlyList<(Guid Id, string ModName, bool Enabled)> SetModEnabledCalls { get; } = new List<(Guid, string, bool)>();
    public IReadOnlyList<IReadOnlyList<string>> SetModOrderCalls { get; } = new List<IReadOnlyList<string>>();
    public IReadOnlyList<(Guid Id, string ModName, ModVersionPolicy Policy)> SetModPolicyCalls { get; } = new List<(Guid, string, ModVersionPolicy)>();
    public IReadOnlyList<(Guid Id, string ModName)> AddModCalls { get; } = new List<(Guid, string)>();
    public IReadOnlyList<(Guid Id, string ModName)> RemoveModCalls { get; } = new List<(Guid, string)>();

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

    public void SetModOrder(Guid id, IReadOnlyList<string> modNamesInOrder)
    {
        ((List<IReadOnlyList<string>>)SetModOrderCalls).Add(modNamesInOrder);

        var list = EnsureList(id);
        var ordered = new List<ModListEntry>();
        var remaining = list.ToList();
        foreach (var name in modNamesInOrder)
        {
            var match = remaining.FirstOrDefault(m => m.Name == name);
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

    public void SetModEnabled(Guid id, string modName, bool enabled)
    {
        ((List<(Guid, string, bool)>)SetModEnabledCalls).Add((id, modName, enabled));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.Name == modName);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No mod {modName} in profile {id}");
        }
        list[idx] = list[idx] with { Enabled = enabled };
    }

    public void AddMod(Guid id, string modName) => AddMod(id, modName, ModVersionPolicy.Latest);

    public void AddMod(Guid id, string modName, ModVersionPolicy policy)
    {
        ((List<(Guid, string)>)AddModCalls).Add((id, modName));

        var list = EnsureList(id);
        if (list.Any(m => m.Name == modName))
        {
            return; // idempotent
        }
        list.Add(new ModListEntry
        {
            Name = modName,
            Enabled = true,
            Order = list.Count,
            Policy = policy,
        });
    }

    public void SetModPolicy(Guid id, string modName, ModVersionPolicy policy)
    {
        ((List<(Guid, string, ModVersionPolicy)>)SetModPolicyCalls).Add((id, modName, policy));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.Name == modName);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No mod {modName} in profile {id}");
        }
        list[idx] = list[idx] with { Policy = policy };
    }

    public void RemoveMod(Guid id, string modName)
    {
        ((List<(Guid, string)>)RemoveModCalls).Add((id, modName));

        var list = EnsureList(id);
        var idx = list.FindIndex(m => m.Name == modName);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"No mod {modName} in profile {id}");
        }
        list.RemoveAt(idx);
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
/// cancel mid-batch by enqueuing a <c>null</c>). Records the requests so tests
/// can assert on the sequence.
/// </summary>
internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public Action? OnManageProfiles { get; set; }
    public Action? OnPreferences { get; set; }
    public int ConfirmCalls { get; private set; }
    public string? LastConfirmMessage { get; private set; }
    public int ManageProfilesCalls { get; private set; }
    public int PreferencesCalls { get; private set; }

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

/// <summary><see cref="ISteamService"/> with a configurable running flag.</summary>
internal sealed class FakeSteamService : ISteamService
{
    public bool Running { get; set; }
    public bool IsGameRunning() => Running;
    public DiscoveryResult Discover() => throw new NotImplementedException();
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
}

/// <summary>No-op launch service (launch is Track C; not exercised here).</summary>
internal sealed class FakeLaunchService : IEnginseerLaunchService
{
    public LaunchResult Launch(Guid profileId) =>
        new(LaunchStatus.Launched, null, Array.Empty<string>());
}

/// <summary>
/// In-memory <see cref="ISharedModStore"/> for VM tests: backs the lookup / upsert
/// surface the mod-list VM joins source + version from. Tests seed entries
/// directly; <see cref="Add"/> upserts (mirrors the real store).
/// </summary>
internal sealed class FakeSharedModStore : ISharedModStore
{
    private readonly Dictionary<string, SharedModEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyList<SharedModEntry> List() => _entries.Values.ToArray();

    public SharedModEntry? Get(string name) =>
        _entries.TryGetValue(name, out var entry) ? entry : null;

    public void Add(SharedModEntry entry) => _entries[entry.Name] = entry;

    public void Remove(string name) => _entries.Remove(name);
}

/// <summary>
/// Recording <see cref="IModImportService"/> for VM tests. Captures each Import
/// call (source path, mod name, parsed source, version) so tests can assert the
/// add flow recorded the right metadata. Optionally upserts a wired
/// <see cref="ISharedModStore"/> so the add flow's reload joins the freshly
/// imported source + version (mirrors the real import service's upsert).
/// </summary>
internal sealed class FakeModImportService : IModImportService
{
    private readonly ISharedModStore? _store;

    public FakeModImportService(ISharedModStore? store = null) => _store = store;

    public IReadOnlyList<(string SourcePath, string ModName, ModSource Source, string Version)> Imports { get; }
        = new List<(string, string, ModSource, string)>();

    public SharedModEntry Import(string sourcePath, string modName, ModSource source, string version)
    {
        ((List<(string, string, ModSource, string)>)Imports).Add((sourcePath, modName, source, version));

        var entry = new SharedModEntry
        {
            Name = modName,
            Source = source,
            ActualVersion = version,
            Path = sourcePath,
            Policy = ModVersionPolicy.Latest,
        };
        _store?.Add(entry);
        return entry;
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
/// the last-written config without touching disk. Returns a configurable config
/// from <see cref="Load"/> (defaults to a fresh <see cref="MagosConfig.CreateDefault"/>).
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
    }
}
