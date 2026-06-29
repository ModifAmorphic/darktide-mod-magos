using CommunityToolkit.Mvvm.ComponentModel;
using Magos.Modificus.Config;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// Minimal view model for the Phase 0 window — exposes the loaded config so
/// the window proves config/logging/DI are wired (displays the resolved paths
/// and the log-file location).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public MainViewModel(MagosConfig config)
    {
        ConfigSummary =
            $"Profiles base folder:{Environment.NewLine}  {config.ProfilesBaseFolder}{Environment.NewLine}{Environment.NewLine}" +
            $"Shared mods folder:{Environment.NewLine}  {config.SharedModsFolder}{Environment.NewLine}{Environment.NewLine}" +
            $"Enginseer runtime dir:{Environment.NewLine}  {config.EnginseerRuntimeDir}{Environment.NewLine}{Environment.NewLine}" +
            $"Log level: {config.Logging.Level}{Environment.NewLine}" +
            $"Log file:{Environment.NewLine}  {config.Logging.LogFile}{Environment.NewLine}{Environment.NewLine}" +
            "Phase 0 scaffold — libraries are stubs; UI is minimal.";
    }

    [ObservableProperty]
    private string _configSummary = string.Empty;
}
