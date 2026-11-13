using System.IO;
using System.Windows;
using System.Windows.Controls;
using WorkTree.Core;
using WorkTree.Git;

namespace WorkTree.Desktop;

public partial class WorktreeCleanupWindow : Window
{
    private readonly RepositoryLocation _repository;
    private readonly WorktreeEntry? _target;
    private readonly GitInstallation _git;
    private readonly WorktreeCleanupService _service;
    private readonly Func<string?, Task> _saveTarget;
    private readonly CancellationTokenSource _lifetime = new();
    private WorktreeRemovalPreview? _removal;
    private PrunePreview? _prune;
    private string? _mergeTarget;
    private BranchRemovalMode _branchMode;
    private bool _working;
    private bool _writing;
    private bool _closed;
    private bool _changingTargets;

    internal WorktreeCleanupWindow(RepositoryLocation repository, GitInstallation git, WorktreeEntry? target,
        string? mergeTarget, Func<string?, Task> saveTarget)
    {
        _repository = repository;
        _target = target;
        _git = git;
        _service = new WorktreeCleanupService(git);
        _mergeTarget = IsOriginBranch(mergeTarget) ? null : mergeTarget;
        _saveTarget = saveTarget;
        InitializeComponent();
        Title = Heading.Text = target is null ? "清理失效登记" : "删除工作区";
        TargetPath.Text = target?.Path ?? repository.MainPath;
        BranchOptions.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        RunButton.Content = Title;
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

    internal CleanupResult? Result { get; private set; }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        Loaded -= Initialize;
        await RunAsync("正在生成清理预览…", LoadPreviewAsync);
    }

    private async Task LoadTargetsAsync(CancellationToken token)
    {
        try
        {
            var choices = await new SparseWorktreeService(_git).ListBranchesAsync(_repository, token);
            var targets = new List<MergeChoice> { new(null, "请选择合入目标") };
            targets.AddRange(choices.Where(choice => !choice.Reference.StartsWith("refs/tags/", StringComparison.Ordinal)
                && !IsOriginBranch(choice.Reference)
                && choice.Reference != "refs/heads/" + _target?.Branch)
                .Select(choice => new MergeChoice(choice.Reference, choice.Name)));
            if (_mergeTarget is not null && targets.All(choice => choice.Reference != _mergeTarget))
                targets.Add(new MergeChoice(_mergeTarget, _mergeTarget + "（当前不可用）"));

            _changingTargets = true;
            try
            {
                MergeTargets.ItemsSource = targets;
                MergeTargets.SelectedItem = targets.First(choice => choice.Reference == _mergeTarget);
            }
            finally { _changingTargets = false; }
        }
        catch (Exception ex) when (IsUserFacing(ex))
        {
            Feedback.Tag = "Error";
            Feedback.Content = $"无法读取合入目标分支。{ex.Message}";
        }
    }

    private async Task LoadPreviewAsync(CancellationToken token)
    {
        _removal = null;
        _prune = null;
        if (_target is { } target)
        {
            var progress = CleanupProgress();
            var preview = await Task.Run(() => _service.PreviewRemovalAsync(_repository, target.Path, null, token, progress), token);
            token.ThrowIfCancellationRequested();
            _removal = preview;
            Summary.Text = $"将删除整个工作目录和 Git 登记。目录内有 {preview.FileCount} 个文件。";
            ListHeading.Text = $"将丢弃的本地内容（{preview.LocalFiles.Count} 项）";
            ImpactList.ItemsSource = preview.LocalFiles;
            BranchLabel.Text = preview.Entry.Branch is { } branch ? $"本地分支 · {branch}" : "此工作区没有本地分支";
            BranchStatus.Text = preview.BranchMessage;
            BranchOptions.Visibility = preview.CanDiscardBranch ? Visibility.Visible : Visibility.Collapsed;
            if (preview.Entry.Branch is not null && !preview.CanDiscardBranch)
                Summary.Text += " 本地分支仍被其他工作区使用，将保留。";
            UpdateConfirmation();
            Feedback.Content = "";
        }
        else
        {
            var preview = await Task.Run(() => _service.PreviewPruneAsync(_repository, token), token);
            token.ThrowIfCancellationRequested();
            _prune = preview;
            Summary.Text = "此操作清理整个仓库中已确认失效的 Git 登记，保留本地分支。";
            ListHeading.Text = preview.Entries.Count == 0
                ? "没有可清理的失效登记。"
                : $"将清理的登记（{preview.Entries.Count} 条）";
            ImpactList.ItemsSource = preview.Entries;
        }
    }

    private void MergeTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingTargets)
            UpdateControls();
    }

    private async void BranchModeChanged(object sender, RoutedEventArgs e)
    {
        if (_working || _removal is not { } preview)
            return;
        var mode = DiscardBranch.IsChecked == true ? BranchRemovalMode.Discard :
            DeleteMergedBranch.IsChecked == true ? BranchRemovalMode.DeleteIfMerged : BranchRemovalMode.Keep;
        if (mode == _branchMode)
            return;

        var confirmAgain = mode == BranchRemovalMode.Discard || _branchMode == BranchRemovalMode.Discard;
        _branchMode = mode;
        MergeOptions.Visibility = mode == BranchRemovalMode.DeleteIfMerged ? Visibility.Visible : Visibility.Collapsed;
        if (confirmAgain)
            Confirmation.Clear();
        UpdateConfirmation();
        UpdateControls();

        if (mode == BranchRemovalMode.DeleteIfMerged && !MergeTargets.HasItems)
        {
            await RunAsync("正在读取合入目标列表…", async token =>
            {
                await LoadTargetsAsync(token);
                if (_mergeTarget is not null)
                {
                    _removal = await _service.RecheckMergeTargetAsync(_repository, preview, _mergeTarget, token);
                    BranchStatus.Text = _removal.BranchMessage;
                }
            });
        }
    }

    private void UpdateConfirmation()
    {
        if (_removal is not { } preview)
            return;
        var discardBranch = _branchMode == BranchRemovalMode.Discard;
        ConfirmationPanel.Visibility = discardBranch || preview.LocalFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var impact = discardBranch
            ? $"删除本地分支「{preview.Entry.Branch}」。该分支独有的提交可能丢失。"
            : "丢弃上方列出的本地内容。";
        if (discardBranch && preview.LocalFiles.Count > 0)
            impact += " 上方列出的本地内容也将丢弃。";
        ConfirmationName.Text = preview.ConfirmationName;
        ConfirmationHint.Text = $"」，确认{impact}";
    }

    private void CopyConfirmationName(object sender, RoutedEventArgs e)
    {
        if (_working || _removal is not { } preview)
            return;
        try
        {
            Clipboard.SetText(preview.ConfirmationName);
            Feedback.Tag = null;
            Feedback.Content = "已复制工作区名称。";
            Confirmation.Focus();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Feedback.Tag = "Error";
            Feedback.Content = "无法写入剪贴板，请重试。";
        }
    }

    private async void SaveMergeTarget(object sender, RoutedEventArgs e)
    {
        if (_working || _removal is not { } preview || MergeTargets.SelectedItem is not MergeChoice choice ||
            choice.Reference == _mergeTarget)
            return;
        await RunAsync("正在保存目标并重新检查…", async token =>
        {
            var rechecked = await _service.RecheckMergeTargetAsync(_repository, preview, choice.Reference, token);
            await _saveTarget(choice.Reference);
            _mergeTarget = choice.Reference;
            _removal = rechecked;
            BranchStatus.Text = rechecked.BranchMessage;
        }, writing: true);
    }

    private void ConfirmationChanged(object sender, TextChangedEventArgs e) => UpdateControls();

    private async void Execute(object sender, RoutedEventArgs e)
    {
        if (_working || !RunButton.IsEnabled)
            return;
        var discardConfirmation = Confirmation.Text;
        var branchMode = _branchMode;
        var removal = _removal;
        var prune = _prune;
        var progress = CleanupProgress();
        await RunAsync("正在复核预览并执行清理…", async token =>
        {
            try
            {
                Result = removal is not null
                    ? await Task.Run(() => _service.RemoveAsync(_repository, removal, discardConfirmation, branchMode, token, progress), token)
                    : await Task.Run(() => _service.PruneAsync(_repository, prune!, token), token);
            }
            catch
            {
                _removal = null;
                _prune = null;
                Confirmation.Clear();
                CloseButton.Content = "关闭";
                throw;
            }
        }, writing: true);
        if (Result is not null && !_closed)
            DialogResult = true;
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action, bool writing = false)
    {
        if (_working || _closed)
            return;
        _working = true;
        _writing = writing;
        Feedback.Tag = null;
        Feedback.Content = message;
        UpdateControls();
        try
        {
            await action(_lifetime.Token);
            if (Equals(Feedback.Content, message))
                Feedback.Content = "";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (IsUserFacing(ex))
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
                UpdateControls();
        }
    }

    private void UpdateControls()
    {
        if (RunButton is null)
            return;
        var pendingTarget = MergeTargets.SelectedItem is MergeChoice choice && choice.Reference != _mergeTarget;
        SaveTargetButton.IsEnabled = !_working && pendingTarget && _removal is not null;
        MergeTargets.IsEnabled = !_working && MergeTargets.HasItems;
        KeepBranch.IsEnabled = !_working && _removal is not null;
        DeleteMergedBranch.IsEnabled = DiscardBranch.IsEnabled = !_working && _removal?.CanDiscardBranch == true;
        Confirmation.IsEnabled = !_working;
        CloseButton.IsEnabled = !_writing;
        RunButton.Content = _branchMode switch
        {
            BranchRemovalMode.Discard => "删除工作区并丢弃分支",
            BranchRemovalMode.DeleteIfMerged => "删除工作区和分支",
            _ => Title
        };
        var branchReady = _branchMode switch
        {
            BranchRemovalMode.Discard => _removal?.CanDiscardBranch == true,
            BranchRemovalMode.DeleteIfMerged => !pendingTarget && _removal?.CanDeleteBranch == true,
            _ => true
        };
        RunButton.IsEnabled = !_working && (_prune is { Entries.Count: > 0 } ||
            (_removal is { } removal && branchReady &&
                ((removal.LocalFiles.Count == 0 && _branchMode != BranchRemovalMode.Discard) ||
                string.Equals(Confirmation.Text, removal.ConfirmationName, StringComparison.Ordinal))));
    }

    private IProgress<string> CleanupProgress() => new Progress<string>(message =>
    {
        if (!_closed && _working && Feedback.Tag is null)
            Feedback.Content = message;
    });

    private static bool IsUserFacing(Exception ex) => ex is GitException or IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException;

    private static bool IsOriginBranch(string? reference) =>
        reference?.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) == true;

    private sealed record MergeChoice(string? Reference, string Label);
}
