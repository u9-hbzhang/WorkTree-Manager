using WorkTree.Core;

namespace WorkTree.Git;

public sealed record BranchChoice(string Reference, string Name, bool IsLocal, string? OccupiedPath)
{
    public string Label => OccupiedPath is null ? Name : $"{Name}（已检出）";
}

public sealed record CreateSparseWorktree(
    string Destination, string Branch, bool ExistingBranch, string Commit, ConeScope Scope);

public sealed record SparseChangePreview(
    WorktreeEntry Entry, string Commit, ConeScope Before, ConeScope After,
    IReadOnlyList<string> RemovedFiles, IReadOnlyList<string> ProtectedFiles)
{
    public IReadOnlyList<string> DiscardableFiles { get; init; } = [];
    public IReadOnlyList<string> BlockingFiles { get; init; } = [];
    public string ConfirmationName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Entry.Path));
    internal string DiscardFingerprint { get; init; } = "";
    internal IReadOnlyList<string> RestorePaths { get; init; } = [];
    internal IReadOnlyList<string> DeletePaths { get; init; } = [];
}

public sealed record SparseWorktreeResult(WorktreeEntry Entry, WorktreeDetails Details, string? Warning);
