using System.Text.Json;
using Magos.Modificus.Config;
using Magos.Modificus.General;

namespace Magos.Modificus.Mods.Tests;

/// <summary>
/// Recording <see cref="IConfigLoader"/> for tests. <see cref="Load"/> returns
/// a configurable config; <see cref="Save"/> captures the last-written config
/// and (by default) mirrors it back into <see cref="Config"/> so a subsequent
/// <see cref="Load"/> returns the saved state, exactly as the real on-disk
/// loader round-trips through its JSON file. Two knobs drive the relocate
/// rollback tests: <see cref="SaveException"/> (a thrown failure) and
/// <see cref="PersistOnSave"/> (a silent failure that writes nothing).
/// </summary>
/// <remarks>
/// <see cref="Load"/> returns a <b>deep copy</b>, matching the real loader (it
/// re-deserializes from disk each call). This is load-bearing for the atomic
/// <see cref="IModRepository.Relocate"/> flow: Relocate mutates the loaded
/// config (<c>ModsFolder = newPath</c>) before calling <see cref="Save"/>, so
/// if Load returned the stored instance by reference, the mutation would leak
/// into the loader's state even when Save failed. The copy isolates the
/// mutation: only a successful <see cref="Save"/> persists it.
/// </remarks>
internal sealed class FakeConfigLoader : IConfigLoader
{
    // Round-trip options for the Load() deep copy. Defaults are sufficient: the
    // persisted shape's polymorphism (ModSource) lives on ModContainer, not on
    // MagosConfig, whose sections are plain POCOs.
    private static readonly JsonSerializerOptions CopyOptions = new();

    public MagosConfig Config { get; set; } = MagosConfig.CreateDefault();
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public MagosConfig? LastSaved { get; private set; }

    /// <summary>
    /// When set, <see cref="Save"/> throws this instead of persisting. Simulates
    /// a loader that reports a write failure by exception (used by the relocate
    /// rollback test).
    /// </summary>
    public Exception? SaveException { get; set; }

    /// <summary>
    /// Whether <see cref="Save"/> mirrors the written config back into
    /// <see cref="Config"/> (so a following <see cref="Load"/> returns it).
    /// Default <c>true</c> (mirrors the real loader). Set <c>false</c> to
    /// simulate a silent save failure: Save returns without persisting, so the
    /// next Load still returns the prior state (used by the relocate
    /// silent-failure rollback test).
    /// </summary>
    public bool PersistOnSave { get; set; } = true;

    public MagosConfig Load()
    {
        LoadCalls++;
        // Deep copy: the real loader re-deserializes from disk on each Load, so
        // mutating the returned object does not leak into the stored state
        // until Save persists it.
        return JsonSerializer.Deserialize<MagosConfig>(
            JsonSerializer.Serialize(Config, CopyOptions), CopyOptions)!;
    }

    public void Save(MagosConfig config)
    {
        SaveCalls++;
        if (SaveException is { } ex)
        {
            throw ex;
        }
        LastSaved = config;
        if (PersistOnSave)
        {
            Config = config;
        }
    }
}
