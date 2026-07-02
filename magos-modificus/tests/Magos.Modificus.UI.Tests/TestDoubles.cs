using Magos.Modificus.EnginseerClient;
using Magos.Modificus.General;
using Magos.Modificus.Profiles;
using Magos.Modificus.SharedMods;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.Dialogs;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Hand-rolled test doubles for the shell/manage VMs' dependencies. No mock
/// library is used anywhere in the repo — these recording fakes match that style
/// and keep the test project dependency-free.
/// </summary>
internal static class TestDoubles
{
    public static FakeProfileService Profiles(params ProfileSummary[] seed) => new(seed);
}

/// <summary>
/// In-memory <see cref="IProfileService"/> for VM tests — backs only the CRUD +
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
/// <see cref="ConfirmAsync"/>; <see cref="ManageProfilesResult"/> is returned
/// verbatim from <see cref="ShowManageProfilesAsync"/>. Records calls so tests
/// can assert the dialog was opened with the expected current active id.
/// </summary>
internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public Guid? ManageProfilesResult { get; set; }
    public Guid? LastCurrentActiveId { get; private set; }
    public int ConfirmCalls { get; private set; }
    public string? LastConfirmMessage { get; private set; }
    public int ManageProfilesCalls { get; private set; }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        ConfirmCalls++;
        LastConfirmMessage = message;
        return Task.FromResult(ConfirmResult);
    }

    public Task<Guid?> ShowManageProfilesAsync(Guid? currentActiveProfileId)
    {
        ManageProfilesCalls++;
        LastCurrentActiveId = currentActiveProfileId;
        return Task.FromResult(ManageProfilesResult);
    }
}

/// <summary><see cref="ISteamService"/> with a configurable running flag.</summary>
internal sealed class FakeSteamService : ISteamService
{
    public bool Running { get; set; }
    public bool IsGameRunning() => Running;
    public DiscoveryResult Discover() => throw new NotImplementedException();
}

/// <summary>No-op launch service (launch is Track C; not exercised here).</summary>
internal sealed class FakeLaunchService : IEnginseerLaunchService
{
    public LaunchResult Launch(Guid profileId) =>
        new(LaunchStatus.Launched, null, Array.Empty<string>());
}
