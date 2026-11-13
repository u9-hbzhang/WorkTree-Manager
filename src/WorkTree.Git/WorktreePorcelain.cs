using WorkTree.Core;

namespace WorkTree.Git;

public static class WorktreePorcelain
{
    public static WorktreeEntry[] Parse(string output)
    {
        var entries = new List<WorktreeEntry>();
        string? path = null;
        string? head = null;
        string? branch = null;
        string? lockReason = null;
        string? pruneReason = null;
        bool bare = false, detached = false, locked = false, prunable = false;

        void FinishEntry()
        {
            if (path is null)
                return;

            entries.Add(new WorktreeEntry(path, head, branch, entries.Count == 0,
                bare, detached, locked, lockReason, prunable, pruneReason));
            path = head = branch = lockReason = pruneReason = null;
            bare = detached = locked = prunable = false;
        }

        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                FinishEntry();
                continue;
            }

            var separator = field.IndexOf(' ');
            var key = separator < 0 ? field : field[..separator];
            var value = separator < 0 ? null : field[(separator + 1)..];
            if (path is null && key != "worktree")
                throw new GitException("Git 工作区列表格式异常。");

            switch (key)
            {
                case "worktree":
                    if (path is not null || value is null || !System.IO.Path.IsPathFullyQualified(value))
                        throw new GitException("Git 返回了无效的工作区路径。");
                    path = System.IO.Path.GetFullPath(value);
                    break;
                case "HEAD": head = value; break;
                case "branch":
                    const string prefix = "refs/heads/";
                    branch = value?.StartsWith(prefix, StringComparison.Ordinal) == true
                        ? value[prefix.Length..] : value;
                    break;
                case "bare": bare = true; break;
                case "detached": detached = true; break;
                case "locked": locked = true; lockReason = value; break;
                case "prunable": prunable = true; pruneReason = value; break;
            }
        }

        FinishEntry();
        return entries.Count == 0 ? throw new GitException("Git 没有返回工作区。") : [.. entries];
    }
}
