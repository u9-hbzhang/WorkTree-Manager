using System.Windows;
using WorkTree.Git;

namespace WorkTree.Desktop;

public partial class SparsePreviewWindow : Window
{
    private readonly SparseChangePreview _preview;

    internal SparsePreviewWindow(SparseChangePreview preview)
    {
        _preview = preview;
        InitializeComponent();
        var added = preview.After.Directories.Except(preview.Before.Directories, StringComparer.Ordinal);
        var removed = preview.Before.Directories.Except(preview.After.Directories, StringComparer.Ordinal);
        ScopeChanges.Text = $"新增勾选：{Display(added)}{Environment.NewLine}取消勾选：{Display(removed)}";
        RemovedTab.Header = $"从磁盘移出的受跟踪文件（{preview.RemovedFiles.Count}）";
        ProtectedTab.Header = $"将丢弃的本地内容（{preview.DiscardableFiles.Count}）";
        BlockingTab.Header = $"需要先处理的内容（{preview.BlockingFiles.Count}）";
        Removed.ItemsSource = preview.RemovedFiles;
        Protected.ItemsSource = preview.DiscardableFiles;
        Blocking.ItemsSource = preview.BlockingFiles;
        ProtectedTab.Visibility = preview.DiscardableFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BlockingTab.Visibility = preview.BlockingFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (preview.BlockingFiles.Count > 0)
        {
            Heading.Text = "请先处理列出的阻塞项，再调整范围。";
            FileTabs.SelectedItem = BlockingTab;
        }
        else if (preview.DiscardableFiles.Count > 0)
        {
            Heading.Text = "缩小范围会丢弃列出的本地内容";
            FileTabs.SelectedItem = ProtectedTab;
            ConfirmationPanel.Visibility = Visibility.Visible;
            WorkspaceName.Text = preview.ConfirmationName;
            Apply.Content = "丢弃并应用范围";
            Apply.Background = Apply.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(170, 57, 46));
        }
        else
        {
            Heading.Text = "确认应用目录范围";
        }
        UpdateApply();
    }

    internal string? DiscardConfirmation => _preview.DiscardableFiles.Count > 0 ? Confirmation.Text : null;

    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UpdateApply();

    private void UpdateApply()
    {
        if (Apply is not null)
            Apply.IsEnabled = _preview.BlockingFiles.Count == 0 && (_preview.DiscardableFiles.Count == 0 ||
                string.Equals(Confirmation.Text, _preview.ConfirmationName, StringComparison.Ordinal));
    }

    private void CopyName(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_preview.ConfirmationName);
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

    private static string Display(IEnumerable<string> paths)
    {
        var text = string.Join("、", paths);
        return text.Length == 0 ? "无" : text;
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (Apply.IsEnabled)
            DialogResult = true;
    }
}
