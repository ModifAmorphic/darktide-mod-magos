using Magos.Modificus.Config;

namespace Magos.Modificus.General.Tests;

/// <summary>
/// Recording <see cref="IConfigLoader"/> for tests. <see cref="Save"/> captures
/// the last-written config without touching disk. Returns a configurable config
/// from <see cref="Load"/> (defaults to a fresh <see cref="MagosConfig.CreateDefault"/>).
/// </summary>
/// <remarks>
/// Mirrors the <c>FakeConfigLoader</c> in the UI test project; each test project
/// carries its own copy so projects stay self-contained with no shared test
/// utility dependency.
/// </remarks>
internal sealed class FakeConfigLoader : IConfigLoader
{
    public MagosConfig Config { get; set; } = MagosConfig.CreateDefault();
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public MagosConfig? LastSaved { get; private set; }

    public MagosConfig Load()
    {
        LoadCalls++;
        return Config;
    }

    public void Save(MagosConfig config)
    {
        SaveCalls++;
        LastSaved = config;
    }
}
