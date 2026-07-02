using Magos.Modificus.SharedMods;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// Mod-list management on a known profile: <see cref="IProfileService.AddMod"/>,
/// <see cref="IProfileService.RemoveMod"/>, <see cref="IProfileService.SetModOrder"/>,
/// <see cref="IProfileService.SetModEnabled"/>, <see cref="IProfileService.GetModList"/>.
/// </summary>
public sealed class ModListTests
{
    [Fact]
    public void AddMod_appends_enabled_entry_and_persists()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");

        fx.Service.AddMod(profile.Id, "DMF");

        var mods = fx.Service.GetModList(profile.Id);
        var entry = Assert.Single(mods);
        Assert.Equal("DMF", entry.Name);
        Assert.True(entry.Enabled);
        Assert.Equal(0, entry.Order);
    }

    [Fact]
    public void AddMod_assigns_increasing_order_to_subsequent_adds()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");

        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.AddMod(profile.Id, "ModB");
        fx.Service.AddMod(profile.Id, "ModC");

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([("DMF", 0), ("ModB", 1), ("ModC", 2)],
            mods.Select(m => (m.Name, m.Order)).ToArray());
    }

    [Fact]
    public void AddMod_is_idempotent_for_existing_name()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");

        fx.Service.AddMod(profile.Id, "DMF"); // no-op

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.Equal("DMF", entry.Name);
    }

    [Fact]
    public void AddMod_rejects_null_or_whitespace_name()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");

        Assert.Throws<ArgumentException>(() => fx.Service.AddMod(profile.Id, ""));
        Assert.Throws<ArgumentException>(() => fx.Service.AddMod(profile.Id, "  "));
        Assert.Empty(fx.Service.GetModList(profile.Id));
    }

    [Fact]
    public void AddMod_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.AddMod(Guid.NewGuid(), "DMF"));
    }

    [Fact]
    public void AddMod_defaults_to_Latest_policy()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");

        fx.Service.AddMod(profile.Id, "DMF"); // Phase 1-compatible overload

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.IsType<LatestPolicy>(entry.Policy);
    }

    [Fact]
    public void AddMod_with_explicit_Pinned_policy_persists()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var pinned = new PinnedPolicy(new Version(2, 1, 0));

        fx.Service.AddMod(profile.Id, "DMF", pinned);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        var pinnedLoaded = Assert.IsType<PinnedPolicy>(entry.Policy);
        Assert.Equal(new Version(2, 1, 0), pinnedLoaded.Version);
    }

    [Fact]
    public void AddMod_is_idempotent_keeps_existing_policy()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF", new PinnedPolicy(new Version(3, 0, 0)));

        // Re-add via the no-policy overload: idempotent, keeps the Pinned policy.
        fx.Service.AddMod(profile.Id, "DMF");

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.IsType<PinnedPolicy>(entry.Policy);
    }

    [Fact]
    public void SetModPolicy_unknown_mod_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");

        Assert.Throws<KeyNotFoundException>(() =>
            fx.Service.SetModPolicy(profile.Id, "Ghost", ModVersionPolicy.Latest));
    }

    [Fact]
    public void GetModList_loads_Phase1_profile_json_without_policy_as_Latest()
    {
        // A profile.json persisted by Phase 1 (no Policy field) upgrades
        // transparently: each entry's Policy deserializes to Latest.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        var phase1Profile = new
        {
            Id = profile.Id,
            Name = "P",
            CreatedAt = profile.CreatedAt,
            Mods = new[]
            {
                new { Name = "DMF", Enabled = true, Order = 0 }, // no Policy
            }
        };
        File.WriteAllText(fx.ProfileJson(profile.Id),
            System.Text.Json.JsonSerializer.Serialize(phase1Profile),
            new System.Text.UTF8Encoding(false));

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.IsType<LatestPolicy>(entry.Policy);
    }

    [Fact]
    public void RemoveMod_drops_entry_and_deletes_its_diverged_copy()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        // Simulate a diverged (profile-local) copy of the mod.
        var divergedModDir = fx.DivergedModDir(profile.Id, "DMF");
        Directory.CreateDirectory(divergedModDir);
        File.WriteAllText(Path.Combine(divergedModDir, "marker.txt"), "x");
        Assert.True(Directory.Exists(divergedModDir));

        fx.Service.RemoveMod(profile.Id, "DMF");

        Assert.Empty(fx.Service.GetModList(profile.Id));
        Assert.False(Directory.Exists(divergedModDir));
    }

    [Fact]
    public void RemoveMod_is_graceful_when_diverged_copy_was_never_present()
    {
        // "missing local copy for a listed mod -> graceful": the mod is in the
        // list (so RemoveMod removes it) but its diverged/ dir was never created
        // (the mod was shared, never diverged). The shared-store copy is NOT
        // touched (RemoveMod's contract is profile-local only).
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF"); // shared copy exists
        fx.Service.AddMod(profile.Id, "DMF");
        Assert.False(Directory.Exists(fx.DivergedModDir(profile.Id, "DMF")));

        fx.Service.RemoveMod(profile.Id, "DMF"); // must not throw

        Assert.Empty(fx.Service.GetModList(profile.Id));
        // Shared copy untouched.
        Assert.True(Directory.Exists(fx.SharedModDir("DMF")));
    }

    [Fact]
    public void RemoveMod_unknown_mod_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");

        Assert.Throws<KeyNotFoundException>(() => fx.Service.RemoveMod(profile.Id, "NotThere"));
    }

    [Fact]
    public void SetModEnabled_toggles_state_and_persists()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");

        fx.Service.SetModEnabled(profile.Id, "DMF", enabled: false);

        var entry = Assert.Single(fx.Service.GetModList(profile.Id));
        Assert.False(entry.Enabled);

        fx.Service.SetModEnabled(profile.Id, "DMF", enabled: true);
        Assert.True(Assert.Single(fx.Service.GetModList(profile.Id)).Enabled);
    }

    [Fact]
    public void SetModEnabled_unknown_mod_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");

        Assert.Throws<KeyNotFoundException>(() => fx.Service.SetModEnabled(profile.Id, "Ghost", true));
    }

    [Fact]
    public void SetModOrder_reorders_mods_by_name_sequence()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");    // order 0
        fx.Service.AddMod(profile.Id, "ModB");   // order 1
        fx.Service.AddMod(profile.Id, "ModC");   // order 2

        fx.Service.SetModOrder(profile.Id, ["ModC", "DMF", "ModB"]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([("ModC", 0), ("DMF", 1), ("ModB", 2)],
            mods.Select(m => (m.Name, m.Order)).ToArray());
    }

    [Fact]
    public void SetModOrder_appends_unmentioned_mods_in_their_relative_order()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.AddMod(profile.Id, "ModB");
        fx.Service.AddMod(profile.Id, "ModC");

        // Only DMF is mentioned; ModB + ModC keep their order, after DMF.
        fx.Service.SetModOrder(profile.Id, ["DMF"]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Equal([("DMF", 0), ("ModB", 1), ("ModC", 2)],
            mods.Select(m => (m.Name, m.Order)).ToArray());
    }

    [Fact]
    public void SetModOrder_ignores_names_not_in_the_profile()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");

        fx.Service.SetModOrder(profile.Id, ["DMF", "Imaginary", "AlsoImaginary"]);

        var mods = fx.Service.GetModList(profile.Id);
        Assert.Single(mods);
        Assert.Equal("DMF", mods[0].Name);
    }

    [Fact]
    public void SetModOrder_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.SetModOrder(Guid.NewGuid(), ["DMF"]));
    }
}
