using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.Mods;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// <see cref="IdentityModOrderResolver"/>: the auto-sort identity stub. Returns
/// the container ids in their current <see cref="ModListEntry.Order"/>; stable
/// on ties. Also covers the <see cref="AddProfiles"/> DI registration of
/// <see cref="IModOrderResolver"/>.
/// </summary>
public sealed class IdentityModOrderResolverTests
{
    [Fact]
    public void ResolveOrder_returns_container_ids_in_current_Order_ascending()
    {
        var resolver = new IdentityModOrderResolver();
        var mods = new[]
        {
            new ModListEntry { ContainerId = Guid.Parse("00000000-0000-0000-0000-0000000000c0"), Order = 2, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = Guid.Parse("00000000-0000-0000-0000-0000000000a0"), Order = 0, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = Guid.Parse("00000000-0000-0000-0000-0000000000b0"), Order = 1, Enabled = false, Policy = ModVersionPolicy.Latest },
        };

        var order = resolver.ResolveOrder(mods);

        Assert.Equal(
            new[]
            {
                Guid.Parse("00000000-0000-0000-0000-0000000000a0"),
                Guid.Parse("00000000-0000-0000-0000-0000000000b0"),
                Guid.Parse("00000000-0000-0000-0000-0000000000c0"),
            },
            order);
    }

    [Fact]
    public void ResolveOrder_is_identity_when_already_ordered()
    {
        var resolver = new IdentityModOrderResolver();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var mods = new[]
        {
            new ModListEntry { ContainerId = id1, Order = 0, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = id2, Order = 1, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = id3, Order = 2, Enabled = true, Policy = ModVersionPolicy.Latest },
        };

        var order = resolver.ResolveOrder(mods);

        // No-op: ids come back in the same order they're already in.
        Assert.Equal(new[] { id1, id2, id3 }, order);
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
        // relative order is preserved on ties.
        var resolver = new IdentityModOrderResolver();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var mods = new[]
        {
            new ModListEntry { ContainerId = first, Order = 5, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = second, Order = 5, Enabled = true, Policy = ModVersionPolicy.Latest },
            new ModListEntry { ContainerId = third, Order = 5, Enabled = true, Policy = ModVersionPolicy.Latest },
        };

        var order = resolver.ResolveOrder(mods);

        Assert.Equal(new[] { first, second, third }, order);
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
        var resolver = new IdentityModOrderResolver();
        var id = Guid.NewGuid();
        var mods = new[]
        {
            new ModListEntry { ContainerId = id, Order = 0, Enabled = true, Policy = ModVersionPolicy.Latest },
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
        public IReadOnlyList<Guid> ResolveOrder(IReadOnlyList<ModListEntry> mods) =>
            mods.Select(m => m.ContainerId).Reverse().ToArray();
    }
}
