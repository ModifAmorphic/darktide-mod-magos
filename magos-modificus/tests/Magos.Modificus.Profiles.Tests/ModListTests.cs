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
    public void RemoveMod_drops_entry_and_deletes_its_directory()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        // Simulate an installed mod directory under the mod root.
        var modDir = Path.Combine(fx.ModRoot(profile.Id), "DMF");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "marker.txt"), "x");
        Assert.True(Directory.Exists(modDir));

        fx.Service.RemoveMod(profile.Id, "DMF");

        Assert.Empty(fx.Service.GetModList(profile.Id));
        Assert.False(Directory.Exists(modDir));
    }

    [Fact]
    public void RemoveMod_is_graceful_when_mod_directory_was_never_installed()
    {
        // "missing mod dir for a listed mod -> graceful": the mod is in the
        // list (so RemoveMod removes it) but its folder was never created.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        Assert.False(Directory.Exists(Path.Combine(fx.ModRoot(profile.Id), "DMF")));

        fx.Service.RemoveMod(profile.Id, "DMF"); // must not throw

        Assert.Empty(fx.Service.GetModList(profile.Id));
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
