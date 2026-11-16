using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WorkTree.Core;
using WorkTree.Desktop.Explorer;
using WorkTree.Desktop.ViewModels;
using WorkTree.Git;

namespace WorkTree.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _fromExplorer;
    private int _dialogDepth;

    public MainWindow() : this(false) { }

    internal MainWindow(bool fromExplorer)
    {
        _fromExplorer = fromExplorer;
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        VersionLabel.Text = $"v{version.ToString(version.Build == 0 ? 2 : 3)}";
        DataContext = _viewModel;
        Loaded += Initialize;
        Closed += (_, _) =>
        {
            _initialized.TrySetCanceled();
            _viewModel.Dispose();
        };
    }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        Loaded -= Initialize;
        try { await _viewModel.InitializeAsync(loadSelected: !_fromExplorer); }
        finally { _initialized.TrySetResult(); }
    }

    internal async Task HandleExplorerRequestAsync(ExplorerRequest request)
    {
        try
        {
            await _initialized.Task;
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            if (_dialogDepth != 0)
            {
                MessageBox.Show(OwnedWindows.OfType<Window>().LastOrDefault(window => window.IsVisible) ?? this,
                    "请先关闭当前对话框，再从资源管理器重试。", "WorkTree", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (await _viewModel.OpenExplorerSelectionAsync(request) is { } directories)
            {
                if (_viewModel.SelectedWorktree is { } selected)
                    WorktreeGrid.ScrollIntoView(selected);
                if (request.Create)
                    await EditSparseWorktreeAsync(adjust: false, directories);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void ShowExplorerError(string message) => _viewModel.ShowExplorerError(message);

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
        _dialogDepth++;
        try
        {
            return picker.ShowDialog(this) == true
                ? _viewModel.AddOrRelocateAsync(picker.FolderName, repository)
                : Task.CompletedTask;
        }
        finally { _dialogDepth--; }
    }

    private async void RemoveRepository(object sender, RoutedEventArgs e) => await _viewModel.RemoveRepositoryAsync();

    private async void PruneWorktrees(object sender, RoutedEventArgs e) => await OpenCleanupAsync(prune: true);

    private async void CleanupSelectedWorktree(object sender, RoutedEventArgs e) =>
        await OpenCleanupAsync(prune: _viewModel.SelectedWorktree?.CanPruneRegistration == true);

    private async Task OpenCleanupAsync(bool prune)
    {
        if (_viewModel.OpenCleanupEditor(prune) is not { } window)
            return;
        if (ShowOwnedDialog(window) == true && window.Result is { } result)
            await _viewModel.AcceptCleanupResultAsync(result);
    }

    private async void CreateSparseWorktree(object sender, RoutedEventArgs e) => await EditSparseWorktreeAsync(adjust: false);

    private async void AdjustSparseWorktree(object sender, RoutedEventArgs e) => await EditSparseWorktreeAsync(adjust: true);

    private async Task EditSparseWorktreeAsync(bool adjust, IReadOnlyList<string>? directories = null)
    {
        if (_viewModel.OpenSparseEditor(adjust, directories) is not { } window)
            return;
        if (ShowOwnedDialog(window) == true && window.Result is { } result)
            await _viewModel.AcceptSparseResultAsync(result);
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        ShowOwnedDialog(new SettingsWindow(_viewModel));
    }

    private bool? ShowOwnedDialog(Window dialog)
    {
        dialog.Owner = this;
        _dialogDepth++;
        try { return dialog.ShowDialog(); }
        finally { _dialogDepth--; }
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
