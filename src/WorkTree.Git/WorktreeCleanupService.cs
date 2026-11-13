using System.ComponentModel;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using WorkTree.Core;

namespace WorkTree.Git;

public sealed class WorktreeCleanupService(GitInstallation git)
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly GitRepositoryReader _reader = new(git);

    public async Task<WorktreeRemovalPreview> PreviewRemovalAsync(RepositoryLocation repository,
        string path, string? mergeTarget, CancellationToken token, IProgress<string>? progress = null)
    {
        progress?.Report("正在核对工作区登记…");
        var snapshot = await GetSnapshotAsync(repository, token).ConfigureAwait(false);
        var matches = snapshot.Entries.Where(entry => Paths.Equals(entry.Path, path)).ToArray();
        if (matches.Length != 1 || matches[0] is not { IsMain: false, IsBare: false, IsLocked: false, IsPrunable: false } entry)
            throw new GitException("只能删除可访问、未锁定的关联工作区。请先刷新登记。");

        RequireLocalPath(entry.Path);
        var root = Attributes(entry.Path);
        if (root is null || (root.Value & FileAttributes.Directory) == 0)
            throw new GitException("工作区目录不存在，请使用失效登记清理。");
        if (Overlaps(entry.Path, repository.CommonDirectory) ||
            snapshot.Entries.Any(other => other != entry && Overlaps(entry.Path, other.Path)))
            throw new GitException("工作区目录与其他 Git 工作区或仓库元数据重叠。");

        await _reader.VerifyWorktreeAsync(repository, entry.Path, token).ConfigureAwait(false);
        var admin = (await ReadAsync(entry.Path, token, "rev-parse", "--absolute-git-dir").ConfigureAwait(false))
            .RequiredOutput().TrimEnd('\r', '\n');
        admin = Path.GetFullPath(admin);
        RequireAdminDirectory(repository, admin);
        RequireWorktreeGitFile(entry.Path);
        EnsureNoOperation(admin);
        if (Attributes(Path.Combine(admin, "modules")) is not null)
            throw new GitException("工作区包含已初始化的子模块。请先处理子模块。");
        _ = ScanTree(admin, isWorktree: false, token);
        if (entry.IsDetached)
            await RequireReachableHeadAsync(repository, entry, token).ConfigureAwait(false);

        progress?.Report("正在读取 Git 修改、未跟踪和忽略状态…");
        var status = (await ReadAsync(entry.Path, token, "status", "--porcelain=v1", "-z", "--no-renames",
            "--untracked-files=all", "--ignored=traditional", "--ignore-submodules=none").ConfigureAwait(false)).RequiredOutput();
        var local = ReadLocalFiles(status);
        var uncheckedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexFlags = (await ReadAsync(entry.Path, token, "ls-files", "-v", "-z").ConfigureAwait(false)).RequiredOutput();
        foreach (var item in indexFlags.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (item.Length >= 3 && (char.IsLower(item[0]) || item[0] == 'S'))
                uncheckedPaths.Add(item[2..]);

        progress?.Report("正在盘点文件并检查目录边界…");
        var localPaths = local.Select(item => item.Path).ToHashSet(Paths);
        var inventory = ScanTree(entry.Path, isWorktree: true, token, uncheckedPaths, localPaths, progress);
        local.AddRange(inventory.UncheckedFiles.Select(file => new CleanupFile(file, "Git 跳过修改检查")));
        var localHash = await HashLocalFilesAsync(entry.Path, inventory.FilesToHash, token, progress).ConfigureAwait(false);
        var index = Path.Combine(admin, "index");
        var indexHash = "";
        if (Attributes(index) is not null)
        {
            using var stream = File.OpenRead(index);
            indexHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        }

        progress?.Report("正在核对本地分支…");
        var eligibility = await CheckBranchAsync(repository, snapshot, entry, mergeTarget, token).ConfigureAwait(false);
        return new WorktreeRemovalPreview(entry, admin, local.OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Status, StringComparer.Ordinal).ToArray(), inventory.Count,
            inventory.Hash + ":" + localHash + ":" + indexHash, mergeTarget, eligibility.Commit, eligibility.Allowed, eligibility.Message)
        {
            CanDiscardBranch = BranchUnavailable(snapshot, entry) is null
        };
    }

    public async Task<WorktreeRemovalPreview> RecheckMergeTargetAsync(RepositoryLocation repository,
        WorktreeRemovalPreview preview, string? mergeTarget, CancellationToken token)
    {
        var snapshot = await GetSnapshotAsync(repository, token).ConfigureAwait(false);
        if (!snapshot.Entries.Contains(preview.Entry))
            throw new GitException("工作区的登记或分支已变化，请重新生成预览。");
        var eligibility = await CheckBranchAsync(repository, snapshot, preview.Entry, mergeTarget, token).ConfigureAwait(false);
        return preview with
        {
            MergeTarget = mergeTarget, MergeCommit = eligibility.Commit,
            CanDeleteBranch = eligibility.Allowed, BranchMessage = eligibility.Message,
            CanDiscardBranch = BranchUnavailable(snapshot, preview.Entry) is null
        };
    }

    public Task<CleanupResult> RemoveAsync(RepositoryLocation repository, WorktreeRemovalPreview preview,
        string? discardConfirmation, bool deleteBranch, CancellationToken token, IProgress<string>? progress = null)
        => RemoveAsync(repository, preview, discardConfirmation,
            deleteBranch ? BranchRemovalMode.DeleteIfMerged : BranchRemovalMode.Keep, token, progress);

    public async Task<CleanupResult> RemoveAsync(RepositoryLocation repository, WorktreeRemovalPreview preview,
        string? discardConfirmation, BranchRemovalMode branchMode, CancellationToken token, IProgress<string>? progress = null)
    {
        if (branchMode is not (BranchRemovalMode.Keep or BranchRemovalMode.DeleteIfMerged or BranchRemovalMode.Discard))
            throw new ArgumentOutOfRangeException(nameof(branchMode));
        var mergeTarget = branchMode == BranchRemovalMode.DeleteIfMerged ? preview.MergeTarget : null;
        var current = await PreviewRemovalAsync(repository, preview.Entry.Path, mergeTarget, token, progress).ConfigureAwait(false);
        if (current.Entry != preview.Entry || current.Registration != preview.Registration
            || current.Fingerprint != preview.Fingerprint || !current.LocalFiles.SequenceEqual(preview.LocalFiles))
            throw new GitException("工作区的登记、索引或文件已变化。请重新生成预览。");
        if ((current.LocalFiles.Count > 0 || branchMode == BranchRemovalMode.Discard) &&
            !string.Equals(discardConfirmation, current.ConfirmationName, StringComparison.Ordinal))
            throw new GitException("请输入工作区名称，确认丢弃预览中的内容。");
        if (branchMode == BranchRemovalMode.DeleteIfMerged &&
            (!preview.CanDeleteBranch || !current.CanDeleteBranch || preview.MergeCommit != current.MergeCommit))
            throw new GitException("分支的合入状态已变化。请重新生成预览。");
        if (branchMode == BranchRemovalMode.Discard && (!preview.CanDiscardBranch || !current.CanDiscardBranch))
            throw new GitException("无法删除此本地分支。请重新生成预览。");

        token.ThrowIfCancellationRequested();
        EnsureNoOperation(current.Registration);
        progress?.Report($"正在删除磁盘文件… 0/{current.FileCount:N0}");
        try
        {
            DeleteWorktreeFiles(current, progress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GitException($"磁盘文件删除中断，Git 登记和分支已保留。部分文件可能已经删除。{Environment.NewLine}{ex.Message}");
        }

        progress?.Report("正在清理 Git 登记…");
        var beforeUnregister = await GetSnapshotAsync(repository, CancellationToken.None).ConfigureAwait(false);
        if (!beforeUnregister.Entries.Contains(current.Entry))
            throw new GitException("磁盘文件已删除，但工作区登记或分支发生了变化。请重新刷新并检查。");
        RequireLocalPath(current.Entry.Path);
        RequireAdminDirectory(repository, current.Registration);
        EnsureNoOperation(current.Registration);
        RequireWorktreeGitFile(current.Entry.Path);
        await _reader.VerifyWorktreeAsync(repository, current.Entry.Path, CancellationToken.None).ConfigureAwait(false);
        if (Directory.EnumerateFileSystemEntries(current.Entry.Path).Any(path => !Paths.Equals(Path.GetFileName(path), ".git")))
            throw new GitException("删除期间工作区出现了新文件，Git 登记和分支已保留。请重新生成预览。");
        // The checkout has already been emptied. One --force bypasses Git's missing-file status check
        // while still letting Git validate the registration and reject a locked worktree.
        var result = await WriteAsync(repository.MainPath, "worktree", "remove", "--force", "--", current.Entry.Path)
            .ConfigureAwait(false);
        var after = await GetSnapshotAsync(repository, CancellationToken.None).ConfigureAwait(false);
        var registered = after.Entries.Any(entry => Paths.Equals(entry.Path, current.Entry.Path));
        if (registered)
            throw new GitException("工作区登记仍存在。删除可能只完成了一部分，请检查实际目录。" +
                Environment.NewLine + result.Error.Trim());
        if (result.ExitCode != 0 || Attributes(current.Entry.Path) is not null)
            return new CleanupResult([current.Entry.Path], "工作区登记已移除，部分文件可能仍在原路径。本地分支已保留。" +
                Environment.NewLine + result.Error.Trim(), true);

        if (branchMode == BranchRemovalMode.Keep)
            return new CleanupResult([current.Entry.Path], "工作区已删除，本地分支已保留。");

        try
        {
            progress?.Report("正在复核并删除本地分支…");
            var branch = current.Entry.Branch!;
            if (branchMode == BranchRemovalMode.DeleteIfMerged)
            {
                var eligibility = await CheckBranchAsync(repository, after, current.Entry, current.MergeTarget, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!eligibility.Allowed || eligibility.Commit != current.MergeCommit)
                    return new CleanupResult([current.Entry.Path], $"工作区已删除，本地分支已保留。{eligibility.Message}", true);
            }
            else
            {
                var unavailable = BranchUnavailable(after, current.Entry);
                var tip = await ResolveCommitAsync(repository.MainPath, "refs/heads/" + branch, CancellationToken.None)
                    .ConfigureAwait(false);
                if (unavailable is not null || tip != current.Entry.Head)
                    return new CleanupResult([current.Entry.Path],
                        $"工作区已删除，本地分支已保留。{unavailable ?? "分支提交已变化。"}", true);
            }

            var flag = branchMode == BranchRemovalMode.Discard ? "-D" : "-d";
            var deletion = await WriteAsync(repository.MainPath, "--no-replace-objects", "branch", flag, "--", branch)
                .ConfigureAwait(false);
            if (deletion.ExitCode == 0)
                return new CleanupResult([current.Entry.Path], $"工作区和本地分支 {current.Entry.Branch} 已删除。");
            return new CleanupResult([current.Entry.Path], "工作区已删除，Git 保留了本地分支。" +
                Environment.NewLine + deletion.Error.Trim(), true);
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new CleanupResult([current.Entry.Path], $"工作区已删除，无法完成本地分支清理。{ex.Message}", true);
        }
    }

    private static void DeleteWorktreeFiles(WorktreeRemovalPreview preview, IProgress<string>? progress)
    {
        var root = preview.Entry.Path;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        var completed = 0;
        DeleteContents(root);

        void DeleteContents(string directory)
        {
            if (Attributes(directory) is not { } attributes ||
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException($"工作区目录已变化：{directory}");

            var children = new FileSystemEnumerable<(string Path, FileAttributes Attributes)>(directory,
                (ref FileSystemEntry child) => (child.ToFullPath(), child.Attributes), options).ToArray();
            var isRoot = Paths.Equals(directory, root);
            if (!isRoot && (children.Any(child => Paths.Equals(Path.GetFileName(child.Path), ".git")) ||
                IsPossibleBareRepository(directory)))
                throw new IOException($"检测到嵌套仓库：{directory}");

            foreach (var child in children)
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"路径中包含链接：{child.Path}");
                if (isRoot && Paths.Equals(Path.GetFileName(child.Path), ".git"))
                    continue;
                if ((child.Attributes & FileAttributes.Directory) != 0)
                {
                    DeleteContents(child.Path);
                    Directory.Delete(child.Path);
                    continue;
                }

                if ((child.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    try { File.Delete(child.Path); }
                    catch (UnauthorizedAccessException)
                    {
                        File.SetAttributes(child.Path, child.Attributes & ~FileAttributes.ReadOnly);
                        File.Delete(child.Path);
                    }
                }
                else
                {
                    File.Delete(child.Path);
                }

                completed++;
                if (completed % 512 == 0)
                    progress?.Report($"正在删除磁盘文件… {completed:N0}/{preview.FileCount:N0}");
            }
        }
    }

    public async Task<PrunePreview> PreviewPruneAsync(RepositoryLocation repository, CancellationToken token)
    {
        var snapshot = await GetSnapshotAsync(repository, token).ConfigureAwait(false);
        if (snapshot.Entries.GroupBy(entry => entry.Path, Paths).Any(group => group.Count() > 1))
            throw new GitException("发现重复的工作区路径，请先修复登记。");

        var dryRun = await ReadAsync(repository.MainPath, token, "worktree", "prune", "--dry-run", "--verbose", "--expire", "now")
            .ConfigureAwait(false);
        dryRun.RequiredOutput();
        var entries = new List<StaleRegistration>();
        var ids = new HashSet<string>(Paths);
        foreach (var line in (dryRun.Output + "\n" + dryRun.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "Removing worktrees/";
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || separator <= prefix.Length)
                throw new GitException($"无法核对 Git 清理预演：{line.TrimEnd()}");
            var id = line[prefix.Length..separator];
            if (!SparseWorktreeService.IsValidWindowsName(id) || !ids.Add(id))
                throw new GitException("Git 预演返回了无效或重复的登记名称。");
            var admin = Path.Combine(repository.CommonDirectory, "worktrees", id);
            RequireAdminDirectory(repository, admin);
            if (Attributes(Path.Combine(admin, "gitdir")) is not { } gitDirFile ||
                (gitDirFile & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new GitException($"无法确认失效登记 {id} 指向的工作区。");
            var target = (await File.ReadAllTextAsync(Path.Combine(admin, "gitdir"), token).ConfigureAwait(false)).TrimEnd('\r', '\n');
            var gitPath = Path.GetFullPath(target, admin);
            if (!Paths.Equals(Path.GetFileName(gitPath), ".git") || Path.GetDirectoryName(gitPath) is not { } directory)
                throw new GitException($"无法确认失效登记 {id} 的工作区路径。");

            RequireLocalPath(directory);
            var entry = snapshot.Entries.SingleOrDefault(item => Paths.Equals(item.Path, directory));
            if (entry is null || entry.IsMain || entry.IsBare || entry.IsLocked || Attributes(directory) is not null
                || Overlaps(directory, repository.CommonDirectory))
                throw new GitException($"无法确认该登记对应的目录确实缺失：{directory}。本次清理已暂停。");
            EnsureNoOperation(admin);
            if (Attributes(Path.Combine(admin, "modules")) is not null)
                throw new GitException($"失效登记仍包含子模块数据：{directory}。");
            if (entry.IsDetached)
                await RequireReachableHeadAsync(repository, entry, token).ConfigureAwait(false);
            _ = ScanTree(admin, isWorktree: false, token);
            var staged = await ReadAsync(repository.MainPath, token, "--git-dir=" + admin,
                "diff", "--cached", "--quiet", "--no-ext-diff", "HEAD", "--").ConfigureAwait(false);
            if (staged.ExitCode == 1)
                throw new GitException($"此失效登记仍有已暂存的修改，请先恢复工作区：{directory}");
            staged.RequiredOutput();
            entries.Add(new StaleRegistration(entry, id));
        }
        return new PrunePreview(entries.OrderBy(entry => entry.Registration, StringComparer.Ordinal).ToArray());
    }

    public async Task<CleanupResult> PruneAsync(RepositoryLocation repository, PrunePreview preview, CancellationToken token)
    {
        var current = await PreviewPruneAsync(repository, token).ConfigureAwait(false);
        if (current.Entries.Count == 0 || !current.Entries.SequenceEqual(preview.Entries))
            throw new GitException("待清理登记已变化，请重新预览。");
        token.ThrowIfCancellationRequested();
        var result = await WriteAsync(repository.MainPath, "worktree", "prune", "--verbose", "--expire", "now").ConfigureAwait(false);
        var after = await GetSnapshotAsync(repository, CancellationToken.None).ConfigureAwait(false);
        var removed = current.Entries.Where(stale => after.Entries.All(entry => !Paths.Equals(entry.Path, stale.Entry.Path)))
            .Select(stale => stale.Entry.Path).ToArray();
        if (removed.Length != current.Entries.Count || result.ExitCode != 0)
            return new CleanupResult(removed, $"已清理 {removed.Length} 条登记，部分登记仍需检查。" +
                Environment.NewLine + result.Error.Trim(), true);
        return new CleanupResult(removed, $"已清理 {removed.Length} 条失效登记。");
    }

    private async Task<WorktreeSnapshot> GetSnapshotAsync(RepositoryLocation repository, CancellationToken token)
    {
        var snapshot = await _reader.RefreshAsync(repository, token).ConfigureAwait(false);
        if (snapshot.Entries[0].IsBare || !Paths.Equals(snapshot.Entries[0].Path, repository.MainPath))
            throw new GitException("请从可访问的主工作区执行清理。");
        RequireLocalPath(repository.MainPath);
        RequireLocalPath(repository.CommonDirectory);
        await _reader.VerifyWorktreeAsync(repository, repository.MainPath, token).ConfigureAwait(false);
        return snapshot;
    }

    private async Task<(bool Allowed, string? Commit, string Message)> CheckBranchAsync(RepositoryLocation repository,
        WorktreeSnapshot snapshot, WorktreeEntry entry, string? mergeTarget, CancellationToken token)
    {
        if (BranchUnavailable(snapshot, entry) is { } unavailable)
            return (false, null, unavailable);
        if (mergeTarget is null)
            return (false, null, "选择一个目标分支，用于检查当前分支是否已合入。");
        var branch = entry.Branch!;
        var head = entry.Head!;
        var reference = "refs/heads/" + branch;
        if (mergeTarget == reference || !(mergeTarget.StartsWith("refs/heads/", StringComparison.Ordinal) ||
            mergeTarget.StartsWith("refs/remotes/", StringComparison.Ordinal)))
            return (false, null, "合入目标必须是另一条本地分支或已缓存的远程分支。");
        var exactTarget = await ReadAsync(repository.MainPath, token, "show-ref", "--verify", "--quiet", mergeTarget)
            .ConfigureAwait(false);
        if (exactTarget.ExitCode != 0)
            return (false, null, "找不到所配置的合入目标分支。");
        var tip = await ResolveCommitAsync(repository.MainPath, reference, token).ConfigureAwait(false);
        var target = await ResolveCommitAsync(repository.MainPath, mergeTarget, token).ConfigureAwait(false);
        if (tip != head || target is null)
            return (false, null, "分支提交已变化或合入目标已不存在。");
        if (!await IsAncestorAsync(repository.MainPath, head, target, token).ConfigureAwait(false))
            return (false, target, "无法证明此分支已合入所配置的目标。");

        var upstream = await ResolveCommitAsync(repository.MainPath, reference + "@{upstream}", token).ConfigureAwait(false);
        var gitTarget = upstream ?? snapshot.Entries[0].Head;
        if (gitTarget is null || !await IsAncestorAsync(repository.MainPath, head, gitTarget, token).ConfigureAwait(false))
            return (false, target, "已合入所配置的目标，Git 的安全删除检查仍要求合入上游分支或主工作区当前提交。");
        return (true, target, $"此分支已合入 {mergeTarget}，可一并删除本地分支。");
    }

    private static string? BranchUnavailable(WorktreeSnapshot snapshot, WorktreeEntry entry)
    {
        if (entry.Branch is not { } branch || entry.Head is null)
            return "此工作区没有可删除的本地分支。";
        return snapshot.Entries.Any(other => !Paths.Equals(other.Path, entry.Path) && other.Branch == branch)
            ? "此分支仍被其他工作区使用。"
            : null;
    }

    private async Task RequireReachableHeadAsync(RepositoryLocation repository, WorktreeEntry entry, CancellationToken token)
    {
        if (entry.Head is not { } head)
            throw new GitException($"无法确认游离提交已保存：{entry.Path}");
        var refs = await ReadAsync(repository.MainPath, token, "--no-replace-objects", "for-each-ref",
            "--contains=" + head, "--count=1", "--format=%(refname)", "refs/heads", "refs/remotes", "refs/tags")
            .ConfigureAwait(false);
        if (refs.RequiredOutput().Trim().Length == 0)
            throw new GitException($"请先用分支或标签保存此工作区的游离提交：{head}");
    }

    private async Task<bool> IsAncestorAsync(string path, string head, string target, CancellationToken token)
    {
        var check = await ReadAsync(path, token, "--no-replace-objects", "merge-base", "--is-ancestor", head, target)
            .ConfigureAwait(false);
        if (check.ExitCode == 1)
            return false;
        check.RequiredOutput();
        return true;
    }

    private async Task<string?> ResolveCommitAsync(string path, string reference, CancellationToken token)
    {
        var result = await ReadAsync(path, token, "rev-parse", "--verify", "--quiet", "--end-of-options",
            reference + "^{commit}").ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    private static List<CleanupFile> ReadLocalFiles(string status)
    {
        var local = new List<CleanupFile>();
        foreach (var item in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Length < 4 || item[2] != ' ')
                throw new GitException("无法读取完整的工作区状态。");
            var category = item[..2] switch
            {
                "??" => "未跟踪",
                "!!" => "已忽略",
                _ when item[0] != ' ' && item[1] != ' ' => "已暂存且有工作区修改",
                _ when item[0] != ' ' => "已暂存",
                _ => "已修改"
            };
            local.Add(new CleanupFile(item[3..], category));
        }
        return local;
    }

    private static void EnsureNoOperation(string admin)
    {
        foreach (var marker in new[] { "locked", "index.lock", "HEAD.lock", "MERGE_HEAD", "CHERRY_PICK_HEAD",
                     "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_START", "sequencer" })
            if (Attributes(Path.Combine(admin, marker)) is not null)
                throw new GitException("此登记已锁定或正在执行 Git 操作，请先完成该操作。");
    }

    private static void RequireAdminDirectory(RepositoryLocation repository, string admin)
    {
        var parent = Path.Combine(repository.CommonDirectory, "worktrees");
        if (!Paths.Equals(Path.GetDirectoryName(admin), parent))
            throw new GitException("工作区登记不在预期的 Git 元数据目录内。");
        RequireLocalPath(admin);
        if (Attributes(admin) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
            throw new GitException("找不到工作区登记目录。");
    }

    private static void RequireWorktreeGitFile(string path)
    {
        if (Attributes(Path.Combine(path, ".git")) is not { } attributes ||
            (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new GitException("关联工作区的 .git 文件无效。");
    }

    private static void RequireLocalPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new GitException($"清理只支持可访问的本机固定磁盘路径：{path}");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            throw new GitException($"无法确认磁盘在线且为本机固定磁盘：{path}");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Attributes(current) is { } attributes &&
                ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0))
                throw new GitException($"清理路径经过链接或非文件夹路径：{current}");
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);

    private static bool Contains(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsPossibleBareRepository(string directory) =>
        Attributes(Path.Combine(directory, "HEAD")) is not null &&
        Attributes(Path.Combine(directory, "objects")) is not null &&
        (Attributes(Path.Combine(directory, "refs")) is not null ||
            Attributes(Path.Combine(directory, "packed-refs")) is not null);

    private static (int Count, string Hash, List<string> UncheckedFiles, List<string> FilesToHash) ScanTree(
        string root, bool isWorktree, CancellationToken token, HashSet<string>? uncheckedPaths = null,
        HashSet<string>? localPaths = null, IProgress<string>? progress = null)
    {
        var records = new List<string>();
        var uncheckedFiles = new List<string>();
        var filesToHash = new List<string>();
        var pending = new Stack<string>();
        var prefixLength = Path.TrimEndingDirectorySeparator(root).Length + 1;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (isWorktree && directory != root && IsPossibleBareRepository(directory))
                throw new GitException($"检测到可能的嵌套裸仓库：{directory}");

            // Windows directory enumeration already returns the attributes, times and size.
            var children = new FileSystemEnumerable<DirectoryItem>(directory, (ref FileSystemEntry entry) =>
                new DirectoryItem(entry.ToFullPath(), entry.Attributes, entry.Length,
                    entry.LastWriteTimeUtc.Ticks, entry.CreationTimeUtc.Ticks), options);
            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new GitException($"路径中包含链接，无法安全清理：{child.Path}");
                var relative = child.Path[prefixLength..];
                if (isWorktree && Paths.Equals(Path.GetFileName(child.Path), ".git") && directory != root)
                    throw new GitException($"检测到嵌套仓库或子模块：{child.Path}");
                var gitPath = localPaths is not null ? relative.Replace('\\', '/') : relative;
                var skippedByGit = uncheckedPaths?.Contains(gitPath) == true;
                if (skippedByGit)
                    uncheckedFiles.Add(gitPath);
                if ((child.Attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child.Path);
                    records.Add(relative + "\0D");
                    continue;
                }

                if (!isWorktree || !Paths.Equals(relative, ".git"))
                {
                    count++;
                    if (count % 4096 == 0)
                        progress?.Report($"正在盘点文件… 已检查 {count:N0} 个");
                }
                if (skippedByGit || localPaths?.Contains(gitPath) == true)
                    filesToHash.Add(gitPath);
                records.Add($"{relative}\0{child.Length}\0{child.LastWriteTicks}\0{child.CreationTicks}");
            }
        }

        records.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in records)
            hash.AppendData(Encoding.UTF8.GetBytes(item + "\n"));
        return (count, Convert.ToHexString(hash.GetHashAndReset()), uncheckedFiles, filesToHash);
    }

    private readonly record struct DirectoryItem(string Path, FileAttributes Attributes, long Length,
        long LastWriteTicks, long CreationTicks);

    private static async Task<string> HashLocalFilesAsync(string root, IEnumerable<string> files,
        CancellationToken token, IProgress<string>? progress)
    {
        var paths = files.Order(StringComparer.Ordinal).ToArray();
        if (paths.Length > 0)
            progress?.Report($"正在校验本地内容… 共 {paths.Length:N0} 项");
        var digests = new byte[paths.Length][];
        var completed = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, (index, cancellation) =>
        {
            digests[index] = HashLocalFile(root, paths[index], cancellation);
            var count = Interlocked.Increment(ref completed);
            if (count % 512 == 0)
                progress?.Report($"正在校验本地内容… {count:N0}/{paths.Length:N0}");
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < paths.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(paths[i] + "\0"));
            hash.AppendData(digests[i]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] HashLocalFile(string root, string relative, CancellationToken token)
    {
        // Paths come from the inventory, which rejects links before any file is opened.
        using var stream = new FileStream(Path.Combine(root, relative), FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 64 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer.AsSpan(0, read));
            }
            return hash.GetHashAndReset();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private Task<GitResult> ReadAsync(string path, CancellationToken token, params string[] args) =>
        GitProcess.RunAsync(git.Executable, path, null, TimeSpan.FromMinutes(5), token, args);

    private Task<GitResult> WriteAsync(string path, params string[] args) =>
        GitProcess.RunAsync(git.Executable, path, null, Timeout.InfiniteTimeSpan, CancellationToken.None, args);
}
