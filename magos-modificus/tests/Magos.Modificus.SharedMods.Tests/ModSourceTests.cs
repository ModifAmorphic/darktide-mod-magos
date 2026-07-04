using System.Text.Json;

namespace Magos.Modificus.SharedMods.Tests;

/// <summary>
/// <see cref="ModSource"/> JSON polymorphism: the <c>$kind</c> discriminator
/// round-trips for none/nexus/github, and the default for an absent source is
/// <see cref="NoneSource"/>. Mirrors the established
/// <see cref="ModVersionPolicy"/> serialization tests.
/// </summary>
public sealed class ModSourceTests
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    [Theory]
    [InlineData("none", typeof(NoneSource))]
    [InlineData("nexus", typeof(NexusSource))]
    [InlineData("github", typeof(GitHubSource))]
    public void Kind_discriminator_round_trips(string kind, Type expectedType)
    {
        // The serialized discriminator is the stable lowercase identifier.
        var source = kind switch
        {
            "none" => (ModSource)new NoneSource(),
            "nexus" => new NexusSource { ModId = 4242 },
            "github" => new GitHubSource { Owner = "o", Repo = "r" },
            _ => throw new ArgumentException($"unknown kind: {kind}", nameof(kind)),
        };

        var json = JsonSerializer.Serialize(source, WriteIndented);
        Assert.Contains($"\"$kind\": \"{kind}\"", json);

        var roundTripped = JsonSerializer.Deserialize<ModSource>(json);
        Assert.IsType(expectedType, roundTripped);
    }

    [Fact]
    public void NoneSource_serializes_with_no_extra_fields()
    {
        var json = JsonSerializer.Serialize<ModSource>(new NoneSource());
        // Only the discriminator; no payload fields.
        Assert.Equal("{\"$kind\":\"none\"}", json);
    }

    [Fact]
    public void NexusSource_round_trips_mod_id()
    {
        ModSource source = new NexusSource { ModId = 12345 };
        // Serialize via the base type so the $kind discriminator is emitted
        // (the polymorphism attributes live on ModSource; serializing the
        // concrete type directly omits $kind).
        var json = JsonSerializer.Serialize<ModSource>(source);
        Assert.Contains("\"$kind\":\"nexus\"", json);
        Assert.Contains("\"ModId\":12345", json);

        var roundTripped = Assert.IsType<NexusSource>(JsonSerializer.Deserialize<ModSource>(json)!);
        Assert.Equal(12345, roundTripped.ModId);
    }

    [Fact]
    public void GitHubSource_round_trips_owner_and_repo()
    {
        ModSource source = new GitHubSource { Owner = "MagosMods", Repo = "WeaponTweaks" };
        var json = JsonSerializer.Serialize<ModSource>(source);
        Assert.Contains("\"$kind\":\"github\"", json);
        Assert.Contains("\"Owner\":\"MagosMods\"", json);
        Assert.Contains("\"Repo\":\"WeaponTweaks\"", json);

        var roundTripped = Assert.IsType<GitHubSource>(JsonSerializer.Deserialize<ModSource>(json)!);
        Assert.Equal("MagosMods", roundTripped.Owner);
        Assert.Equal("WeaponTweaks", roundTripped.Repo);
    }

    [Fact]
    public void Defaults_are_empty_strings_and_zero()
    {
        // NexusSource.ModId defaults to 0; GitHubSource.Owner/Repo default to "".
        // NoneSource carries no payload. These defaults are the read-back shape
        // when fields are absent in the JSON (legacy/Phase-2 entries).
        var noneJson = "{\"$kind\":\"none\"}";
        Assert.IsType<NoneSource>(JsonSerializer.Deserialize<ModSource>(noneJson));

        var nexusJson = "{\"$kind\":\"nexus\"}";
        Assert.Equal(0, Assert.IsType<NexusSource>(JsonSerializer.Deserialize<ModSource>(nexusJson)!).ModId);

        var ghJson = "{\"$kind\":\"github\"}";
        var gh = Assert.IsType<GitHubSource>(JsonSerializer.Deserialize<ModSource>(ghJson)!);
        Assert.Equal(string.Empty, gh.Owner);
        Assert.Equal(string.Empty, gh.Repo);
    }

    [Fact]
    public void Records_use_value_equality()
    {
        // Records compare by value: important for set-comparison + identity
        // checks elsewhere (e.g. "does this entry match this source").
        Assert.Equal(new NoneSource(), new NoneSource());
        Assert.Equal(new NexusSource { ModId = 7 }, new NexusSource { ModId = 7 });
        Assert.NotEqual(new NexusSource { ModId = 7 }, new NexusSource { ModId = 8 });
        Assert.Equal(
            new GitHubSource { Owner = "a", Repo = "b" },
            new GitHubSource { Owner = "a", Repo = "b" });
        Assert.NotEqual(
            new GitHubSource { Owner = "a", Repo = "b" },
            new GitHubSource { Owner = "a", Repo = "c" });
    }
}
