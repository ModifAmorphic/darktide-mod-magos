using Magos.Modificus.Config;
using Magos.Modificus.General;

namespace Magos.Modificus.Integrations.Tests;

/// <summary>
/// Recording <see cref="IConfigLoader"/> for tests. <see cref="Load"/> returns
/// a configurable config; <see cref="Save"/> captures the last-written config.
/// </summary>
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
