using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkTree.Core;
using WorkTree.Desktop.ViewModels;
using WorkTree.Git;

namespace WorkTree.Desktop;

public partial class SparseWorktreeWindow : Window
{
    private readonly RepositoryLocation _repository;
    private readonly WorktreeEntry? _target;
    private readonly WorktreeEntry? _startingTree;
    private readonly SparseWorktreeService _service;
    private readonly WorktreeEntry[] _entries;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, string[]> _directoryCache = new(StringComparer.Ordinal);
    private BranchChoice[] _branches = [];
    private StartChoice[] _sources = [];
    private StartChoice? _source;
    private DirectoryPicker? _picker;
    private ConeScope? _originalScope;
    private string? _commit;
    private string _defaultBranch = "";
    private string _defaultDestination = "";
    private bool _ready;
    private bool _working;
    private bool _writing;
    private bool _closed;

    internal SparseWorktreeWindow(RepositoryLocation repository, GitInstallation git, WorktreeEntry? selected,
        WorktreeEntry? target, IEnumerable<WorktreeEntry> entries, IEnumerable<WorktreeEntry> reusable)
    {
        _repository = repository;
        _target = target;
        _startingTree = selected is { IsBare: false, IsPrunable: false } ? selected : null;
        _entries = entries.ToArray();
        _service = new SparseWorktreeService(git);
        InitializeComponent();
        Heading.Text = Title = target is null ? "创建 cone 稀疏工作区" : "调整稀疏范围";
        RepoText.Text = target?.Path ?? repository.MainPath;
        CreateFields.Visibility = target is null ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Content = target is null ? "创建工作区" : "预览调整";
        var reuse = reusable.Where(entry => !entry.IsBare && !entry.IsPrunable &&
                !string.Equals(entry.Path, target?.Path, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new ReuseChoice(entry)).ToArray();
        ReuseSources.ItemsSource = reuse;
        ReuseSources.IsEnabled = reuse.Length > 0;
        ReuseButton.IsEnabled = reuse.Length > 0;
        ReuseSources.SelectedIndex = reuse.Length > 0 ? 0 : -1;
        Loaded += Initialize;
        Closing += (_, e) => e.Cancel = _writing;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            if (!_working)
                _lifetime.Dispose();
        };
    }

    internal SparseWorktreeResult? Result { get; private set; }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        Loaded -= Initialize;
        await RunAsync("正在读取 Git 目录…", async token =>
        {
            if (_target is null)
            {
                _branches = await _service.ListBranchesAsync(_repository, token);
                UpdateSources();
                _ready = true;
                SuggestDefaults();
                await LoadDirectoriesAsync(token);
            }
            else
            {
                _originalScope = await _service.ReadScopeAsync(_repository, _target, token);
                _ready = true;
                await LoadDirectoriesAsync(token);
            }
        });
    }

    private void SuggestDefaults()
    {
        var branches = _branches.Where(branch => branch.IsLocal).Select(branch => branch.Reference)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = _entries.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; ; index++)
        {
            var name = $"worktree-{index}";
            var destination = DefaultDestination(name);
            if (branches.Contains("refs/heads/codex/" + name) || paths.Contains(destination) ||
                Path.Exists(destination))
                continue;
            NameBox.Text = name;
            return;
        }
    }

    private string DefaultDestination(string name) =>
        Path.Combine(Path.GetDirectoryName(_repository.MainPath) ?? _repository.MainPath, "worktrees", name);

    private void NameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;
        var name = NameBox.Text.Trim();
        var newBranch = name.Length == 0 ? "" : "codex/" + name;
        var newDestination = SparseWorktreeService.IsValidWindowsName(name)
            ? DefaultDestination(name) : "";
        if (BranchBox.Text == _defaultBranch)
            BranchBox.Text = newBranch;
        if (DestinationBox.Text == _defaultDestination)
            DestinationBox.Text = newDestination;
        _defaultBranch = newBranch;
        _defaultDestination = newDestination;
    }

    private async void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _working)
            return;
        _ready = false;
        UpdateSources();
        _ready = true;
        await RunAsync("正在读取分支的目录…", LoadDirectoriesAsync);
    }

    private void UpdateSources()
    {
        var existing = ExistingBranch.IsChecked == true;
        BranchBox.IsEnabled = !existing;
        SourceLabel.Text = existing ? "已有本地分支" : "起始版本";
        var sources = new List<StartChoice>();
        if (!existing)
        {
            sources.Add(new StartChoice(_startingTree is null ? "主工作区当前提交" : "当前工作区 · " +
                (_startingTree.Branch ?? Path.GetFileName(_startingTree.Path)), null, false));
            sources.Add(new StartChoice("指定提交或 Git 引用…", null, true));
        }

        sources.AddRange(_branches.Where(branch => existing ? branch.IsLocal
                : !branch.Reference.StartsWith("refs/remotes/origin/", StringComparison.Ordinal))
            .Select(branch => new StartChoice(branch.Label, branch, false)));
        _sources = sources.ToArray();
        _source = existing ? _sources.FirstOrDefault(source => source.Branch?.OccupiedPath is null) ?? _sources.FirstOrDefault()
            : _sources.FirstOrDefault();
        SourceButton.Content = _source?.Label ?? "没有可用的本地分支";
        CommitInput.Visibility = Visibility.Collapsed;
    }

    private void OpenSources(object sender, RoutedEventArgs e)
    {
        if (_ready && !_working)
            SourcePopup.IsOpen = true;
    }

    private void SourcesOpened(object sender, EventArgs e)
    {
        SourceSearch.Clear();
        FilterSources();
        SourceSearch.Focus();
    }

    private void SourceSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SourceResults is not null)
            FilterSources();
    }

    private void FilterSources()
    {
        var query = SourceSearch.Text.Trim();
        const int limit = 250;
        var results = _sources.Select(source => (Source: source, Rank: MatchRank(source.Label, query)))
            .Where(item => item.Rank >= 0).OrderBy(item => item.Rank).Take(limit + 1).ToArray();
        SourceResults.ItemsSource = results.Take(limit).Select(item => item.Source).ToArray();
        SourceResults.SelectedItem = query.Length == 0 && results.Any(item => item.Source == _source)
            ? _source : results.FirstOrDefault().Source;
        SourceSearchHint.Text = results.Length == 0 ? "没有匹配的版本。"
            : results.Length > limit ? "仅显示前 250 项，可输入更多字符缩小范围。" : "";
        SourceSearchHint.Visibility = SourceSearchHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static int MatchRank(string label, string query)
    {
        if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (label.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        var index = 0;
        foreach (var character in label)
        {
            if (char.ToUpperInvariant(character) == char.ToUpperInvariant(query[index]) && ++index == query.Length)
                return 2;
        }
        return -1;
    }

    private async void ChooseSourceWithMouse(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(SourceResults, element) is ListBoxItem { DataContext: StartChoice source })
            await ChooseSourceAsync(source);
    }

    private async void SourcePopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SourcePopup.IsOpen = false;
            SourceButton.Focus();
        }
        else if (e.Key == Key.Enter && SourceResults.SelectedItem is StartChoice source)
        {
            e.Handled = true;
            await ChooseSourceAsync(source);
            return;
        }
        else if (e.Key is Key.Down or Key.Up && SourceResults.HasItems)
        {
            var offset = e.Key == Key.Down ? 1 : -1;
            SourceResults.SelectedIndex = Math.Clamp(SourceResults.SelectedIndex + offset, 0, SourceResults.Items.Count - 1);
            SourceResults.ScrollIntoView(SourceResults.SelectedItem);
        }
        else
        {
            return;
        }
        e.Handled = true;
    }

    private async Task ChooseSourceAsync(StartChoice source)
    {
        SourcePopup.IsOpen = false;
        SourceButton.Focus();
        if (!_ready || _working || _source == source)
            return;
        _source = source;
        SourceButton.Content = source.Label;
        await RunAsync("正在读取所选版本的目录…", LoadDirectoriesAsync);
    }

    private async void ReadCommit(object sender, RoutedEventArgs e) =>
        await RunAsync("正在读取所选版本的目录…", LoadDirectoriesAsync);

    private void CommitChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && !_working && _source is { Custom: true })
        {
            _commit = null;
            CommitText.Text = "读取提交后可应用已选目录。";
            UpdateSelection();
        }
    }

    private async Task LoadDirectoriesAsync(CancellationToken token)
    {
        _commit = null;
        CommitText.Text = "";
        var source = _source;
        CommitInput.Visibility = source?.Custom == true ? Visibility.Visible : Visibility.Collapsed;
        string path;
        string revision;
        if (_target is not null)
        {
            path = _target.Path;
            revision = "HEAD";
        }
        else if (source is null)
        {
            SetPicker(new DirectoryPicker([]), []);
            Feedback.Content = "没有可用的本地分支。";
            return;
        }
        else if (source.Custom)
        {
            path = _repository.MainPath;
            revision = CommitBox.Text.Trim();
            if (revision.Length == 0)
            {
                Feedback.Content = "请输入提交 ID 或 Git 引用，再点击「读取目录」。";
                return;
            }
        }
        else if (source.Branch is { } branch)
        {
            path = _repository.MainPath;
            revision = branch.Reference;
            if (ExistingBranch.IsChecked == true && branch.OccupiedPath is not null)
            {
                Feedback.Content = $"分支已在 {branch.OccupiedPath} 检出，请选择其他分支。";
                return;
            }
        }
        else
        {
            path = _startingTree?.Path ?? _repository.MainPath;
            revision = "HEAD";
        }

        var commit = await _service.ResolveCommitAsync(path, revision, token);
        if (!_directoryCache.TryGetValue(commit, out var paths))
        {
            paths = await _service.ListDirectoriesAsync(_repository.MainPath, commit, token);
            _directoryCache[commit] = paths;
        }
        token.ThrowIfCancellationRequested();
        var selected = _picker?.Selected ?? _originalScope?.Directories ?? [];
        var picker = await Task.Run(() => new DirectoryPicker(paths), token);
        token.ThrowIfCancellationRequested();
        SetPicker(picker, selected);
        _commit = commit;
        CommitText.Text = $"{commit[..12]} · {paths.Length} 个目录";
    }

    private void SetPicker(DirectoryPicker picker, IEnumerable<string> selected)
    {
        if (_picker is not null)
            _picker.SelectionChanged -= SelectionChanged;
        _picker = picker;
        _picker.SelectionChanged += SelectionChanged;
        var missing = _picker.SelectDirectories(selected);
        DirectoryTree.ItemsSource = _picker.Roots;
        RenderSearch();
        if (missing.Length > 0)
            Feedback.Content = $"当前提交缺少 {missing.Length} 个目录，已跳过：{string.Join("、", missing)}";
    }

    private async void ReuseScope(object sender, RoutedEventArgs e)
    {
        if (_commit is null || _picker is null || ReuseSources.SelectedItem is not ReuseChoice source)
            return;
        await RunAsync("正在读取来源工作区的 cone 范围…", async token =>
        {
            var scope = await _service.ReadScopeAsync(_repository, source.Entry, token);
            token.ThrowIfCancellationRequested();
            var missing = _picker.SelectDirectories(scope.Directories);
            Feedback.Content = missing.Length == 0 ? $"已复用 {scope.Directories.Count} 个目录。"
                : $"已复用可用目录。当前提交缺少：{string.Join("、", missing)}";
        });
    }

    private void SelectionChanged(object? sender, EventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var selected = _picker?.Selected ?? [];
        SelectionHeading.Text = $"已选目录（{selected.Length}）";
        SelectionList.ItemsSource = selected;
        SubmitButton.IsEnabled = !_working && _commit is not null && (_target is null ||
            _originalScope is not null && !selected.SequenceEqual(_originalScope.Directories));
        ReuseButton.IsEnabled = _commit is not null && ReuseSources.HasItems;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            RenderSearch();
    }

    private void RenderSearch()
    {
        if (_picker is null)
            return;
        const int limit = 500;
        var query = DirectorySearch.Text.Trim();
        DirectoryTree.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var matches = query.Length == 0 ? [] : _picker.Search(query, limit + 1);
        SearchResults.ItemsSource = matches.Take(limit).ToArray();
        SearchHint.Text = query.Length == 0 ? "" : matches.Length > limit ? "仅显示前 500 项，请缩小搜索范围。"
            : matches.Length == 0 ? "没有匹配的目录。" : $"{matches.Length} 个匹配目录";
    }

    private void ExpandNode(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: DirectoryPicker.DirectoryNode node })
            node.Expand();
    }

    private async void Submit(object sender, RoutedEventArgs e)
    {
        if (_picker is null || _commit is not { } commit || _working)
            return;

        var scope = _picker.Scope;
        var progress = new Progress<string>(message => { if (!_closed) Feedback.Content = message; });
        if (_target is null)
        {
            var name = NameBox.Text.Trim();
            if (!SparseWorktreeService.IsValidWindowsName(name))
            {
                Feedback.Tag = "Error";
                Feedback.Content = "请输入有效的 Windows 文件夹名称。";
                return;
            }

            var existing = ExistingBranch.IsChecked == true;
            var branch = existing && _source is { Branch: { } source }
                ? source.Reference["refs/heads/".Length..] : BranchBox.Text.Trim();
            var request = new CreateSparseWorktree(DestinationBox.Text.Trim(), branch, existing, commit, scope);
            await RunAsync("正在检查创建条件…", async token =>
            {
                Result = await Task.Run(() => _service.CreateAsync(_repository, request, progress, token), token);
            }, writing: true);
        }
        else
        {
            await RunAsync("正在检查受影响的文件…", async token =>
            {
                var preview = await Task.Run(() => _service.PreviewAsync(_repository, _target, scope, token), token);
                token.ThrowIfCancellationRequested();
                if (preview.Commit != commit || _originalScope is null ||
                    !preview.Before.Directories.SequenceEqual(_originalScope.Directories))
                    throw new GitException("提交或稀疏范围已变化。请关闭窗口并重新打开。");

                var confirmation = new SparsePreviewWindow(preview) { Owner = this };
                if (confirmation.ShowDialog() != true)
                {
                    Feedback.Content = "已返回目录选择。";
                    return;
                }
                _writing = true;
                CloseButton.IsEnabled = false;
                var discardConfirmation = confirmation.DiscardConfirmation;
                Result = await Task.Run(() => _service.ApplyAsync(_repository, preview, progress, token,
                    discardConfirmation), token);
            });
        }
        if (Result is not null && !_closed)
            DialogResult = true;
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> operation, bool writing = false)
    {
        if (_working || _closed)
            return;
        _working = true;
        _writing = writing;
        Editor.IsEnabled = false;
        SubmitButton.IsEnabled = false;
        CloseButton.IsEnabled = !writing;
        Feedback.Tag = null;
        Feedback.Content = message;
        try
        {
            await operation(_lifetime.Token);
            if (Equals(Feedback.Content, message))
                Feedback.Content = "";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or FormatException)
        {
            if (!_closed)
            {
                Feedback.Tag = "Error";
                Feedback.Content = ex.Message;
            }
        }
        finally
        {
            _working = _writing = false;
            if (_closed)
                _lifetime.Dispose();
            else
            {
                Editor.IsEnabled = true;
                CloseButton.IsEnabled = true;
                UpdateSelection();
            }
        }
    }

    private sealed record StartChoice(string Label, BranchChoice? Branch, bool Custom);
    private sealed record ReuseChoice(WorktreeEntry Entry)
    {
        public string Label => $"{Path.GetFileName(Entry.Path)} · {Entry.Branch ?? Entry.Path}";
    }
}
