using System.Text;
using Magos.Modificus.SharedMods;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// <see cref="IProfileService.PrepareModRoot"/> + <c>mods.lst</c> generation
/// contract under the Phase 2 shared-first staging model: enabled mods that
/// resolve to a present target are symlinked into <c>staged/</c> and written to
/// <c>mods.lst</c> in <see cref="ModListEntry.Order"/>; disabled mods and mods
/// with no staged target are omitted; UTF-8 no BOM; trailing newline; idempotent
/// (clears + rebuilds <c>staged/</c>); returns the <c>--mod-path</c> (<c>staged/</c>).
/// </summary>
/// <remarks>
/// These tests were updated for Phase 2 (shared-first staging); under Phase 1
/// the mod root was a per-profile <c>mods/</c> dir and <c>mods.lst</c> was
/// written from the enabled list regardless of whether files existed. Phase 2
/// stages via symlinks and <c>mods.lst</c> reflects what actually got staged.
/// </remarks>
public sealed class PrepareModRootTests
{
    [Fact]
    public void Returns_staged_path_and_writes_mods_lst()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF");
        fx.Service.AddMod(profile.Id, "DMF");

        var modPath = fx.Service.PrepareModRoot(profile.Id);

        Assert.Equal(fx.StagedDir(profile.Id), modPath);
        Assert.True(Directory.Exists(modPath));
        Assert.True(File.Exists(fx.ModsLst(profile.Id)));
    }

    [Fact]
    public void ModsLst_lists_staged_enabled_mods_in_order_one_per_line_with_trailing_newline()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF");
        fx.AddSharedMod("ModB");
        fx.AddSharedMod("ModC");
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
        fx.AddSharedMod("DMF");
        fx.AddSharedMod("DisabledMod");
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
        fx.AddSharedMod("DMF");
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
        fx.AddSharedMod("DMF");
        fx.Service.AddMod(profile.Id, "DMF");

        fx.Service.PrepareModRoot(profile.Id);

        var bytes = File.ReadAllBytes(fx.ModsLst(profile.Id));
        // A UTF-8 BOM would be 0xEF 0xBB 0xBF at the start.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "mods.lst must not carry a UTF-8 BOM.");
        Assert.Equal("DMF\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void PrepareModRoot_is_idempotent_on_repeat_calls()
    {
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF");
        fx.AddSharedMod("ModB");
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
        fx.AddSharedMod("DMF");
        fx.AddSharedMod("ModB");
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

    [Fact]
    public void ModsLst_faithful_to_stored_entries_with_duplicate_names()
    {
        // Duplicate names can't arise through the public AddMod (it's
        // idempotent), but staging must remain graceful + deterministic if the
        // persisted list ever contains them: one symlink (created by the first
        // occurrence), and the name listed once per entry in Order — faithful to
        // the Phase 1 contract, no crash.
        using var fx = new ProfileServiceFixture();
        var profile = fx.Service.CreateProfile("P");
        fx.AddSharedMod("DMF");
        fx.AddSharedMod("ModB");
        fx.Service.AddMod(profile.Id, "DMF");
        fx.Service.AddMod(profile.Id, "ModB");

        // Hand-craft a profile.json with a duplicate entry (real entries, so the
        // Policy $kind discriminator round-trips), bypassing AddMod's idempotency.
        var dupProfile = new Profile
        {
            Id = profile.Id,
            Name = "P",
            CreatedAt = profile.CreatedAt,
            Mods = new[]
            {
                new ModListEntry { Name = "DMF", Enabled = true, Order = 0, Policy = ModVersionPolicy.Latest },
                new ModListEntry { Name = "DMF", Enabled = true, Order = 1, Policy = ModVersionPolicy.Latest },
                new ModListEntry { Name = "ModB", Enabled = true, Order = 2, Policy = ModVersionPolicy.Latest },
            }
        };
        File.WriteAllText(fx.ProfileJson(profile.Id),
            System.Text.Json.JsonSerializer.Serialize(dupProfile),
            new UTF8Encoding(false));

        fx.Service.PrepareModRoot(profile.Id);

        // Faithful: both DMF entries are listed, in Order. One symlink backs them.
        Assert.Equal("DMF\nDMF\nModB\n", File.ReadAllText(fx.ModsLst(profile.Id)));
        Assert.True(Directory.Exists(fx.StagedModLink(profile.Id, "DMF")));
    }
}
