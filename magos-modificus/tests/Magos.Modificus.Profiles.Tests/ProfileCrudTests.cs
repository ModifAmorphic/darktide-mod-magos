using Microsoft.Extensions.DependencyInjection;
using Magos.Modificus.Config;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// CRUD round-trip + first-run + edge cases for <see cref="IProfileService"/>
/// profile lifecycle. Each test gets a fresh temp <c>ProfilesBaseFolder</c>.
/// </summary>
public sealed class ProfileCrudTests
{
    [Fact]
    public void CreateProfile_scaffolds_dir_and_staged_and_diverged_and_persists_profile_json()
    {
        using var fx = new ProfileServiceFixture();

        var profile = fx.Service.CreateProfile("Vanilla+");

        Assert.NotEqual(Guid.Empty, profile.Id);
        Assert.Equal("Vanilla+", profile.Name);
        Assert.Equal(DateTimeOffset.UtcNow, profile.CreatedAt, TimeSpan.FromSeconds(5));
        Assert.Empty(profile.Mods);

        // Dir + staged/ + mods/ + profile.json all created.
        Assert.True(Directory.Exists(fx.ProfileDir(profile.Id)));
        Assert.True(Directory.Exists(fx.StagedDir(profile.Id)));
        Assert.True(Directory.Exists(fx.DivergedDir(profile.Id)));
        Assert.True(File.Exists(fx.ProfileJson(profile.Id)));
    }

    [Fact]
    public void CreateProfile_rejects_null_or_whitespace_name()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<ArgumentException>(() => fx.Service.CreateProfile(""));
        Assert.Throws<ArgumentException>(() => fx.Service.CreateProfile("   "));
    }

    [Fact]
    public void GetProfile_returns_persisted_profile_across_instances()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("My Profile");

        // A second service instance reads the same disk state — proves the
        // profile genuinely persists, not just in-memory.
        var reloadConfig = MagosConfig.CreateDefault();
        reloadConfig.ProfilesBaseFolder = fx.BaseFolder;
        using var reloadFx = new ReloadFixture(reloadConfig);

        var loaded = reloadFx.Service.GetProfile(created.Id);

        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("My Profile", loaded.Name);
        Assert.Equal(created.CreatedAt, loaded.CreatedAt);
        Assert.Empty(loaded.Mods);
    }

    [Fact]
    public void GetProfile_unknown_id_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.GetProfile(Guid.NewGuid()));
    }

    [Fact]
    public void GetProfile_coerces_a_hand_edited_null_mods_to_empty()
    {
        using var fx = new ProfileServiceFixture();
        var created = fx.Service.CreateProfile("P");

        // Simulate a hand-edit (or a future schema regression) writing Mods:null.
        var id = created.Id.ToString();
        File.WriteAllText(fx.ProfileJson(created.Id),
            $$"""{"Id":"{{id}}","Name":"P","CreatedAt":"2024-01-01T00:00:00Z","Mods":null}""");

        var loaded = fx.Service.GetProfile(created.Id);

        Assert.NotNull(loaded.Mods);
        Assert.Empty(loaded.Mods); // enumeration safe — no NRE downstream
    }

    [Fact]
    public void ListProfiles_returns_all_created_profiles_as_summaries()
    {
        using var fx = new ProfileServiceFixture();
        var a = fx.Service.CreateProfile("A");
        var b = fx.Service.CreateProfile("B");

        var summaries = fx.Service.ListProfiles();

        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s is { Id: var id, Name: "A" } && id == a.Id);
        Assert.Contains(summaries, s => s is { Id: var id, Name: "B" } && id == b.Id);
    }

    [Fact]
    public void ListProfiles_is_sorted_by_name_ordinal()
    {
        using var fx = new ProfileServiceFixture();
        fx.Service.CreateProfile("Charlie");
        fx.Service.CreateProfile("alpha");
        fx.Service.CreateProfile("Bravo");

        var names = fx.Service.ListProfiles().Select(s => s.Name).ToArray();

        // Ordinal sort: uppercase precedes lowercase in ASCII, so this order
        // also distinguishes Ordinal from OrdinalIgnoreCase (which would put
        // 'alpha' first).
        Assert.Equal(new[] { "Bravo", "Charlie", "alpha" }, names);
    }

    [Fact]
    public void ListProfiles_is_empty_when_no_profiles_exist()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Empty(fx.Service.ListProfiles());
    }

    [Fact]
    public void ListProfiles_skips_non_guid_and_corrupted_directories()
    {
        using var fx = new ProfileServiceFixture();
        fx.Service.CreateProfile("Good");

        // A non-guid dir (ignored).
        Directory.CreateDirectory(Path.Combine(fx.BaseFolder, "not-a-guid"));
        // A guid dir with a corrupted profile.json (skipped, not fatal).
        var badId = Guid.NewGuid();
        var badDir = Path.Combine(fx.BaseFolder, badId.ToString());
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "profile.json"), "{ this is not json");

        var summaries = fx.Service.ListProfiles();

        var only = Assert.Single(summaries);
        Assert.Equal("Good", only.Name);
    }

    [Fact]
    public void RenameProfile_updates_name_on_disk()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("Old");

        fx.Service.RenameProfile(profile.Id, "New");

        // The directory name is unchanged (id-keyed); only the name changed.
        Assert.True(Directory.Exists(fx.ProfileDir(profile.Id)));
        var reloaded = fx.Service.GetProfile(profile.Id);
        Assert.Equal("New", reloaded.Name);
    }

    [Fact]
    public void RenameProfile_unknown_id_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.RenameProfile(Guid.NewGuid(), "X"));
    }

    [Fact]
    public void RenameProfile_rejects_null_or_whitespace_name()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("Old");

        Assert.Throws<ArgumentException>(() => fx.Service.RenameProfile(profile.Id, ""));
        Assert.Throws<ArgumentException>(() => fx.Service.RenameProfile(profile.Id, "   "));
        // Name unchanged after the rejected renames.
        Assert.Equal("Old", fx.Service.GetProfile(profile.Id).Name);
    }

    [Fact]
    public void DeleteProfile_removes_entry_and_directory_tree()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("Doomed");
        fx.Service.AddMod(profile.Id, "SomeMod");
        var modPath = fx.Service.PrepareModRoot(profile.Id); // writes mods/ + mods.lst + populates
        Assert.True(Directory.Exists(fx.ProfileDir(profile.Id)));

        fx.Service.DeleteProfile(profile.Id);

        Assert.False(Directory.Exists(fx.ProfileDir(profile.Id)));
        Assert.Empty(fx.Service.ListProfiles());
    }

    [Fact]
    public void DeleteProfile_unknown_id_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.DeleteProfile(Guid.NewGuid()));
    }

    [Fact]
    public void FirstRun_creates_missing_ProfilesBaseFolder()
    {
        // Point at a path whose parent chain does not exist yet; constructing
        // the service (via AddProfiles DI) must create the full chain.
        var tempRoot = Path.Combine(Path.GetTempPath(), "magos-firstrun-" + Guid.NewGuid());
        var baseFolder = Path.Combine(tempRoot, "deep", "profiles");
        Assert.False(Directory.Exists(baseFolder));

        try
        {
            var config = MagosConfig.CreateDefault();
            config.ProfilesBaseFolder = baseFolder;
            var services = new ServiceCollection();
            services.AddSingleton(config);
            services.AddLogging();
            services.AddProfiles();
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IProfileService>(); // forces construction

            Assert.True(Directory.Exists(baseFolder));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>Resolves a second <see cref="IProfileService"/> against a given config.</summary>
    private sealed class ReloadFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public IProfileService Service { get; }

        public ReloadFixture(MagosConfig config)
        {
            _provider = new ServiceCollection()
                .AddSingleton(config)
                .AddLogging()
                .AddProfiles()
                .BuildServiceProvider();
            Service = _provider.GetRequiredService<IProfileService>();
        }

        public void Dispose() => _provider.Dispose();
    }
}
