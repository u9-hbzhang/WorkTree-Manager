using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using WorkTree.Core;
using WorkTree.Git;

namespace WorkTree.Desktop.ViewModels;

internal sealed class MainViewModel : Bindable, IDisposable
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly string _dataPath = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly PortableStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _detailSlots = new(2);
    private readonly SemaphoreSlim _settingsSlot = new(1);
    private readonly ObservableCollection<WorktreeRow> _rows = [];
    private readonly Dictionary<string, RepositoryList> _lists = new(Paths);
    private CancellationTokenSource? _usageScan;
    private GitInstallation? _git;
    private AppSettings _settings = new();
    private bool _settingsValid = true;
    private bool _initializing = true;
    private bool _busy;
    private bool _suppressSelection;
    private bool _disposed;
    private RepositoryLocation? _selectedRepository;
    private WorktreeRow? _selectedWorktree;
    private string _search = "";
    private string _message = "";
    private string _listCaption = "添加本地仓库以开始。";
    private string _usage = "尚未统计";

    public MainViewModel()
    {
        _store = new PortableStore(_dataPath);
        Rows = CollectionViewSource.GetDefaultView(_rows);
        Rows.Filter = item => item is WorktreeRow row && Matches(row);
        RefreshCommand = new ActionCommand(async () => await RefreshAsync(), () => CanRefresh);
        OpenFolderCommand = new ActionCommand(OpenFolder, () => SelectedWorktree?.CanOpen == true);
        CopyPathCommand = new ActionCommand(CopyPath, () => SelectedWorktree is not null);
        MeasureCommand = new ActionCommand(async () => await MeasureAsync(),
            () => SelectedWorktree?.CanOpen == true && _usageScan is null);
    }

    public ObservableCollection<RepositoryLocation> Repositories { get; } = [];
    public ICollectionView Rows { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand MeasureCommand { get; }

    public bool CanEditSettings => !_initializing && !_busy;
    public bool CanAddRepository => CanEditSettings && _git is not null;
    public bool CanManageRepository => CanEditSettings && SelectedRepository is not null;
    public bool CanCreateWorktree => CanAddRepository && SelectedRepository is not null;
    public bool IsListRefreshing => SelectedRepository is { } repository
        && _lists.GetValueOrDefault(repository.CommonDirectory)?.Refresh is not null;
    private bool CanRefresh => CanAddRepository && SelectedRepository is not null && !IsListRefreshing;
    public string? ConfiguredGitPath => _settings.GitPath;
    public ExternalTool[] Tools => _settings.Tools;
    public string GitCaption => _initializing ? "正在检测 Git…" : _git is null ? "Git 不可用" : $"Git {_git.Version}";
    public string? GitExecutable => _git?.Executable;
    public string RepositoryTitle => SelectedRepository?.Name ?? "本地工作区";
    public string? RepositoryPath => SelectedRepository?.MainPath;
    public string EmptyListText => SelectedRepository is null ? "添加仓库后，工作区会显示在这里。"
        : _search.Trim().Length > 0 && _rows.Count > 0 ? "没有匹配的工作区。" : "暂无可显示的工作区登记。";

    public string Message { get => _message; private set => Set(ref _message, value); }
    public string ListCaption { get => _listCaption; private set => Set(ref _listCaption, value); }
    public string Usage { get => _usage; private set => Set(ref _usage, value); }

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value))
                return;
            Rows.Refresh();
            Notify(nameof(EmptyListText));
        }
    }

    public RepositoryLocation? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (!Set(ref _selectedRepository, value))
                return;
            Notify(nameof(RepositoryTitle));
            Notify(nameof(RepositoryPath));
            Notify(nameof(EmptyListText));
            NotifyCapabilities();
            if (!_suppressSelection && !_initializing)
                _ = ChangeSelectionAsync();
        }
    }

    public WorktreeRow? SelectedWorktree
    {
        get => _selectedWorktree;
        set
        {
            if (!Set(ref _selectedWorktree, value))
                return;
            _usageScan?.Cancel();
            Usage = "尚未统计";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            try
            {
                _settings = _store.LoadSettings();
            }
            catch (Exception ex) when (IsUserFacing(ex))
            {
                _settingsValid = false;
                Report("设置无法读取，原文件已保留。", ex);
            }

            foreach (var repository in _settings.Repositories)
                Repositories.Add(repository);

            SelectWithoutReload(Repositories.FirstOrDefault(repo => Paths.Equals(
                repo.CommonDirectory, _settings.SelectedRepository)) ?? Repositories.FirstOrDefault());
            ShowRepositorySelection();
            var detection = await Task.Run(() => GitDiscovery.DetectAsync(_settings.GitPath, _lifetime.Token));
            _git = detection.Installation;
            if (detection.Message is not null)
                Report(detection.Message);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) when (IsUserFacing(ex)) { Report("Git 检测失败。", ex); }
        finally
        {
            _initializing = false;
            NotifyCapabilities();
        }

        if (!_disposed)
            await LoadSelectedOnceAsync();
    }

    public async Task AddOrRelocateAsync(string folder, RepositoryLocation? replace)
    {
        if (_git is null || _busy)
            return;

        _busy = true;
        NotifyCapabilities();
        try
        {
            var reader = new GitRepositoryReader(_git);
            var (repository, snapshot) = await reader.OpenAsync(folder, _lifetime.Token);
            var existing = Repositories.FirstOrDefault(r => Paths.Equals(r.CommonDirectory, repository.CommonDirectory));
            var updated = Repositories.ToList();
            var index = replace is null ? -1 : updated.IndexOf(replace);
            if (existing is not null && !ReferenceEquals(existing, replace))
            {
                updated.Remove(existing);
                index = replace is null ? Repositories.IndexOf(existing) : updated.IndexOf(replace);
            }
            if (replace is not null)
                updated.Remove(replace);
            if (index < 0)
                index = updated.Count;
            updated.Insert(Math.Min(index, updated.Count), repository);
            await SaveSettingsAsync(_settings with
            {
                Repositories = [.. updated],
                SelectedRepository = repository.CommonDirectory
            });
            if (replace is not null)
                ForgetList(replace);
            ForgetList(repository);
            UpdateRepositories(updated, repository);
            ShowRepositorySelection();
            Message = "";
            _ = LoadSnapshotAsync(reader, repository, snapshot);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) when (IsUserFacing(ex)) { Report("无法打开该仓库。", ex); }
        finally
        {
            _busy = false;
            NotifyCapabilities();
        }
    }

    public async Task RemoveRepositoryAsync()
    {
        if (!CanManageRepository || SelectedRepository is not { } repository)
            return;
        _busy = true;
        NotifyCapabilities();
        try
        {
            var updated = Repositories.Where(repo => repo != repository).ToArray();
            var selected = updated.FirstOrDefault();
            await SaveSettingsAsync(_settings with
            {
                Repositories = updated,
                SelectedRepository = selected?.CommonDirectory
            });
            ForgetList(repository);
            UpdateRepositories(updated, selected);
            Message = "";
            ShowRepositorySelection();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) when (IsUserFacing(ex)) { Report("无法从列表移除仓库。", ex); }
        finally
        {
            _busy = false;
            NotifyCapabilities();
        }
        await LoadSelectedOnceAsync();
    }

    public async Task<bool> ApplySettingsAsync(string? gitPath, ExternalTool[] tools)
    {
        if (!CanEditSettings)
            return false;

        _busy = true;
        NotifyCapabilities();
        try
        {
            var detection = gitPath is null
                ? await Task.Run(() => GitDiscovery.DetectAsync(null, _lifetime.Token))
                : new GitDetection(await GitDiscovery.ValidateAsync(gitPath, _lifetime.Token), null);
            await SaveSettingsAsync(_settings with { GitPath = gitPath, Tools = tools });
            _git = detection.Installation;
            Notify(nameof(Tools));
            Message = detection.Message ?? "";
        }
        catch (OperationCanceledException) when (_disposed) { return false; }
        catch (Exception ex) when (IsUserFacing(ex)) { Report("无法保存设置。", ex); return false; }
        finally
        {
            _busy = false;
            NotifyCapabilities();
        }

        return true;
    }

    public void OpenExternal(ExternalTool tool)
    {
        if (SelectedWorktree is not { CanOpen: true } row)
            return;
        try
        {
            if (!Directory.Exists(row.PathText))
                throw new DirectoryNotFoundException(row.PathText);
            var executable = RequireExecutable(tool.Executable);
            StartProgram(executable, row.PathText, tool.ArgumentsFor(row.PathText));
        }
        catch (Exception ex) when (IsUserFacing(ex)) { Report($"无法启动 {tool.Name}。请检查设置中的程序路径。", ex); }
    }

    public SparseWorktreeWindow? OpenSparseEditor(bool adjust)
    {
        if (!CanCreateWorktree || _git is null || SelectedRepository is not { } repository
            || (adjust && SelectedWorktree?.CanAdjustScope != true))
            return null;

        var rows = GetList(repository).Rows;
        return new SparseWorktreeWindow(repository, _git, SelectedWorktree?.Entry,
            adjust ? SelectedWorktree?.Entry : null, rows.Select(row => row.Entry),
            rows.Where(row => row.CanReuseScope).Select(row => row.Entry));
    }

    public WorktreeCleanupWindow? OpenCleanupEditor(bool prune)
    {
        if (!CanCreateWorktree || _git is null || SelectedRepository is not { } repository
            || (!prune && SelectedWorktree?.CanRemove != true))
            return null;

        var target = _settings.MergeTargets.GetValueOrDefault(repository.CommonDirectory);
        return new WorktreeCleanupWindow(repository, _git, prune ? null : SelectedWorktree!.Entry,
            target, reference => SaveMergeTargetAsync(repository, reference));
    }

    private Task SaveMergeTargetAsync(RepositoryLocation repository, string? reference)
    {
        var targets = new Dictionary<string, string>(_settings.MergeTargets, Paths);
        if (reference is null)
            targets.Remove(repository.CommonDirectory);
        else
            targets[repository.CommonDirectory] = reference;
        return SaveSettingsAsync(_settings with { MergeTargets = targets });
    }

    public async Task AcceptCleanupResultAsync(CleanupResult result)
    {
        if (SelectedRepository is not { } repository)
            return;
        Message = result.Message;
        if (result.RemovedPaths.Count == 0)
            return;

        var list = GetList(repository);
        list.Refresh?.Cancel();
        list.HasRequestedLoad = true;
        var removed = result.RemovedPaths.ToHashSet(Paths);
        list.Rows = list.Rows.Where(row => !removed.Contains(row.PathText)).ToArray();
        ShowRows(list.Rows);
        SetCaption(repository, list, $"{list.Rows.Length} 个工作区 · 清理后更新");
        try
        {
            await _store.SaveSnapshotAsync(repository, new WorktreeSnapshot(DateTimeOffset.Now,
                list.Rows.Select(row => row.Entry).ToArray()));
        }
        catch (Exception ex) when (IsUserFacing(ex))
        {
            Report(result.Message + " 无法保存列表缓存。", ex);
        }
    }

    public async Task AcceptSparseResultAsync(SparseWorktreeResult result)
    {
        if (SelectedRepository is not { } repository || _git is null)
            return;
        var list = GetList(repository);
        list.Refresh?.Cancel();
        list.HasRequestedLoad = true;
        var row = list.Rows.FirstOrDefault(candidate => Paths.Equals(candidate.PathText, result.Entry.Path));
        if (row is null)
        {
            row = new WorktreeRow(result.Entry);
            list.Rows = [.. list.Rows, row];
        }
        else
        {
            row.UpdateEntry(result.Entry);
        }
        row.Update(result.Details);
        if (!Matches(row))
            Search = "";
        ShowRows(list.Rows, entriesChanged: true);
        SelectedWorktree = row;
        var now = DateTimeOffset.Now;
        SetCaption(repository, list, $"{list.Rows.Length} 个工作区 · 已更新 {row.Name}");
        Message = result.Warning ?? "";
        try
        {
            await _store.SaveSnapshotAsync(repository, new WorktreeSnapshot(now, list.Rows.Select(item => item.Entry).ToArray()));
        }
        catch (Exception ex) when (IsUserFacing(ex)) { Report("工作区已更新，无法写入本地缓存。", ex); }
        if (!_disposed)
        {
            try { await LoadDetailsAsync(new GitRepositoryReader(_git), repository, row, _lifetime.Token); }
            catch (OperationCanceledException) when (_disposed) { }
        }
    }

    public static string RequireExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择存在的 .exe 文件。");
        return path;
    }

    private async Task ChangeSelectionAsync()
    {
        ShowRepositorySelection();
        var selected = SelectedRepository?.CommonDirectory;
        if (_settingsValid && !Paths.Equals(selected, _settings.SelectedRepository))
        {
            try { await SaveSettingsAsync(_settings with { SelectedRepository = selected }); }
            catch (OperationCanceledException) when (_disposed) { return; }
            catch (Exception ex) when (IsUserFacing(ex)) { Report("无法保存所选仓库。", ex); }
        }
        if (Paths.Equals(selected, SelectedRepository?.CommonDirectory) && !_disposed)
            await LoadSelectedOnceAsync();
    }

    private Task LoadSelectedOnceAsync() =>
        SelectedRepository is { } repository && GetList(repository).HasRequestedLoad
            ? Task.CompletedTask : RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!CanRefresh || SelectedRepository is not { } repository || _git is null || _disposed)
            return;

        await LoadSnapshotAsync(new GitRepositoryReader(_git), repository, null);
    }

    private async Task LoadSnapshotAsync(GitRepositoryReader reader, RepositoryLocation repository, WorktreeSnapshot? initial)
    {
        var list = GetList(repository);
        if (list.Refresh is not null)
            return;

        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        list.HasRequestedLoad = true;
        list.Refresh = refresh;
        var token = refresh.Token;
        NotifyCapabilities();
        SetCaption(repository, list, list.Rows.Length == 0 ? "正在读取工作区登记…" : "正在刷新工作区登记…");

        try
        {
            var snapshot = initial ?? await reader.RefreshAsync(repository, token);
            token.ThrowIfCancellationRequested();
            var previous = list.Rows.ToDictionary(row => row.PathText, Paths);
            var entriesChanged = false;
            var rows = snapshot.Entries.Select(entry =>
            {
                if (!previous.TryGetValue(entry.Path, out var row))
                    return new WorktreeRow(entry);
                entriesChanged |= row.Entry != entry;
                row.UpdateEntry(entry);
                return row;
            }).ToArray();
            list.Rows = rows;
            if (SelectedRepository == repository)
                ShowRows(rows, entriesChanged);
            SetCaption(repository, list, $"{rows.Length} 个工作区 · 正在检查状态…");
            try { await _store.SaveSnapshotAsync(repository, snapshot); }
            catch (Exception ex) when (IsUserFacing(ex)) { Report("列表已读取，无法写入本地缓存。", ex); }
            token.ThrowIfCancellationRequested();

            var ordered = rows.OrderByDescending(row => row == SelectedWorktree);
            await Task.WhenAll(ordered.Select(row => LoadDetailsAsync(reader, repository, row, token)));
            token.ThrowIfCancellationRequested();
            SetCaption(repository, list, $"{rows.Length} 个工作区 · 刷新于 {snapshot.ReadAt:HH:mm:ss}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (IsUserFacing(ex))
        {
            SetCaption(repository, list, list.Rows.Length > 0 ? "刷新失败，显示上次读取的登记。" : "无法读取工作区登记。");
            Report("无法刷新。请检查 Git、仓库路径或使用“重新定位”。", ex);
        }
        finally
        {
            list.Refresh = null;
            NotifyCapabilities();
        }
    }

    private async Task LoadDetailsAsync(GitRepositoryReader reader, RepositoryLocation repository,
        WorktreeRow row, CancellationToken token)
    {
        await _detailSlots.WaitAsync(token);
        try
        {
            WorktreeDetails details;
            try
            {
                details = await Task.Run(() => reader.ReadDetailsAsync(repository, row.Entry, token), token);
            }
            catch (Exception ex) when (IsUserFacing(ex))
            {
                details = new WorktreeDetails(WorktreeAvailability.Unavailable, Error: ex.Message);
            }
            token.ThrowIfCancellationRequested();
            row.Update(details);
            CommandManager.InvalidateRequerySuggested();
        }
        finally { _detailSlots.Release(); }
    }

    private void ShowRepositorySelection()
    {
        SelectedWorktree = null;
        _rows.Clear();
        if (SelectedRepository is not { } repository)
        {
            ListCaption = "添加本地仓库以开始。";
            Notify(nameof(EmptyListText));
            return;
        }

        var list = GetList(repository);
        ListCaption = list.Caption;
        ShowRows(list.Rows);
    }

    private RepositoryList GetList(RepositoryLocation repository)
    {
        if (_lists.TryGetValue(repository.CommonDirectory, out var list))
            return list;

        list = new RepositoryList();
        _lists.Add(repository.CommonDirectory, list);
        try
        {
            if (_store.LoadSnapshot(repository) is { } snapshot)
            {
                list.Rows = snapshot.Entries.Select(entry => new WorktreeRow(entry)).ToArray();
                list.Caption = $"{list.Rows.Length} 个工作区 · 缓存于 {snapshot.ReadAt:MM-dd HH:mm}";
            }
        }
        catch (Exception ex) when (IsUserFacing(ex)) { Report("无法读取列表缓存。", ex); }

        return list;
    }

    private void SetCaption(RepositoryLocation repository, RepositoryList list, string caption)
    {
        list.Caption = caption;
        if (SelectedRepository == repository)
            ListCaption = caption;
    }

    private void ShowRows(WorktreeRow[] rows, bool entriesChanged = false)
    {
        var selected = SelectedWorktree;
        var current = rows.ToHashSet();
        for (var i = _rows.Count - 1; i >= 0; i--)
            if (!current.Contains(_rows[i]))
                _rows.RemoveAt(i);

        for (var i = 0; i < rows.Length; i++)
        {
            if (i < _rows.Count && _rows[i] == rows[i])
                continue;
            var existing = _rows.IndexOf(rows[i]);
            if (existing >= 0)
                _rows.Move(existing, i);
            else
                _rows.Insert(i, rows[i]);
        }

        if (entriesChanged && (_search.Trim().Length > 0 || Rows.SortDescriptions.Count > 0))
            Rows.Refresh();
        SelectedWorktree = selected is not null && current.Contains(selected) && Matches(selected)
            ? selected : rows.FirstOrDefault(Matches);
        Notify(nameof(EmptyListText));
        CommandManager.InvalidateRequerySuggested();
    }

    private bool Matches(WorktreeRow row)
    {
        var query = _search.Trim();
        return query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Branch.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.PathText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        if (!_settingsValid)
            throw new IOException($"请先修复或移走 {_dataPath}\\settings.json，再重新打开程序。");
        await _settingsSlot.WaitAsync(_lifetime.Token);
        try
        {
            await _store.SaveSettingsAsync(settings);
            _settings = settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法写入 {_dataPath}。请将软件移到当前用户可写的文件夹。", ex);
        }
        finally { _settingsSlot.Release(); }
    }

    private void UpdateRepositories(IEnumerable<RepositoryLocation> repositories, RepositoryLocation? selected)
    {
        _suppressSelection = true;
        try
        {
            Repositories.Clear();
            foreach (var repository in repositories)
                Repositories.Add(repository);
            SelectedRepository = selected;
        }
        finally { _suppressSelection = false; }
    }

    private void SelectWithoutReload(RepositoryLocation? repository)
    {
        _suppressSelection = true;
        try { SelectedRepository = repository; }
        finally { _suppressSelection = false; }
    }

    private void OpenFolder()
    {
        if (SelectedWorktree is not { CanOpen: true } row)
            return;
        try
        {
            if (!Directory.Exists(row.PathText))
                throw new DirectoryNotFoundException(row.PathText);
            StartProgram(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                row.PathText, [row.PathText]);
        }
        catch (Exception ex) when (IsUserFacing(ex)) { Report("无法打开文件夹。", ex); }
    }

    private void CopyPath()
    {
        if (SelectedWorktree is not { } row)
            return;
        try { Clipboard.SetText(row.PathText); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Report("无法写入剪贴板，请重试。", ex); }
    }

    private async Task MeasureAsync()
    {
        if (SelectedWorktree is not { CanOpen: true } row)
            return;
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _usageScan = scan;
        Usage = "正在统计…";
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var result = await Task.Run(() => DirectoryUsage.Measure(row.PathText, scan.Token), scan.Token);
            if (!scan.IsCancellationRequested && SelectedWorktree == row)
                Usage = FormatSize(result.Bytes) + (result.SkippedEntries > 0 ? $" · 跳过 {result.SkippedEntries} 项" : "");
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested) { }
        catch (Exception ex) when (IsUserFacing(ex))
        {
            if (SelectedWorktree == row)
                Usage = $"无法统计 · {ex.Message}";
        }
        finally
        {
            _usageScan = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static string FormatSize(long bytes)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        double size = bytes;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1)
        {
            size /= 1024;
            index++;
        }
        return $"{size:0.##} {units[index]}";
    }

    private static void StartProgram(string executable, string directory, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
    }

    private void ForgetList(RepositoryLocation repository)
    {
        if (_lists.Remove(repository.CommonDirectory, out var list))
            list.Refresh?.Cancel();
    }

    private sealed class RepositoryList
    {
        public WorktreeRow[] Rows { get; set; } = [];
        public string Caption { get; set; } = "等待刷新工作区登记。";
        public bool HasRequestedLoad { get; set; }
        public CancellationTokenSource? Refresh { get; set; }
    }

    private void NotifyCapabilities()
    {
        Notify(nameof(CanEditSettings));
        Notify(nameof(CanAddRepository));
        Notify(nameof(CanManageRepository));
        Notify(nameof(CanCreateWorktree));
        Notify(nameof(IsListRefreshing));
        Notify(nameof(GitCaption));
        Notify(nameof(GitExecutable));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Report(string message, Exception? exception = null)
    {
        Message = exception is null ? message : $"{message}{Environment.NewLine}{exception.Message}";
        var entry = Message;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_dataPath);
                var log = Path.Combine(_dataPath, "app.log");
                if (File.Exists(log) && new FileInfo(log).Length > 1024 * 1024)
                    File.Move(log, log + ".old", overwrite: true);
                File.AppendAllText(log, $"{DateTimeOffset.Now:O} {entry}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    private static bool IsUserFacing(Exception ex) =>
        ex is GitException or IOException or UnauthorizedAccessException or Win32Exception or JsonException
            or ArgumentException or FormatException or NotSupportedException or InvalidOperationException
            or InvalidDataException;

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
