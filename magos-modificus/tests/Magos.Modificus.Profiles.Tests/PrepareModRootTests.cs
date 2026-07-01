using System.Text;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// <see cref="IProfileService.PrepareModRoot"/> + <c>mods.lst</c> generation
/// contract: enabled-only, honors <see cref="ModListEntry.Order"/>, disabled
/// omitted, empty list -> empty file, UTF-8 no BOM, trailing newline,
/// idempotent, returns the --mod-path.
/// </summary>
public sealed class PrepareModRootTests
{
    [Fact]
    public void Returns_mod_root_path_and_writes_mods_lst()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");

        var modPath = fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal(fx.ModRoot(profile.Id), modPath);
        Assert.True(Directory.Exists(modPath));
        Assert.True(File.Exists(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_lists_enabled_mods_in_order_one_per_line_with_trailing_newline()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.AddMod(profile.Id, "ModB");
        fx.Service.AddMod(profile.Id, "ModC");
        // Reverse the order so we prove Order is honored, not insertion order.
        fx.Service.SetModOrder(profile.Id, ["ModC", "DMF", "ModB"]);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal("ModC\nDMF\nModB\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_omits_disabled_mods()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.AddMod(profile.Id, "DisabledMod");
        fx.Service.SetModEnabled(profile.Id, "DisabledMod", enabled: false);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal("DMF\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_is_empty_file_when_no_enabled_mods()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P"); // no mods at all

        fx.Service.PrepareModRoot(profile.Id);

        Assert.True(File.Exists(fx.ModsLst(profile.Id)));
        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_is_empty_file_when_all_mods_disabled()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.SetModEnabled(profile.Id, "DMF", enabled: false);

        fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal(string.Empty, File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_is_utf8_without_bom()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");

        fx.Service.PrepareModRoot(profile.Id);

        var bytes = File.ReadAllBytes(fx.ModsLst(profile.Id));
        // A UTF-8 BOM would be 0xEF 0xBB 0xBF at the start.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "mods.lst must not carry a UTF-8 BOM.");
        Assert.Equal("DMF\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ModsLst_faithful_to_stored_entries_with_duplicate_names()
    {
        // Duplicate names can't arise through the public AddMod (it's
        // idempotent), but generation must remain faithful + deterministic if
        // the persisted list ever contains them — one line per entry, in Order.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.AddMod(profile.Id, "ModB");
        // Hand-craft a profile.json with a duplicate entry, bypassing AddMod.
        var dupProfile = new
        {
            Id = profile.Id,
            Name = "P",
            CreatedAt = profile.CreatedAt,
            Mods = new[]
            {
                new { Name = "DMF", Enabled = true, Order = 0 },
                new { Name = "DMF", Enabled = true, Order = 1 },
                new { Name = "ModB", Enabled = true, Order = 2 },
            }
        };
        File.WriteAllText(fx.ProfileJson(profile.Id),
            System.Text.Json.JsonSerializer.Serialize(dupProfile),
            new UTF8Encoding(false));

        fx.Service.PrepareModRoot(profile.Id);

        // Faithful: both DMF entries are written, in Order. No dedup, no crash.
        Assert.Equal("DMF\nDMF\nModB\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void PrepareModRoot_is_idempotent_on_repeat_calls()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.AddMod(profile.Id, "ModB");

        var first = fx.Service.PrepareModRoot(profile.Id);
        var firstContent = File.ReadAllText(fx.ModsLst(profile.Id));
        var second = fx.Service.PrepareModRoot(profile.Id);
        var secondContent = File.ReadAllText(fx.ModsLst(profile.Id));

        Assert.Equal(first, second);
        Assert.Equal(firstContent, secondContent);
        Assert.Equal("DMF\nModB\n", secondContent);
    }

    [Fact]
    public void PrepareModRoot_reflects_latest_state_after_changes()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.PrepareModRoot(profile.Id);
        Assert.Equal("DMF\n", File.ReadAllText(fx.ModsLst(profile.Id)));

        fx.Service.AddMod(profile.Id, "ModB");
        fx.Service.SetModEnabled(profile.Id, "DMF", enabled: false);
        fx.Service.PrepareModRoot(profile.Id);

        // DMF now disabled -> omitted; ModB only.
        Assert.Equal("ModB\n", File.ReadAllText(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void PrepareModRoot_unknown_profile_throws_KeyNotFoundException()
    {
        using var fx = new ProfileServiceFixture();

        Assert.Throws<KeyNotFoundException>(() => fx.Service.PrepareModRoot(Guid.NewGuid()));
    }
}
