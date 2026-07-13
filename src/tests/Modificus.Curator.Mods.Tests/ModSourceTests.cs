using System.Text.Json;

namespace Modificus.Curator.Mods.Tests;

/// <summary>
/// <see cref="ModSource"/> JSON polymorphism: the <c>$kind</c> discriminator
/// round-trips for untracked/nexus, and the default for an absent source is
/// <see cref="UntrackedSource"/>. Mirrors the established
/// <see cref="ModVersionPolicy"/> serialization tests.
/// </summary>
public sealed class ModSourceTests
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    [Theory]
    [InlineData("untracked", typeof(UntrackedSource))]
    [InlineData("nexus", typeof(NexusSource))]
    public void Kind_discriminator_round_trips(string kind, Type expectedType)
    {
        // The serialized discriminator is the stable lowercase identifier.
        var source = kind switch
        {
            "untracked" => (ModSource)new UntrackedSource(),
            "nexus" => new NexusSource { ModId = 4242 },
            _ => throw new ArgumentException($"unknown kind: {kind}", nameof(kind)),
        };

        var json = JsonSerializer.Serialize(source, WriteIndented);
        Assert.Contains($"\"$kind\": \"{kind}\"", json);

        var roundTripped = JsonSerializer.Deserialize<ModSource>(json);
        Assert.IsType(expectedType, roundTripped);
    }

    [Fact]
    public void UntrackedSource_serializes_with_no_extra_fields()
    {
        var json = JsonSerializer.Serialize<ModSource>(new UntrackedSource());
        // Only the discriminator; no payload fields.
        Assert.Equal("{\"$kind\":\"untracked\"}", json);
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
    public void Defaults_are_empty_strings_and_zero()
    {
        // NexusSource.ModId defaults to 0; UntrackedSource carries no payload.
        // These defaults are the read-back shape when fields are absent in the
        // JSON (legacy entries).
        var untrackedJson = "{\"$kind\":\"untracked\"}";
        Assert.IsType<UntrackedSource>(JsonSerializer.Deserialize<ModSource>(untrackedJson));

        var nexusJson = "{\"$kind\":\"nexus\"}";
        Assert.Equal(0, Assert.IsType<NexusSource>(JsonSerializer.Deserialize<ModSource>(nexusJson)!).ModId);
    }

    [Fact]
    public void Records_use_value_equality()
    {
        // Records compare by value: important for set-comparison + identity
        // checks elsewhere (e.g. "does this entry match this source").
        Assert.Equal(new UntrackedSource(), new UntrackedSource());
        Assert.Equal(new NexusSource { ModId = 7 }, new NexusSource { ModId = 7 });
        Assert.NotEqual(new NexusSource { ModId = 7 }, new NexusSource { ModId = 8 });
    }

    [Fact]
    public void Only_untracked_and_nexus_discriminators_are_known_kinds()
    {
        // The source hierarchy has exactly two known kinds. An unknown
        // discriminator fails to deserialize with a JsonException, so a
        // container whose manifest carries an unrecognized source kind is
        // rejected rather than misread.
        var json = "{\"$kind\":\"unsupported\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModSource>(json));
    }
}
