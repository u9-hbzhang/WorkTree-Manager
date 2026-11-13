using WorkTree.Core;

namespace WorkTree.Git;

public sealed record CleanupFile(string Path, string Status)
{
    public string Label => $"{Status} · {Path}";
}

public sealed record WorktreeRemovalPreview(WorktreeEntry Entry, string Registration,
    IReadOnlyList<CleanupFile> LocalFiles, int FileCount, string Fingerprint,
    string? MergeTarget, string? MergeCommit, bool CanDeleteBranch, string BranchMessage)
{
    public string ConfirmationName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Entry.Path));
    public bool CanDiscardBranch { get; init; }
}

public enum BranchRemovalMode
{
    Keep,
    DeleteIfMerged,
    Discard
}

public sealed record StaleRegistration(WorktreeEntry Entry, string Registration)
{
    public string Label => $"{Entry.Path}   [{Entry.Branch ?? "游离 HEAD"}]";
}

public sealed record PrunePreview(IReadOnlyList<StaleRegistration> Entries);

public sealed record CleanupResult(IReadOnlyList<string> RemovedPaths, string Message, bool HasWarning = false);
