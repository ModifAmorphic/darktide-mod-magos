using System.Text.Json;
using Modificus.Curator.Config;
using Modificus.Curator.General;

namespace Modificus.Curator.Mods.Tests;

/// <summary>
/// Recording <see cref="IConfigLoader"/> for tests. <see cref="Load"/> returns
/// a configurable config; <see cref="Save"/> captures the last-written config
/// and mirrors it back into <see cref="Config"/> so a subsequent
/// <see cref="Load"/> returns the saved state, exactly as the real on-disk
/// loader round-trips through its JSON file.
/// </summary>
/// <remarks>
/// <see cref="Load"/> returns a <b>deep copy</b>, matching the real loader (it
/// re-deserializes from disk each call). This isolates a caller's mutation of
/// the loaded config from the loader's stored state until <see cref="Save"/>
/// persists it.
/// </remarks>
internal sealed class FakeConfigLoader : IConfigLoader
{
    // Round-trip options for the Load() deep copy. Defaults are sufficient: the
    // persisted shape's polymorphism (ModSource) lives on ModContainer, not on
    // CuratorConfig, whose sections are plain POCOs.
    private static readonly JsonSerializerOptions CopyOptions = new();

    public CuratorConfig Config { get; set; } = CuratorConfig.CreateDefault();
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public CuratorConfig? LastSaved { get; private set; }

    public CuratorConfig Load()
    {
        LoadCalls++;
        // Deep copy: the real loader re-deserializes from disk on each Load, so
        // mutating the returned object does not leak into the stored state
        // until Save persists it.
        return JsonSerializer.Deserialize<CuratorConfig>(
            JsonSerializer.Serialize(Config, CopyOptions), CopyOptions)!;
    }

    public void Save(CuratorConfig config)
    {
        SaveCalls++;
        LastSaved = config;
        Config = config;
    }
}
