using WorkTree.Core;

namespace WorkTree.Git;

public sealed class GitRepositoryReader(GitInstallation installation)
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public async Task<(RepositoryLocation Location, WorktreeSnapshot Snapshot)> OpenAsync(
        string folder, CancellationToken token)
    {
        folder = Path.GetFullPath(folder);
        var common = await CommonDirectoryAsync(folder, token).ConfigureAwait(false);
        var snapshot = await ReadSnapshotAsync(folder, token).ConfigureAwait(false);
        var selected = snapshot.Entries
            .Where(entry => IsAtOrBelow(folder, entry.Path))
            .MaxBy(entry => entry.Path.Length);
        return (new RepositoryLocation(snapshot.Entries[0].Path, common, selected?.Path ?? folder), snapshot);
    }

    public async Task<(RepositoryLocation Repository, WorktreeSnapshot Snapshot, WorktreeEntry Source, string[] Directories)>
        OpenDirectorySelectionAsync(IReadOnlyList<string> folders, CancellationToken token)
    {
        if (folders.Count == 0)
            throw new GitException("请在资源管理器中选择目录。");

        string? root = null;
        var directories = new HashSet<string>(Paths);
        foreach (var folder in folders)
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
                throw new GitException($"所选项目不是可访问的目录：{folder}");

            var result = await RunAsync(folder, token, "rev-parse", "--show-toplevel").ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new GitException($"无法识别目录所属的 Git 工作区：{folder}{Environment.NewLine}{result.Error.Trim()}");
            var current = NormalizePath(result.Output.TrimEnd('\r', '\n'));
            root ??= current;
            if (!Paths.Equals(current, root))
                throw new GitException("所选目录来自不同的仓库或工作区。请只选择同一个工作区中的目录。");

            var relative = Path.GetRelativePath(root, NormalizePath(folder)).Replace('\\', '/');
            if (relative == ".")
                continue;
            if (!ConeScope.IsDirectoryPath(relative))
                throw new GitException($"无法将所选目录转换为 Git 目录：{folder}");
            directories.Add(relative);
        }

        var (repository, snapshot) = await OpenAsync(root!, token).ConfigureAwait(false);
        var source = snapshot.Entries.FirstOrDefault(entry => Paths.Equals(entry.Path, root) &&
            !entry.IsBare && !entry.IsPrunable);
        if (source is null)
            throw new GitException("无法在 Git 登记中找到所选目录的工作区。");
        return (repository, snapshot, source, directories.Order(StringComparer.Ordinal).ToArray());
    }

    public async Task<WorktreeSnapshot> RefreshAsync(RepositoryLocation repository, CancellationToken token)
    {
        Exception? lastError = null;
        foreach (var path in new[] { repository.MainPath, repository.AccessPath }.Distinct(Paths))
        {
            try
            {
                var common = await CommonDirectoryAsync(path, token).ConfigureAwait(false);
                if (!Paths.Equals(common, repository.CommonDirectory))
                    throw new GitException("此目录现在指向另一个仓库，请重新定位。");

                return await ReadSnapshotAsync(path, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
            {
                lastError = ex;
            }
        }
        throw new GitException($"无法刷新仓库。{lastError?.Message}");
    }

    public async Task<WorktreeDetails> ReadDetailsAsync(
        RepositoryLocation repository, WorktreeEntry entry, CancellationToken token)
    {
        if (entry.IsBare)
            return new WorktreeDetails(WorktreeAvailability.Bare);

        WorktreeAvailability availability;
        try { availability = GetAvailability(entry.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorktreeDetails(WorktreeAvailability.Unavailable, Error: ex.Message);
        }

        if (availability != WorktreeAvailability.Available)
            return new WorktreeDetails(availability);

        try
        {
            await VerifyWorktreeAsync(repository, entry.Path, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
        {
            return new WorktreeDetails(WorktreeAvailability.Unavailable, Error: ex.Message);
        }

        SparseMode mode = SparseMode.Unknown;
        IReadOnlyList<string>? sparsePaths = null;
        ChangeState state = ChangeState.Unknown;
        var errors = new List<string>();

        try
        {
            var sparse = await GetBooleanAsync(entry.Path, "core.sparseCheckout", token).ConfigureAwait(false);
            if (sparse != true)
                mode = SparseMode.Full;
            else
            {
                var cone = await GetBooleanAsync(entry.Path, "core.sparseCheckoutCone", token).ConfigureAwait(false);
                mode = cone == true ? SparseMode.Cone : SparseMode.NonCone;
                var result = await RunAsync(entry.Path, token, "-c", "core.quotePath=false",
                    "sparse-checkout", "list").ConfigureAwait(false);
                sparsePaths = result.RequiredOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r')).ToArray();
            }
        }
        catch (GitException ex) { errors.Add($"稀疏范围：{ex.Message}"); }

        try
        {
            var result = await RunAsync(entry.Path, token, "status", "--porcelain=v1", "-z",
                "--untracked-files=normal", "--ignore-submodules=none").ConfigureAwait(false);
            state = result.RequiredOutput().Length == 0 ? ChangeState.Clean : ChangeState.Modified;
        }
        catch (GitException ex) { errors.Add($"修改状态：{ex.Message}"); }

        return new WorktreeDetails(availability, mode, sparsePaths, state, DateTimeOffset.Now,
            errors.Count == 0 ? null : string.Join(Environment.NewLine, errors));
    }

    internal async Task VerifyWorktreeAsync(RepositoryLocation repository, string path, CancellationToken token)
    {
        var common = await CommonDirectoryAsync(path, token).ConfigureAwait(false);
        if (!Paths.Equals(common, repository.CommonDirectory))
            throw new GitException("此路径不再指向登记的仓库。");

        var top = (await RunAsync(path, token, "rev-parse", "--show-toplevel")
            .ConfigureAwait(false)).RequiredOutput().TrimEnd('\r', '\n');
        if (!Paths.Equals(NormalizePath(top), NormalizePath(path)))
            throw new GitException("此路径不再是登记的工作区根目录。");
    }

    private async Task<WorktreeSnapshot> ReadSnapshotAsync(string path, CancellationToken token)
    {
        var result = await RunAsync(path, token, "worktree", "list", "--porcelain", "-z").ConfigureAwait(false);
        return new WorktreeSnapshot(DateTimeOffset.Now, WorktreePorcelain.Parse(result.RequiredOutput()));
    }

    private async Task<string> CommonDirectoryAsync(string path, CancellationToken token)
    {
        var result = await RunAsync(path, token, "rev-parse", "--path-format=absolute", "--git-common-dir")
            .ConfigureAwait(false);
        return NormalizePath(result.RequiredOutput().TrimEnd('\r', '\n'));
    }

    internal async Task<bool?> GetBooleanAsync(string path, string name, CancellationToken token)
    {
        var result = await RunAsync(path, token, "config", "--type=bool", "--get", name).ConfigureAwait(false);
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Error))
            return null;
        return bool.Parse(result.RequiredOutput().Trim());
    }

    private Task<GitResult> RunAsync(string path, CancellationToken token, params string[] arguments) =>
        GitProcess.RunAsync(installation.Executable, path, token, arguments);

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsAtOrBelow(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static WorktreeAvailability GetAvailability(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0
                ? WorktreeAvailability.Available : WorktreeAvailability.Unavailable;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            var root = Path.GetPathRoot(path);
            if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal) || !new DriveInfo(root).IsReady)
                return WorktreeAvailability.Unavailable;
            return WorktreeAvailability.Missing;
        }
    }
}
