using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WorkTree.Core;
using WorkTree.Desktop.Explorer;
using WorkTree.Desktop.ViewModels;

namespace WorkTree.Desktop;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<ToolDraft> _tools = [];
    private readonly MainViewModel _viewModel;
    private bool _saving;

    internal SettingsWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        GitPathBox.Text = viewModel.ConfiguredGitPath ?? "";
        GitHint.Text = "留空时自动查找本机 Git。"
            + (viewModel.GitExecutable is { } executable ? $" 当前使用 {executable}" : "");
        try
        {
            var (enabled, needsRepair) = ExplorerMenu.GetState();
            ExplorerMenuEnabled.IsChecked = enabled;
            if (needsRepair)
                ExplorerMenuHint.Text = "右键菜单仍指向旧位置。保持勾选并保存即可重新注册。";
        }
        catch (Exception ex) when (IsExplorerRegistrationError(ex))
        {
            ExplorerMenuEnabled.IsEnabled = false;
            ExplorerMenuHint.Text = $"无法读取右键菜单状态。{ex.Message}";
        }
        foreach (var tool in viewModel.Tools)
            _tools.Add(new ToolDraft(tool));
        ToolList.ItemsSource = _tools;
        ToolList.SelectedIndex = _tools.Count == 0 ? -1 : 0;
        Closing += (_, e) => e.Cancel = _saving;
    }

    private void BrowseGit(object sender, RoutedEventArgs e)
    {
        if (PickExecutable("选择 Git", "Git (git.exe)|git.exe") is { } path)
            GitPathBox.Text = path;
    }

    private void BrowseTool(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is ToolDraft tool &&
            PickExecutable("选择开发工具", "应用程序 (*.exe)|*.exe") is { } path)
            tool.Executable = path;
    }

    private string? PickExecutable(string title, string filter)
    {
        var picker = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }

    private void AddTool(object sender, RoutedEventArgs e)
    {
        var tool = new ToolDraft(new ExternalTool("新工具", "", [ExternalTool.PathToken]));
        _tools.Add(tool);
        ToolList.SelectedItem = tool;
        ToolNameBox.Focus();
        ToolNameBox.SelectAll();
    }

    private void RemoveTool(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolDraft tool)
            return;
        var index = ToolList.SelectedIndex;
        _tools.Remove(tool);
        ToolList.SelectedIndex = Math.Min(index, _tools.Count - 1);
    }

    private async void Save(object sender, RoutedEventArgs e)
    {
        if (_saving)
            return;

        ExternalTool[] tools;
        try
        {
            tools = _tools.Select(draft => draft.ToTool()).ToArray();
            if (tools.Select(tool => tool.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tools.Length)
                throw new IOException("开发工具的显示名称不能重复。");
        }
        catch (IOException ex)
        {
            Validation.Tag = "Error";
            Validation.Content = ex.Message;
            return;
        }

        Validation.Tag = null;
        Validation.Content = "正在保存…";
        bool? explorerMenu = ExplorerMenuEnabled.IsEnabled ? ExplorerMenuEnabled.IsChecked : null;
        _saving = true;
        IsEnabled = false;
        bool saved;
        try
        {
            var gitPath = GitPathBox.Text.Trim();
            saved = await _viewModel.ApplySettingsAsync(gitPath.Length == 0 ? null : gitPath, tools);
            if (saved && explorerMenu is { } desired)
            {
                var state = ExplorerMenu.GetState();
                if (desired != state.Enabled || (desired && state.NeedsRepair))
                    ExplorerMenu.SetEnabled(desired);
                ((App)Application.Current).UpdateExplorerServer();
            }
        }
        catch (Exception ex) when (IsExplorerRegistrationError(ex))
        {
            Validation.Tag = "Error";
            Validation.Content = $"无法更新资源管理器右键菜单。{ex.Message}";
            return;
        }
        finally
        {
            _saving = false;
            IsEnabled = true;
        }

        if (saved)
            DialogResult = true;
        else
        {
            Validation.Tag = "Error";
            Validation.Content = _viewModel.Message;
        }
    }

    private static bool IsExplorerRegistrationError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or System.Runtime.InteropServices.COMException;
}

internal sealed class ToolDraft(ExternalTool tool) : Bindable
{
    private string _name = tool.Name;
    private string _executable = tool.Executable;
    private string _argumentText = string.Join(Environment.NewLine, tool.Arguments);

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Executable { get => _executable; set => Set(ref _executable, value); }
    public string ArgumentText { get => _argumentText; set => Set(ref _argumentText, value); }

    public ExternalTool ToTool()
    {
        var name = Name.Trim();
        var executable = Executable.Trim();
        var arguments = ArgumentText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (name.Length == 0 || !Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请为每个开发工具填写名称和 .exe 的完整路径。");
        if (!arguments.Any(arg => arg.Contains(ExternalTool.PathToken, StringComparison.Ordinal)))
            throw new IOException($"请在 {name} 的参数里加入 {{worktree}}。");

        return new ExternalTool(name, executable, arguments);
    }
}
