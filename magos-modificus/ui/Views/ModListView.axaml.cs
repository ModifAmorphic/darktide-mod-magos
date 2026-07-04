using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Magos.Modificus.SharedMods;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// The mod-list content area (a <see cref="UserControl"/>). Its
/// <c>DataContext</c> is a <see cref="ModListViewModel"/> (bound from the shell as
/// <c>{Binding ModList}</c>). Owns the add entry points (the Add split button's
/// zip file picker + folder picker + the content-area drag-and-drop target) and
/// routes every per-row interaction (toggle / move / policy / remove) through
/// code-behind handlers calling the parent VM's commands with the row as the
/// parameter (the established <c>ManageProfilesWindow</c> pattern). All state +
/// service calls stay in the (unit-tested) VM; this is pure view mechanics.
/// </summary>
/// <remarks>
/// <para><b>Add split button:</b> the primary click opens the current mode's
/// picker (zip file picker by default; folder picker after the folder flyout item
/// is chosen). The flyout's two items switch the mode (one-click import) and the
/// VM's <see cref="ModListViewModel.AddMode"/> is mirrored so the split button's
/// label reflects it. Folders get a picker path because drag-and-drop is a
/// Windows-only feature in Avalonia 12.0.x.</para>
/// <para><b>Drag-and-drop:</b> the content area has
/// <c>DragDrop.AllowDrop="True"</c> + <c>Drop</c>/<c>DragOver</c> handlers. The
/// drop reads the files (folders AND <c>.zip</c>, multi) via the sync
/// <c>TryGetFiles</c> extension on <see cref="DragEventArgs.DataTransfer"/> (an
/// <c>IDataTransfer</c> in Avalonia 12.x, so the async variant is unavailable
/// here), maps each to its local path, and forwards the list to the VM's add
/// command. <c>DragOver</c> advertises the Copy effect only when files are
/// present. Drag-and-drop is Windows-only in 12.0.x; the folder picker covers
/// the cross-platform folder case.</para>
/// <para><b>Policy ComboBox guard:</b> <see cref="Policy_Changed"/> skips when the
/// selection already agrees with the row's effective policy, so the binding-init
/// (and post-Reload) <c>SelectionChanged</c> fires do not re-apply + loop. Only a
/// genuine divergence routes to the parent's policy command.</para>
/// </remarks>
public partial class ModListView : UserControl
{
    public ModListView()
    {
        InitializeComponent();
    }

    private ModListViewModel? ViewModel => DataContext as ModListViewModel;

    /// <summary>
    /// The Add split button's current mode (which picker the primary click
    /// opens). Defaults to <see cref="ModAddMode.Zip"/>. Kept in sync with the
    /// VM's <see cref="ModListViewModel.AddMode"/> (via <see cref="SetAddMode"/>)
    /// so the split button's label tracks the selected mode.
    /// </summary>
    private ModAddMode _addMode = ModAddMode.Zip;

    // ---- add: split button (zip + folder pickers) --------------------------

    /// <summary>
    /// The Add split button's primary click: opens the picker for the current
    /// mode (zip file picker by default; folder picker after the folder flyout
    /// item is chosen). A native picker cannot mix files + folders, so the two
    /// flyout items switch the mode for one-click import.
    /// </summary>
    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (_addMode == ModAddMode.Folder)
        {
            await OpenFolderPickerAsync();
        }
        else
        {
            await OpenZipPickerAsync();
        }
    }

    /// <summary>
    /// The "Add Mod (zip)" flyout item: switches the mode to zip (so subsequent
    /// primary clicks open the zip picker) and opens the zip picker immediately
    /// (one-click import).
    /// </summary>
    private async void AddZip_Click(object? sender, RoutedEventArgs e)
    {
        SetAddMode(ModAddMode.Zip);
        await OpenZipPickerAsync();
    }

    /// <summary>
    /// The "Add Mod (folder)" flyout item: switches the mode to folder (so
    /// subsequent primary clicks open the folder picker) and opens the folder
    /// picker immediately (one-click import). Folders get a picker path because
    /// drag-and-drop is a Windows-only feature in Avalonia 12.0.x.
    /// </summary>
    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        SetAddMode(ModAddMode.Folder);
        await OpenFolderPickerAsync();
    }

    /// <summary>
    /// Sets the current add mode + mirrors it on the VM (so the split button's
    /// <c>AddModeLabel</c> binding refreshes). Centralized so the field + the VM
    /// property never drift apart.
    /// </summary>
    private void SetAddMode(ModAddMode mode)
    {
        _addMode = mode;
        if (ViewModel is { } vm)
        {
            vm.AddMode = mode;
        }
    }

    /// <summary>
    /// Opens a multi-select <c>.zip</c> file picker and forwards the selected
    /// paths to the VM's add command.
    /// </summary>
    private async Task OpenZipPickerAsync()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var zip = new FilePickerFileType("Zip archive")
        {
            Patterns = new[] { "*.zip" },
        };
        var options = new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = new[] { zip },
        };

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        if (result is null || result.Count == 0)
        {
            return;
        }

        var paths = result.Select(f => f.Path.LocalPath).ToArray();
        if (paths.Length > 0)
        {
            await vm.AddModsCommand.ExecuteAsync(paths);
        }
    }

    /// <summary>
    /// Opens a multi-select folder picker and forwards the selected folder paths
    /// to the VM's add command. Folders are covered by drag-and-drop only on
    /// Windows in Avalonia 12.0.x, so this picker is the cross-platform path for
    /// folder import.
    /// </summary>
    private async Task OpenFolderPickerAsync()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var options = new FolderPickerOpenOptions
        {
            AllowMultiple = true,
        };

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (result is null || result.Count == 0)
        {
            return;
        }

        var paths = result.Select(f => f.Path.LocalPath).ToArray();
        if (paths.Length > 0)
        {
            await vm.AddModsCommand.ExecuteAsync(paths);
        }
    }

    // ---- add: drag-and-drop ------------------------------------------------

    /// <summary>
    /// Advertises the Copy effect when the dragged payload carries files (folders
    /// or <c>.zip</c>); otherwise None, so non-file drops are not accepted.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Gate on the actual file retrieval (the same call OnDrop uses), not on
        // Contains(DataFormat.File): that format-name check is unreliable for
        // external file-manager drags on X11/Wayland and silently rejects drops on
        // Linux. TryGetFiles is consistent with OnDrop + grants Copy only when files
        // are genuinely present.
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>
    /// Collects the dropped files' local paths (folders or <c>.zip</c>, multi) via
    /// the sync <c>TryGetFiles</c> extension on <see cref="DragEventArgs.DataTransfer"/>
    /// and forwards them to the VM's add command.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0)
        {
            return;
        }

        var paths = files.Select(f => f.Path.LocalPath).ToArray();
        e.Handled = true;

        if (paths.Length > 0 && ViewModel is { } vm)
        {
            await vm.AddModsCommand.ExecuteAsync(paths);
        }
    }

    // ---- per-row interactions ----------------------------------------------

    /// <summary>
    /// Applies a row's enabled toggle. The CheckBox two-way bound
    /// <see cref="ModItemViewModel.Enabled"/> already flipped; this persists it via
    /// the parent's <c>ToggleEnabledCommand</c>.
    /// </summary>
    private void Enabled_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ModItemViewModel row)
        {
            ViewModel?.ToggleEnabledCommand.Execute(row);
        }
    }

    /// <summary>
    /// Routes a policy-ComboBox change to the parent's Latest / Pinned command.
    /// Skips when the selection already agrees with the row's effective policy, so
    /// binding-init + post-Reload <c>SelectionChanged</c> fires (which would
    /// otherwise re-apply + reload infinitely) are harmless. Only a genuine
    /// divergence proceeds.
    /// </summary>
    private void Policy_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.DataContext is not ModItemViewModel row)
        {
            return;
        }

        // Skip the init / programmatic fire: when the ComboBox's selection matches
        // the row's effective policy there is nothing to apply. Reloads recreate
        // rows + re-init their ComboBoxes; without this guard each would re-apply.
        var wantsPinned = cb.SelectedIndex == ModItemViewModel.PolicyPinned;
        var isPinned = row.Policy is PinnedPolicy;
        if (wantsPinned == isPinned)
        {
            return;
        }

        if (wantsPinned)
        {
            ViewModel?.SetPolicyPinnedCommand.Execute(row);
        }
        else
        {
            ViewModel?.SetPolicyLatestCommand.Execute(row);
        }
    }

    /// <summary>
    /// Applies the inline pinned-version edit on Enter (commits the row's
    /// <see cref="ModItemViewModel.PinnedVersion"/> as a new Pinned policy).
    /// </summary>
    private void PinnedVersion_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (sender is TextBox box && box.DataContext is ModItemViewModel row)
        {
            ViewModel?.SetPolicyPinnedCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            ViewModel?.MoveUpCommand.Execute(row);
        }
    }

    private void MoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            ViewModel?.MoveDownCommand.Execute(row);
        }
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ModItemViewModel row)
        {
            // AsyncRelayCommand.Execute forwards to ExecuteAsync.
            ViewModel?.RemoveCommand.Execute(row);
        }
    }

    /// <summary>
    /// Applies the auto-sort resolver once on toggle. The command is a no-op when
    /// there is no active profile or no mods (and the identity resolver makes it a
    /// no-op regardless); <see cref="ModListViewModel.AutoSortEnabled"/> tracks the
    /// toggle state for display.
    /// </summary>
    private void AutoSort_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AutoSortCommand.Execute(null);
    }
}
