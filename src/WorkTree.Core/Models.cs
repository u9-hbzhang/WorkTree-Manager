namespace WorkTree.Core;

public sealed record RepositoryLocation(string MainPath, string CommonDirectory, string AccessPath)
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(MainPath)) is { Length: > 0 } name
        ? name : MainPath;
}

public sealed record WorktreeEntry(
    string Path, string? Head, string? Branch, bool IsMain, bool IsBare,
    bool IsDetached, bool IsLocked, string? LockReason, bool IsPrunable, string? PruneReason);

public enum WorktreeAvailability { Unknown, Available, Missing, Unavailable, Bare }
public enum SparseMode { Unknown, Full, Cone, NonCone }
public enum ChangeState { Unknown, Clean, Modified }

public sealed record WorktreeDetails(
    WorktreeAvailability Availability, SparseMode Mode = SparseMode.Unknown,
    IReadOnlyList<string>? SparsePaths = null, ChangeState Changes = ChangeState.Unknown,
    DateTimeOffset? CheckedAt = null, string? Error = null);

public sealed record WorktreeSnapshot(DateTimeOffset ReadAt, WorktreeEntry[] Entries);

public sealed record ExternalTool(string Name, string Executable, string[] Arguments)
{
    public const string PathToken = "{worktree}";

    public string[] ArgumentsFor(string path) => Arguments
        .Select(argument => argument.Replace(PathToken, path, StringComparison.Ordinal))
        .ToArray();
}

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string? GitPath { get; init; }
    public RepositoryLocation[] Repositories { get; init; } = [];
    public string? SelectedRepository { get; init; }
    public ExternalTool[] Tools { get; init; } = [];
    public Dictionary<string, string> MergeTargets { get; init; } = new();
}
