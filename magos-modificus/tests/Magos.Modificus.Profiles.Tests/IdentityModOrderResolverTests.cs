using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.SharedMods;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// <see cref="IdentityModOrderResolver"/>: the auto-sort identity stub. Returns
/// the mod names in their current <see cref="ModListEntry.Order"/>; stable on
/// ties. Also covers the <see cref="AddProfiles"/> DI registration of
/// <see cref="IModOrderResolver"/>.
/// </summary>
public sealed class IdentityModOrderResolverTests
{
    [Fact]
    public void ResolveOrder_returns_names_in_current_Order_ascending()
    {
        var resolver = new IdentityModOrderResolver();
        var mods = new[]
        {
            new ModListEntry { Name = "Gamma", Order = 2, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { Name = "Alpha", Order = 0, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { Name = "Beta",  Order = 1, Enabled = false, Policy = ModVersionPolicy.Latest },
        };

        var order = resolver.ResolveOrder(mods);

        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, order);
    }

    [Fact]
    public void ResolveOrder_is_identity_when_already_ordered()
    {
        var resolver = new IdentityModOrderResolver();
        var mods = new[]
        {
            new ModListEntry { Name = "DMF",   Order = 0, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { Name = "ModB",  Order = 1, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { Name = "ModC",  Order = 2, Enabled = true, Policy = ModVersionPolicy.Latest },
        };

        var order = resolver.ResolveOrder(mods);

        // No-op: names come back in the same order they're already in.
        Assert.Equal(new[] { "DMF", "ModB", "ModC" }, order);
    }

    [Fact]
    public void ResolveOrder_returns_empty_for_empty_input()
    {
        var resolver = new IdentityModOrderResolver();
        Assert.Empty(resolver.ResolveOrder(Array.Empty<ModListEntry>()));
    }

    [Fact]
    public void ResolveOrder_preserves_input_relative_order_on_ties()
    {
        // Equal Order values are stable-sorted (OrderBy is stable), so the input's
        // relative order is preserved on ties. This is the identity behavior the
        // auto-sort toggle relies on (a no-op should not reshuffle the list).
        var resolver = new IdentityModOrderResolver();
        var mods = new[]
        {
            new ModListEntry { Name = "First",  Order = 5, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { Name = "Second", Order = 5, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { Name = "Third",  Order = 5, Enabled = true, Policy = ModVersionPolicy.Latest },
        };

        var order = resolver.ResolveOrder(mods);

        Assert.Equal(new[] { "First", "Second", "Third" }, order);
    }

    [Fact]
    public void ResolveOrder_rejects_null_input()
    {
        var resolver = new IdentityModOrderResolver();
        Assert.Throws<ArgumentNullException>(() => resolver.ResolveOrder(null!));
    }

    [Fact]
    public void ResolveOrder_returns_a_separate_list_not_the_input()
    {
        // The result must be its own collection (caller may persist it; mutation
        // of the result must not affect the input list).
        var resolver = new IdentityModOrderResolver();
        var mods = new[]
        {
            new ModListEntry { Name = "DMF", Order = 0, Enabled = true, Policy = ModVersionPolicy.Latest },
        };

        var order = resolver.ResolveOrder(mods);
        Assert.Single(order);
        Assert.NotSame(mods, order);
    }

    // ---- DI registration ---------------------------------------------------

    [Fact]
    public void AddProfiles_registers_IModOrderResolver_as_IdentityModOrderResolver()
    {
        var config = MagosConfig.CreateDefault();
        using var provider = new ServiceCollection()
            .AddSingleton(config)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddProfiles()
            .BuildServiceProvider();

        var resolved = provider.GetRequiredService<IModOrderResolver>();
        Assert.IsType<IdentityModOrderResolver>(resolved);
    }

    [Fact]
    public void AddProfiles_Allows_pre_registered_resolver_override()
    {
        // TryAdd: a caller (tests, or the future real algorithm) may pre-register
        // an IModOrderResolver and have it survive AddProfiles.
        var fake = new FakeOrderResolver();
        var config = MagosConfig.CreateDefault();
        using var provider = new ServiceCollection()
            .AddSingleton(config)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<IModOrderResolver>(fake)
            .AddProfiles()
            .BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<IModOrderResolver>());
    }

    /// <summary>Hand-rolled fake (no mock library, matching the test style).</summary>
    private sealed class FakeOrderResolver : IModOrderResolver
    {
        public IReadOnlyList<string> ResolveOrder(IReadOnlyList<ModListEntry> mods) =>
            mods.Select(m => m.Name).Reverse().ToArray();
    }
}
