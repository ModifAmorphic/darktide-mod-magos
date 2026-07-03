using CommunityToolkit.Mvvm.ComponentModel;
using Magos.Modificus.EnginseerClient;
using Magos.Modificus.General;
using Magos.Modificus.Profiles;
using Magos.Modificus.SharedMods;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.Session;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Hand-rolled test doubles for the shell/manage VMs' dependencies. No mock
/// library is used anywhere in the repo; these recording fakes match that style
/// and keep the test project dependency-free.
/// </summary>
internal static class TestDoubles
{
    public static FakeProfileService Profiles(params ProfileSummary[] seed) => new(seed);
}

/// <summary>
/// In-memory <see cref="IProfileService"/> for VM tests: backs only the CRUD +
/// listing surface the shell/manage VMs touch. Records calls so tests can assert
/// on them. <c>ModList</c>/<c>PrepareModRoot</c>-style members throw
/// <see cref="NotImplementedException"/> (out of scope for milestone-2 VM tests).
/// </summary>
internal sealed class FakeProfileService : IProfileService
{
    private readonly List<ProfileSummary> _profiles;

    public FakeProfileService(IEnumerable<ProfileSummary> seed) =>
        _profiles = new List<ProfileSummary>(seed);

    public IReadOnlyList<string> CreatedNames { get; } = new List<string>();
    public IReadOnlyList<(Guid Id, string Name)> Renames { get; } = new List<(Guid, string)>();
    public IReadOnlyList<Guid> DeletedIds { get; } = new List<Guid>();

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
        ((List<Guid>)DeletedIds).Add(id);
    }

    // ---- Out of scope for milestone-2 VM tests (Tracks B/C) -----------------

    public IReadOnlyList<ModListEntry> GetModList(Guid id) => throw new NotImplementedException();
    public void SetModOrder(Guid id, IReadOnlyList<string> modNamesInOrder) => throw new NotImplementedException();
    public void SetModEnabled(Guid id, string modName, bool enabled) => throw new NotImplementedException();
    public void AddMod(Guid id, string modName) => throw new NotImplementedException();
    public void AddMod(Guid id, string modName, ModVersionPolicy policy) => throw new NotImplementedException();
    public void SetModPolicy(Guid id, string modName, ModVersionPolicy policy) => throw new NotImplementedException();
    public void RemoveMod(Guid id, string modName) => throw new NotImplementedException();
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
/// deleting profiles and routing active changes through the session). Records
/// calls so tests can assert the dialog was opened.
/// </summary>
internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public Action? OnManageProfiles { get; set; }
    public int ConfirmCalls { get; private set; }
    public string? LastConfirmMessage { get; private set; }
    public int ManageProfilesCalls { get; private set; }

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
