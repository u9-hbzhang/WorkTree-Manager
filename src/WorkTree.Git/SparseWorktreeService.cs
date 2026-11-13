using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using WorkTree.Core;

namespace WorkTree.Git;

public sealed class SparseWorktreeService(GitInstallation installation)
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly GitRepositoryReader _reader = new(installation);

    public async Task<BranchChoice[]> ListBranchesAsync(RepositoryLocation repository, CancellationToken token)
    {
        var snapshot = await _reader.RefreshAsync(repository, token).ConfigureAwait(false);
        await RequireMainWorktreeAsync(repository, snapshot, token).ConfigureAwait(false);
        var result = await RunAsync(repository.MainPath, token, "for-each-ref",
            "--format=%(refname)%00%(symref)", "refs/heads", "refs/remotes", "refs/tags").ConfigureAwait(false);
        var occupied = snapshot.Entries.Where(entry => entry.Branch is not null)
            .ToLookup(entry => entry.Branch!, StringComparer.Ordinal);
        var branches = new List<BranchChoice>();
        foreach (var line in result.RequiredOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\0');
            if (fields.Length != 2 || fields[1].Length != 0)
                continue;
            var reference = fields[0];
            var local = reference.StartsWith("refs/heads/", StringComparison.Ordinal);
            var name = reference[(reference.IndexOf('/', 5) + 1)..];
            var label = reference.StartsWith("refs/tags/", StringComparison.Ordinal) ? $"标签：{name}" : name;
            branches.Add(new BranchChoice(reference, label, local, local ? occupied[name].FirstOrDefault()?.Path : null));
        }
        return [.. branches];
    }

    public async Task<string> ResolveCommitAsync(string path, string revision, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.Any(char.IsControl))
            throw new GitException("请选择起始分支或填写提交。");
        var result = await RunAsync(path, token, "rev-parse", "--verify", "--end-of-options",
            revision + "^{commit}").ConfigureAwait(false);
        var commit = result.RequiredOutput().Trim();
        if (!IsObjectId(commit))
            throw new GitException("Git 未返回有效的提交 ID。");
        return commit;
    }

    public async Task<string[]> ListDirectoriesAsync(string repositoryPath, string commit, CancellationToken token)
    {
        RequireObjectId(commit);
        var result = await RunAsync(repositoryPath, token, "ls-tree", "--full-tree", "-r", "-d",
            "-z", "--name-only", commit).ConfigureAwait(false);
        return result.RequiredOutput().Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(ConeScope.IsDirectoryPath).ToArray();
    }

    public async Task<ConeScope> ReadScopeAsync(RepositoryLocation repository, WorktreeEntry entry, CancellationToken token)
    {
        if (entry.IsBare || entry.IsPrunable)
            throw new GitException("无法读取此工作区的稀疏目录。");
        await _reader.VerifyWorktreeAsync(repository, entry.Path, token).ConfigureAwait(false);
        if (await _reader.GetBooleanAsync(entry.Path, "core.sparseCheckout", token).ConfigureAwait(false) != true ||
            await _reader.GetBooleanAsync(entry.Path, "core.sparseCheckoutCone", token).ConfigureAwait(false) != true)
            throw new GitException("所选工作区没有启用 cone 稀疏检出。");

        var result = await RunAsync(entry.Path, token, "-c", "core.quotePath=false", "sparse-checkout", "list")
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Error))
            throw new GitException($"Git 无法按 cone 模式读取此范围。{Environment.NewLine}{result.Error.Trim()}");
        return new ConeScope(result.RequiredOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')));
    }

    public async Task<SparseWorktreeResult> CreateAsync(RepositoryLocation repository, CreateSparseWorktree request,
        IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("正在检查分支、目录和目标路径…");
        RequireObjectId(request.Commit);
        if (!Path.IsPathFullyQualified(request.Destination))
            throw new GitException("请填写工作区的完整目标路径。");

        if (request.Destination.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            request.Destination.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new GitException("请使用普通的 Windows 文件夹路径。");
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Destination));
        if (Path.GetDirectoryName(destination) is not { } parent || !IsValidWindowsName(Path.GetFileName(destination)))
            throw new GitException("目标文件夹名称无效。");

        var snapshot = await _reader.RefreshAsync(repository, token).ConfigureAwait(false);
        await RequireMainWorktreeAsync(repository, snapshot, token).ConfigureAwait(false);
        if (snapshot.Entries.Any(entry => IsAtOrBelow(destination, entry.Path) || IsAtOrBelow(entry.Path, destination)))
            throw new GitException("目标目录与已登记的工作区重叠。");

        if (request.Branch.StartsWith('-') || request.Branch.StartsWith("refs/", StringComparison.Ordinal))
            throw new GitException("请输入有效的本地分支名称。");
        var branchCheck = await RunAsync(repository.MainPath, token, "check-ref-format",
            "refs/heads/" + request.Branch).ConfigureAwait(false);
        if (branchCheck.ExitCode != 0)
            throw new GitException("请输入有效的本地分支名称。");
        var fullBranch = "refs/heads/" + request.Branch;
        var branch = await RunAsync(repository.MainPath, token, "show-ref", "--verify", "--quiet", fullBranch)
            .ConfigureAwait(false);
        if (branch.ExitCode is not (0 or 1))
            branch.RequiredOutput();

        if (request.ExistingBranch)
        {
            if (branch.ExitCode != 0)
                throw new GitException("所选本地分支已不存在。");
            if (snapshot.Entries.FirstOrDefault(entry => entry.Branch == request.Branch) is { } occupied)
                throw new GitException($"分支已在此工作区检出：{occupied.Path}");
            var head = await ResolveCommitAsync(repository.MainPath, fullBranch, token).ConfigureAwait(false);
            if (head != request.Commit)
                throw new GitException("分支已有新提交，请重新读取目录树。");
        }
        else if (branch.ExitCode == 0)
            throw new GitException("同名本地分支已存在。");

        await ValidateDirectoriesAsync(repository.MainPath, request.Commit, request.Scope, token).ConfigureAwait(false);
        RequireUnoccupiedPath(destination, parent);
        token.ThrowIfCancellationRequested();

        var stage = "创建工作区登记";
        try
        {
            Directory.CreateDirectory(parent);
            RequireUnoccupiedPath(destination, parent);
            await _reader.VerifyWorktreeAsync(repository, repository.MainPath, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            progress?.Report("正在建立工作区登记…");
            var args = request.ExistingBranch
                ? new[] { "worktree", "add", "--no-checkout", "--no-guess-remote", "--", destination, request.Branch }
                : ["worktree", "add", "--no-checkout", "--no-track", "-b", request.Branch, "--", destination, request.Commit];
            (await WriteAsync(repository.MainPath, null, args).ConfigureAwait(false)).RequiredOutput();

            stage = "核对工作区与分支";
            await _reader.VerifyWorktreeAsync(repository, destination, CancellationToken.None).ConfigureAwait(false);
            var head = await ResolveCommitAsync(destination, "HEAD", CancellationToken.None).ConfigureAwait(false);
            var symbolic = await RunAsync(destination, CancellationToken.None, "symbolic-ref", "HEAD").ConfigureAwait(false);
            if (head != request.Commit || symbolic.RequiredOutput().Trim() != fullBranch)
                throw new GitException("新工作区的分支或提交与预览不一致。");

            stage = "设置 cone 目录";
            progress?.Report("正在设置 cone 目录…");
            var sparse = await SetScopeAsync(destination, request.Scope).ConfigureAwait(false);
            stage = "检出所选文件";
            progress?.Report("正在检出所选目录的文件…");
            var checkout = await WriteAsync(destination, null, "-c", "checkout.workers=8", "-c", "submodule.recurse=false",
                "read-tree", "-m", "-u", "HEAD").ConfigureAwait(false);
            checkout.RequiredOutput();

            stage = "验证检出结果";
            var entry = new WorktreeEntry(destination, head, request.Branch, false, false, false, false, null, false, null);
            await VerifyScopeAsync(repository, entry, request.Scope).ConfigureAwait(false);
            var details = new WorktreeDetails(WorktreeAvailability.Available, SparseMode.Cone, request.Scope.Directories);
            return new SparseWorktreeResult(entry, details, CombineWarnings(sparse.Error, checkout.Error));
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new GitException($"{stage}失败。请检查目标 {destination} 和分支 {request.Branch}，已生成的内容会保留。{Environment.NewLine}{ex.Message}");
        }
    }

    public async Task<SparseChangePreview> PreviewAsync(RepositoryLocation repository, WorktreeEntry entry,
        ConeScope scope, CancellationToken token)
    {
        var snapshot = await _reader.RefreshAsync(repository, token).ConfigureAwait(false);
        var current = snapshot.Entries.FirstOrDefault(item => Paths.Equals(item.Path, entry.Path));
        if (current is null || current.IsMain || current.IsBare || current.IsLocked || current.IsPrunable)
            throw new GitException("只能调整可访问且未锁定的关联 cone 工作区。");

        var before = await ReadScopeAsync(repository, current, token).ConfigureAwait(false);
        var commit = await ResolveCommitAsync(entry.Path, "HEAD", token).ConfigureAwait(false);
        await ValidateDirectoriesAsync(entry.Path, commit, scope, token).ConfigureAwait(false);
        var gitDir = (await RunAsync(entry.Path, token, "rev-parse", "--absolute-git-dir").ConfigureAwait(false))
            .RequiredOutput().TrimEnd('\r', '\n');
        foreach (var marker in new[] { "locked", "index.lock", "HEAD.lock", "MERGE_HEAD", "CHERRY_PICK_HEAD",
                     "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_START", "sequencer" })
            if (Exists(Path.Combine(gitDir, marker)))
                throw new GitException("此工作区正在执行合并、变基或其他 Git 操作，请完成后重试。");

        var status = (await ReadLongAsync(entry.Path, token, "status", "--porcelain=v1", "-z", "--no-renames",
            "--untracked-files=all", "--ignored=traditional", "--ignore-submodules=none").ConfigureAwait(false)).RequiredOutput();
        var expands = scope.Directories.Any(path => !before.ContainsDirectory(path));
        var windowsScope = new ConeScope(scope.Directories.Select(path => path.ToUpperInvariant()));
        var blockers = new HashSet<string>(StringComparer.Ordinal);
        var restore = new HashSet<string>(Paths);
        var delete = new HashSet<string>(Paths);
        var affectedStatus = new List<string>();
        foreach (var record in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record.Length < 4)
                throw new GitException("无法读取完整的工作区状态。");
            var code = record[..2];
            var path = record[3..];
            if (code.Contains('U') || code is "AA" or "DD")
                throw new GitException("请先解决此工作区的 Git 冲突。");
            var directory = path.EndsWith('/');
            var included = directory ? scope.ContainsDirectory(path.TrimEnd('/')) : scope.ContainsFile(path);
            var previouslyIncluded = directory ? before.ContainsDirectory(path.TrimEnd('/')) : before.ContainsFile(path);
            // A file at a newly included path can collide with checkout.
            if (!included && previouslyIncluded && !directory && !windowsScope.ContainsFile(path.ToUpperInvariant()))
            {
                (code is "!!" or "??" ? delete : restore).Add(path);
                affectedStatus.Add(record);
            }
            else if (!included || (expands && code is ("!!" or "??") && !previouslyIncluded))
            {
                blockers.Add(path);
            }
        }

        var tracked = (await ReadLongAsync(entry.Path, token, "ls-files", "--stage", "-v", "-z")
            .ConfigureAwait(false)).RequiredOutput();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var affectedIndex = new List<string>();
        var indexPaths = new List<(string Path, bool IsSpecial)>();
        foreach (var record in tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 2)
                throw new GitException("无法读取完整的 Git 索引。");
            var path = record[(tab + 1)..];
            if (restore.Count > 0)
                indexPaths.Add((path, record.AsSpan(2).StartsWith("160000 ", StringComparison.Ordinal) ||
                    record.AsSpan(2).StartsWith("120000 ", StringComparison.Ordinal)));
            if (restore.Contains(path) || delete.Contains(path))
                affectedIndex.Add(record);
            if (scope.ContainsFile(path))
                continue;
            if ((char.IsLower(record[0]) || record[0] == 'S') && Exists(Path.Combine(entry.Path, path)))
                blockers.Add(path + "（Git 跳过修改检查）");
            if (record.AsSpan(2).StartsWith("160000 ", StringComparison.Ordinal))
            {
                if (Exists(Path.Combine(entry.Path, path, ".git")))
                    blockers.Add(path + "/（已初始化的子模块）");
            }
            else if (record[0] != 'S' && Exists(Path.Combine(entry.Path, path)))
                removed.Add(path);
        }

        CheckDirectoryBoundaries(entry.Path, before, scope, blockers, token);
        delete.ExceptWith(restore);
        var discard = restore.Concat(delete).Order(StringComparer.Ordinal).ToArray();
        if (restore.Count > 0)
        {
            CheckRestoreBoundaries(restore, indexPaths, blockers);
            var headPaths = (await ReadLongAsync(entry.Path, token, "ls-tree", "--full-tree", "-r", "-z", commit)
                .ConfigureAwait(false)).RequiredOutput();
            CheckRestoreBoundaries(restore, headPaths.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(record =>
            {
                var tab = record.IndexOf('\t');
                if (tab < 0)
                    throw new GitException("无法读取提交中的文件路径。");
                return (record[(tab + 1)..], record.StartsWith("160000 ", StringComparison.Ordinal) ||
                    record.StartsWith("120000 ", StringComparison.Ordinal));
            }), blockers);
        }
        foreach (var path in discard)
        {
            if (!IsSafeDiscardPath(entry.Path, path, allowMissing: restore.Contains(path)))
                blockers.Add(path + "（无法核实为普通文件）");
        }
        var fingerprint = blockers.Count == 0 && discard.Length > 0
            ? await FingerprintDiscardAsync(entry.Path, discard, affectedStatus, affectedIndex, token).ConfigureAwait(false)
            : "";
        return new SparseChangePreview(current, commit, before, scope,
            removed.Order(StringComparer.Ordinal).ToArray(),
            blockers.Concat(discard).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        {
            DiscardableFiles = discard,
            BlockingFiles = blockers.Order(StringComparer.Ordinal).ToArray(),
            RestorePaths = restore.Order(StringComparer.Ordinal).ToArray(),
            DeletePaths = delete.Order(StringComparer.Ordinal).ToArray(),
            DiscardFingerprint = fingerprint
        };
    }

    public async Task<SparseWorktreeResult> ApplyAsync(RepositoryLocation repository, SparseChangePreview preview,
        IProgress<string>? progress, CancellationToken token, string? discardConfirmation = null)
    {
        if (preview.BlockingFiles.Count > 0)
            throw new GitException("请先处理无法在此处丢弃的本地内容。");
        if (preview.ProtectedFiles.Count > 0 && (preview.DiscardableFiles.Count == 0 ||
            !string.Equals(discardConfirmation, preview.ConfirmationName, StringComparison.Ordinal)))
            throw new GitException("请输入工作区名称，确认丢弃将移出范围的本地内容。");

        progress?.Report("正在复核工作区和受影响文件…");
        var current = await PreviewAsync(repository, preview.Entry, preview.After, token).ConfigureAwait(false);
        if (current.Commit != preview.Commit || current.Entry != preview.Entry
            || !current.Before.Directories.SequenceEqual(preview.Before.Directories)
            || !current.RemovedFiles.SequenceEqual(preview.RemovedFiles)
            || current.BlockingFiles.Count > 0
            || !current.ProtectedFiles.SequenceEqual(preview.ProtectedFiles)
            || !current.RestorePaths.SequenceEqual(preview.RestorePaths)
            || !current.DeletePaths.SequenceEqual(preview.DeletePaths)
            || current.DiscardFingerprint != preview.DiscardFingerprint)
            throw new GitException("工作区或受影响文件已变化，请重新预览。");
        token.ThrowIfCancellationRequested();

        try
        {
            if (current.DiscardableFiles.Count > 0)
            {
                progress?.Report("正在丢弃移出范围内的本地内容…");
                await DiscardAsync(current).ConfigureAwait(false);
                progress?.Report("正在确认移出范围内的本地内容已处理…");
                var afterDiscard = await PreviewAsync(repository, current.Entry, current.After, CancellationToken.None)
                    .ConfigureAwait(false);
                if (afterDiscard.Entry != current.Entry || afterDiscard.Commit != current.Commit ||
                    !afterDiscard.Before.Directories.SequenceEqual(current.Before.Directories) ||
                    afterDiscard.ProtectedFiles.Count > 0)
                    throw new GitException("受影响的文件或工作区已变化，请重新预览。");
            }
            progress?.Report("正在更新 cone 目录…");
            var result = await SetScopeAsync(current.Entry.Path, current.After).ConfigureAwait(false);
            await VerifyScopeAsync(repository, current.Entry, current.After).ConfigureAwait(false);
            var details = new WorktreeDetails(WorktreeAvailability.Available, SparseMode.Cone, current.After.Directories);
            return new SparseWorktreeResult(current.Entry, details, CombineWarnings(result.Error));
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new GitException($"更新稀疏范围时出错。可能已处理部分文件，请检查工作区后重新预览。{Environment.NewLine}{ex.Message}");
        }
    }

    private async Task DiscardAsync(SparseChangePreview preview)
    {
        var root = preview.Entry.Path;
        if (preview.RestorePaths.Any(path => !IsSafeDiscardPath(root, path, allowMissing: true)) ||
            preview.DeletePaths.Any(path => !IsSafeDiscardPath(root, path, allowMissing: false)))
            throw new GitException("本地内容的路径已变化，请重新预览。");

        if (preview.RestorePaths.Count > 0)
        {
            var input = string.Join('\0', preview.RestorePaths) + '\0';
            var restored = await WriteAsync(root, input, "--literal-pathspecs", "restore", "--source=" + preview.Commit,
                "--staged", "--worktree", "--ignore-skip-worktree-bits", "--no-recurse-submodules",
                "--pathspec-from-file=-", "--pathspec-file-nul").ConfigureAwait(false);
            restored.RequiredOutput();
        }
        foreach (var path in preview.DeletePaths)
        {
            if (!IsSafeDiscardPath(root, path, allowMissing: false))
                throw new GitException($"本地文件的路径已变化：{path}");
            File.Delete(Path.Combine(root, path));
        }
    }

    private static bool IsSafeDiscardPath(string root, string path, bool allowMissing)
    {
        if (!ConeScope.IsDirectoryPath(path) || path.Split('/').Any(part => !IsValidWindowsName(part)))
            return false;
        for (string? ancestor = root; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (ReadAttributes(ancestor) is not { } attributes ||
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                return false;

        for (var relative = ConeScope.Parent(path); relative.Length > 0; relative = ConeScope.Parent(relative))
        {
            var directory = Path.Combine(root, relative);
            if (ReadAttributes(directory) is { } attributes &&
                ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
                Exists(Path.Combine(directory, ".git")) || IsPossibleBareRepository(directory)))
                return false;
        }
        var file = ReadAttributes(Path.Combine(root, path));
        return file is null ? allowMissing : (file.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static void CheckRestoreBoundaries(HashSet<string> restore,
        IEnumerable<(string Path, bool IsSpecial)> entries, HashSet<string> blockers)
    {
        var exactPaths = restore.ToHashSet(StringComparer.Ordinal);
        var parents = new HashSet<string>(Paths);
        foreach (var path in restore)
            for (var parent = ConeScope.Parent(path); parent.Length > 0; parent = ConeScope.Parent(parent))
                parents.Add(parent);

        foreach (var (path, special) in entries)
        {
            if (parents.Contains(path) || (restore.Contains(path) && (special || !exactPaths.Contains(path))))
                blockers.Add(path + "（链接、子模块或文件目录冲突）");
            for (var parent = ConeScope.Parent(path); parent.Length > 0; parent = ConeScope.Parent(parent))
                if (restore.Contains(parent))
                    blockers.Add(parent + "（文件目录冲突）");
        }
    }

    private static async Task<string> FingerprintDiscardAsync(string root, IReadOnlyList<string> paths,
        List<string> status, List<string> index, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var record in status.Concat(index).Order(StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(record + "\0"));
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
            var file = Path.Combine(root, path);
            var attributes = ReadAttributes(file);
            hash.AppendData([attributes is null ? (byte)0 : (byte)1]);
            if (attributes is not null)
            {
                using var stream = File.OpenRead(file);
                hash.AppendData(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task ValidateDirectoriesAsync(string path, string commit, ConeScope scope, CancellationToken token)
    {
        var directories = (await ListDirectoriesAsync(path, commit, token).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var invalid = scope.Directories.FirstOrDefault(dir => !directories.Contains(dir));
        if (invalid is not null)
            throw new GitException($"所选提交不包含目录：{invalid}");
    }

    private async Task RequireMainWorktreeAsync(RepositoryLocation repository, WorktreeSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.Entries[0].IsBare || !Paths.Equals(snapshot.Entries[0].Path, repository.MainPath))
            throw new GitException("请从可访问的主工作区创建，必要时先重新定位仓库。");
        await _reader.VerifyWorktreeAsync(repository, repository.MainPath, token).ConfigureAwait(false);
    }

    private async Task<GitResult> SetScopeAsync(string path, ConeScope scope)
    {
        var result = await WriteAsync(path, string.Concat(scope.Directories.Select(directory => directory + "\n")),
            "-c", "checkout.workers=8", "-c", "submodule.recurse=false",
            "sparse-checkout", "set", "--cone", "--no-sparse-index", "--stdin")
            .ConfigureAwait(false);
        result.RequiredOutput();
        return result;
    }

    private async Task VerifyScopeAsync(RepositoryLocation repository, WorktreeEntry entry, ConeScope expected)
    {
        var actual = await ReadScopeAsync(repository, entry, CancellationToken.None).ConfigureAwait(false);
        if (!actual.Directories.SequenceEqual(expected.Directories))
            throw new GitException("Git 保存的目录范围与预览不一致。");
    }

    private static void CheckDirectoryBoundaries(string root, ConeScope before, ConeScope after,
        HashSet<string> local, CancellationToken token)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new GitException("工作区根目录是链接，无法安全调整。");
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(child);
                var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    (before.ContainsDirectory(relative) && after.ContainsDirectory(relative)))
                    continue;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0 ||
                    Exists(Path.Combine(child, ".git")) || IsPossibleBareRepository(child))
                {
                    local.Add(relative + "/（链接或嵌套仓库）");
                    continue;
                }
                pending.Push(child);
            }
        }
    }

    private static void RequireUnoccupiedPath(string destination, string parent)
    {
        if (Exists(destination))
            throw new GitException($"目标路径已存在：{destination}");
        for (string? current = parent; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Path.GetDirectoryName(current) is not null && !IsValidWindowsName(Path.GetFileName(current)))
                throw new GitException($"目标路径包含无效的父文件夹名称：{current}");
            if (!Exists(current))
                continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new GitException($"目标路径的父级不是普通文件夹：{current}");
            if (Exists(Path.Combine(current, ".git")))
                throw new GitException($"目标位于另一个 Git 工作区内：{current}");
        }
    }

    private static bool IsPossibleBareRepository(string path) =>
        Exists(Path.Combine(path, "HEAD")) && Exists(Path.Combine(path, "objects")) &&
        (Exists(Path.Combine(path, "refs")) || Exists(Path.Combine(path, "packed-refs")));

    private static bool Exists(string path) => ReadAttributes(path) is not null;

    private static FileAttributes? ReadAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static bool IsAtOrBelow(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static bool IsValidWindowsName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "0123456789¹²³".Contains(stem[3]));
    }

    private static bool IsObjectId(string commit) => commit.Length is 40 or 64 && commit.All(Uri.IsHexDigit);

    private static void RequireObjectId(string commit)
    {
        if (!IsObjectId(commit))
            throw new GitException("请先读取起始提交的目录树。");
    }

    private static string? CombineWarnings(params string[] messages)
    {
        var warnings = string.Join(Environment.NewLine, messages.Select(message => message.Trim()).Where(message => message.Length > 0));
        return warnings.Length == 0 ? null : warnings;
    }

    private Task<GitResult> RunAsync(string path, CancellationToken token, params string[] args) =>
        GitProcess.RunAsync(installation.Executable, path, token, args);

    private Task<GitResult> ReadLongAsync(string path, CancellationToken token, params string[] args) =>
        GitProcess.RunAsync(installation.Executable, path, null, TimeSpan.FromMinutes(5), token, args);

    // Stop cancellation before a write so the application never kills Git halfway through an index update.
    private Task<GitResult> WriteAsync(string path, string? input, params string[] args) =>
        GitProcess.RunAsync(installation.Executable, path, input, Timeout.InfiniteTimeSpan, CancellationToken.None, args);
}
