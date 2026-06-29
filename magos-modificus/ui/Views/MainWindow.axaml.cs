using Avalonia.Controls;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// The main window. Its <c>DataContext</c> is set by the composition root
/// (<see cref="App.OnFrameworkInitializationCompleted"/>) to the resolved
/// <see cref="ViewModels.MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
