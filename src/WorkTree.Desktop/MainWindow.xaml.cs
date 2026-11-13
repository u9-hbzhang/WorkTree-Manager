using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WorkTree.Core;
using WorkTree.Desktop.ViewModels;

namespace WorkTree.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        VersionLabel.Text = $"v{version.ToString(version.Build == 0 ? 2 : 3)}";
        DataContext = _viewModel;
        Loaded += Initialize;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        Loaded -= Initialize;
        await _viewModel.InitializeAsync();
    }

    private async void AddRepository(object sender, RoutedEventArgs e) => await SelectFolderAsync(null);

    private void OpenRepositoryMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private async void RelocateRepository(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRepository is { } repository)
            await SelectFolderAsync(repository);
    }

    private Task SelectFolderAsync(RepositoryLocation? repository)
    {
        var picker = new OpenFolderDialog { Title = repository is null ? "选择 Git 仓库或工作区" : "重新定位仓库" };
        if (repository is not null && Directory.Exists(repository.AccessPath))
            picker.InitialDirectory = repository.AccessPath;
        return picker.ShowDialog(this) == true
            ? _viewModel.AddOrRelocateAsync(picker.FolderName, repository)
            : Task.CompletedTask;
    }

    private async void RemoveRepository(object sender, RoutedEventArgs e) => await _viewModel.RemoveRepositoryAsync();

    private async void PruneWorktrees(object sender, RoutedEventArgs e) => await OpenCleanupAsync(prune: true);

    private async void DeleteWorktree(object sender, RoutedEventArgs e) => await OpenCleanupAsync(prune: false);

    private async Task OpenCleanupAsync(bool prune)
    {
        if (_viewModel.OpenCleanupEditor(prune) is not { } window)
            return;
        window.Owner = this;
        if (window.ShowDialog() == true && window.Result is { } result)
            await _viewModel.AcceptCleanupResultAsync(result);
    }

    private async void CreateSparseWorktree(object sender, RoutedEventArgs e) => await EditSparseWorktreeAsync(adjust: false);

    private async void AdjustSparseWorktree(object sender, RoutedEventArgs e) => await EditSparseWorktreeAsync(adjust: true);

    private async Task EditSparseWorktreeAsync(bool adjust)
    {
        if (_viewModel.OpenSparseEditor(adjust) is not { } window)
            return;
        window.Owner = this;
        if (window.ShowDialog() == true && window.Result is { } result)
            await _viewModel.AcceptSparseResultAsync(result);
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_viewModel)
        {
            Owner = this
        };
        settings.ShowDialog();
    }

    private void OpenExternalTool(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ExternalTool tool })
            _viewModel.OpenExternal(tool);
    }

    private void OpenWorktreeRow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is DataGridRow { Item: WorktreeRow row })
        {
            _viewModel.SelectedWorktree = row;
            if (_viewModel.OpenFolderCommand.CanExecute(null))
                _viewModel.OpenFolderCommand.Execute(null);
        }
    }

    private void FocusSearch(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
