using System.Text.Json;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Mods.Tests;

/// <summary>
/// <see cref="ModVersionPolicy"/> shape + polymorphic JSON contract: the sum
/// type's two cases, the <c>$kind</c> discriminator values, and the
/// <see cref="PinnedPolicy.VersionId"/> foreign key. Locks the on-wire shape so
/// a future schema change is a visible, reviewable test break.
/// </summary>
public sealed class ModVersionPolicyTests
{
    // ---- shape ---------------------------------------------------------------

    [Fact]
    public void PinnedPolicy_carries_VersionId()
    {
        var pinned = new PinnedPolicy("abc123");
        Assert.Equal("abc123", pinned.VersionId);
    }

    [Fact]
    public void PinnedPolicy_default_ctor_leaves_VersionId_empty()
    {
        // The parameterless ctor is the JSON-deserialization path; it leaves
        // VersionId at the default empty string (the phantom-pin signal that
        // ProfileService.ReadProfileFile drops legacy entries on).
        var pinned = new PinnedPolicy();
        Assert.Equal(string.Empty, pinned.VersionId);
    }

    [Fact]
    public void PinnedPolicy_ctor_rejects_null_versionId()
    {
        Assert.Throws<ArgumentNullException>(() => new PinnedPolicy(null!));
    }

    [Fact]
    public void Latest_is_the_default_policy()
    {
        Assert.IsType<LatestPolicy>(ModVersionPolicy.Latest);
    }

    // ---- polymorphic JSON round-trip ----------------------------------------

    [Fact]
    public void PinnedPolicy_round_trips_with_pinned_kind_and_VersionId()
    {
        // Serialized via the base type so the $kind discriminator is emitted
        // (mirrors how a profile.json stores each entry's Policy).
        ModVersionPolicy pinned = new PinnedPolicy("deadbeefdeadbeefdeadbeefdeadbeef");

        var json = JsonSerializer.Serialize(pinned);
        var loaded = JsonSerializer.Deserialize<ModVersionPolicy>(json);

        var roundTrip = Assert.IsType<PinnedPolicy>(loaded);
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeef", roundTrip.VersionId);
        // The discriminator is the stable "pinned" identifier; the version is
        // serialized under "VersionId" (the foreign key), not "Version".
        Assert.Contains("\"$kind\":\"pinned\"", json);
        Assert.Contains("\"VersionId\":\"deadbeefdeadbeefdeadbeefdeadbeef\"", json);
        Assert.DoesNotContain("\"Version\":", json);
    }

    [Fact]
    public void LatestPolicy_round_trips_with_latest_kind()
    {
        var json = JsonSerializer.Serialize<ModVersionPolicy>(ModVersionPolicy.Latest);
        var loaded = JsonSerializer.Deserialize<ModVersionPolicy>(json);

        Assert.IsType<LatestPolicy>(loaded);
        Assert.Contains("\"$kind\":\"latest\"", json);
    }

    // ---- fresh-start tolerance: legacy pinned shape --------------------------

    [Fact]
    public void Legacy_pinned_JSON_with_Version_tag_deserializes_to_empty_VersionId()
    {
        // The pre-versionId shape carried a "Version" tag string. Under the new
        // shape that property is unrecognized + skipped (System.Text.Json's
        // default), leaving VersionId empty. ReadProfileFile uses the empty
        // VersionId as the phantom-pin signal and drops the entry.
        var legacy = """{"$kind":"pinned","Version":"1.2.3"}""";

        var loaded = JsonSerializer.Deserialize<ModVersionPolicy>(legacy);

        var pinned = Assert.IsType<PinnedPolicy>(loaded);
        Assert.Equal(string.Empty, pinned.VersionId);
    }

    [Fact]
    public void PinnedPolicy_with_VersionId_round_trips_and_resolves_on_the_container()
    {
        // End-to-end: a PinnedPolicy referencing a real version folder resolves
        // against the container it pins to (resolution is by Folder, not tag).
        var folder = Guid.NewGuid().ToString("N");
        var container = new ModContainer
        {
            Id = Guid.NewGuid(),
            Name = "DMF",
            Versions = new[]
            {
                new ModVersion { Folder = folder, VersionString = "1.2", IsLatest = true },
                new ModVersion { Folder = Guid.NewGuid().ToString("N"), VersionString = "2.0", IsLatest = false },
            },
        };

        var resolved = container.ResolveVersion(new PinnedPolicy(folder));

        Assert.NotNull(resolved);
        Assert.Equal(folder, resolved!.Folder);
        Assert.Equal("1.2", resolved.VersionString);
    }
}
