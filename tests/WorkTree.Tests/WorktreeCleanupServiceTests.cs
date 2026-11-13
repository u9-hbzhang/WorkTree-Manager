using WorkTree.Git;
using Xunit;
using Fixture = WorkTree.Tests.SparseWorktreeServiceTests.Fixture;

namespace WorkTree.Tests;

public sealed class WorktreeCleanupServiceTests
{
    [Fact]
    public async Task RequiresNamedConfirmationAndRejectsChangedLocalContent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        using var fixture = await Fixture.CreateAsync(token);
        var service = new WorktreeCleanupService(fixture.Installation);
        var path = Path.Combine(fixture.Root, "有修改的工作区");
        await fixture.GitAsync("worktree", "add", "-b", "dirty", path);
        await File.AppendAllTextAsync(Path.Combine(path, "root.txt"), " edited", token);
        var ignored = Path.Combine(path, "local.log");
        await File.WriteAllTextAsync(ignored, "old", token);
        await File.WriteAllTextAsync(Path.Combine(path, "scratch.txt"), "local", token);
        await fixture.GitAsync("-C", path, "add", "--", "scratch.txt");
        var preview = await service.PreviewRemovalAsync(fixture.Repository, path, "refs/heads/main", token);
        Assert.True(preview.CanDeleteBranch);
        Assert.Contains(new CleanupFile("root.txt", "已修改"), preview.LocalFiles);
        Assert.Contains(new CleanupFile("local.log", "已忽略"), preview.LocalFiles);
        Assert.Contains(new CleanupFile("scratch.txt", "已暂存"), preview.LocalFiles);
        await Assert.ThrowsAsync<GitException>(() => service.RemoveAsync(fixture.Repository, preview, null, false, token));
        Assert.True(Directory.Exists(path));

        var timestamp = File.GetLastWriteTimeUtc(ignored);
        await File.WriteAllTextAsync(ignored, "new", token);
        File.SetLastWriteTimeUtc(ignored, timestamp);
        await Assert.ThrowsAsync<GitException>(() => service.RemoveAsync(fixture.Repository, preview,
            preview.ConfirmationName, false, token));
        Assert.Equal("new", await File.ReadAllTextAsync(ignored, token));

        var rechecked = await service.RecheckMergeTargetAsync(fixture.Repository, preview, null, token);
        Assert.False(rechecked.CanDeleteBranch);
        Assert.Equal(preview.Fingerprint, rechecked.Fingerprint);
        await Assert.ThrowsAsync<GitException>(() => service.RemoveAsync(fixture.Repository, rechecked,
            rechecked.ConfirmationName, false, token));

        var current = await service.PreviewRemovalAsync(fixture.Repository, path, "refs/heads/main", token);
        var removed = await service.RemoveAsync(fixture.Repository, current, current.ConfirmationName, false, token);
        Assert.False(removed.HasWarning, removed.Message);
        Assert.False(Directory.Exists(path));
        Assert.Contains("refs/heads/dirty", await fixture.GitAsync("show-ref", "--verify", "refs/heads/dirty"));
    }

    [Fact]
    public async Task DeletesOnlyProvenMergedBranchAndProtectsLockedWorktree()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        using var fixture = await Fixture.CreateAsync(token);
        var service = new WorktreeCleanupService(fixture.Installation);
        var path = Path.Combine(fixture.Root, "linked");
        await fixture.GitAsync("worktree", "add", "-b", "topic", path);
        await fixture.GitAsync("-C", path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "topic");
        var unmerged = await service.PreviewRemovalAsync(fixture.Repository, path, "refs/heads/main", token);
        Assert.False(unmerged.CanDeleteBranch);
        await Assert.ThrowsAsync<GitException>(() => service.RemoveAsync(fixture.Repository, unmerged, null, true, token));
        Assert.True(Directory.Exists(path));

        await fixture.GitAsync("merge", "--ff-only", "topic");
        await fixture.GitAsync("worktree", "lock", path);
        await Assert.ThrowsAsync<GitException>(() => service.PreviewRemovalAsync(fixture.Repository, path, "refs/heads/main", token));
        await Assert.ThrowsAsync<GitException>(() => service.PreviewRemovalAsync(fixture.Repository, fixture.Main, null, token));
        await fixture.GitAsync("worktree", "unlock", path);
        var merged = await service.PreviewRemovalAsync(fixture.Repository, path, "refs/heads/main", token);
        Assert.True(merged.CanDeleteBranch, merged.BranchMessage);
        Assert.Empty(merged.LocalFiles);
        var result = await service.RemoveAsync(fixture.Repository, merged, null, true, token);
        Assert.False(result.HasWarning, result.Message);
        Assert.False(Directory.Exists(path));
        Assert.Empty(await fixture.GitAsync("branch", "--list", "topic"));
        Assert.True(Directory.Exists(fixture.Main));
    }

    [Fact]
    public async Task DiscardsUnmergedLocalBranchOnlyWithExplicitConfirmationAndCurrentPreview()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        using var fixture = await Fixture.CreateAsync(token);
        var service = new WorktreeCleanupService(fixture.Installation);
        var path = Path.Combine(fixture.Root, "experiment");
        var other = Path.Combine(fixture.Root, "shared");
        await fixture.GitAsync("worktree", "add", "-b", "experiment", path);

        async Task CommitAsync(string message) => await fixture.GitAsync("-C", path,
            "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--allow-empty", "-m", message);

        await CommitAsync("local experiment");
        await fixture.GitAsync("update-ref", "refs/remotes/origin/experiment", "main");
        var preview = await service.PreviewRemovalAsync(fixture.Repository, path, null, token);
        Assert.False(preview.CanDeleteBranch);
        Assert.True(preview.CanDiscardBranch);
        Assert.Empty(preview.LocalFiles);
        await Assert.ThrowsAsync<GitException>(() => service.RemoveAsync(fixture.Repository, preview, null,
            BranchRemovalMode.Discard, token));
        Assert.True(Directory.Exists(path));

        await fixture.GitAsync("worktree", "add", "--force", other, "experiment");
        Assert.False((await service.PreviewRemovalAsync(fixture.Repository, path, null, token)).CanDiscardBranch);
        await Assert.ThrowsAsync<GitException>(() => service.RemoveAsync(fixture.Repository, preview,
            preview.ConfirmationName, BranchRemovalMode.Discard, token));
        await fixture.GitAsync("worktree", "remove", other);

        await CommitAsync("changed after preview");
        await Assert.ThrowsAsync<GitException>(() => service.RemoveAsync(fixture.Repository, preview,
            preview.ConfirmationName, BranchRemovalMode.Discard, token));
        Assert.True(Directory.Exists(path));

        var current = await service.PreviewRemovalAsync(fixture.Repository, path, null, token);
        var result = await service.RemoveAsync(fixture.Repository, current, current.ConfirmationName,
            BranchRemovalMode.Discard, token);
        Assert.False(result.HasWarning, result.Message);
        Assert.False(Directory.Exists(path));
        Assert.Empty(await fixture.GitAsync("branch", "--list", "experiment"));
        Assert.Contains("refs/remotes/origin/experiment",
            await fixture.GitAsync("show-ref", "--verify", "refs/remotes/origin/experiment"));
    }

    [Fact]
    public async Task DiskDeletionFailurePreservesRegistrationAndBranch()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        using var fixture = await Fixture.CreateAsync(token);
        var service = new WorktreeCleanupService(fixture.Installation);
        var path = Path.Combine(fixture.Root, "busy");
        await fixture.GitAsync("worktree", "add", "-b", "busy", path);
        var preview = await service.PreviewRemovalAsync(fixture.Repository, path, null, token);

        using (File.Open(Path.Combine(path, "root.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<GitException>(() => service.RemoveAsync(fixture.Repository, preview,
                preview.ConfirmationName, BranchRemovalMode.Discard, token));
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.Contains("refs/heads/busy", await fixture.GitAsync("worktree", "list", "--porcelain"));
            Assert.Contains("refs/heads/busy", await fixture.GitAsync("show-ref", "--verify", "refs/heads/busy"));
        }

        var current = await service.PreviewRemovalAsync(fixture.Repository, path, null, token);
        var result = await service.RemoveAsync(fixture.Repository, current, current.ConfirmationName,
            BranchRemovalMode.Discard, token);
        Assert.False(result.HasWarning, result.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task PruneChecksEntirePreviewAndLeavesBranchesAndLockedEntries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        using var fixture = await Fixture.CreateAsync(token);
        var service = new WorktreeCleanupService(fixture.Installation);
        var path = Path.Combine(fixture.Root, "待清理 A");
        var moved = Path.Combine(fixture.Root, "moved");
        var locked = Path.Combine(fixture.Root, "locked");
        await fixture.GitAsync("worktree", "add", "-b", "stale", path);
        await fixture.GitAsync("worktree", "add", "-b", "keep", locked);
        await fixture.GitAsync("worktree", "lock", locked);
        Directory.Move(locked, Path.Combine(fixture.Root, "locked-moved"));

        await File.WriteAllTextAsync(Path.Combine(path, "pending.txt"), "staged", token);
        await fixture.GitAsync("-C", path, "add", "--", "pending.txt");
        Directory.Move(path, moved);
        await Assert.ThrowsAsync<GitException>(() => service.PreviewPruneAsync(fixture.Repository, token));
        Directory.Move(moved, path);
        await fixture.GitAsync("-C", path, "reset", "HEAD", "--", "pending.txt");
        Directory.Move(path, moved);

        var preview = await service.PreviewPruneAsync(fixture.Repository, token);
        Assert.Equal(path, Assert.Single(preview.Entries).Entry.Path);
        Directory.Move(moved, path);
        await Assert.ThrowsAsync<GitException>(() => service.PruneAsync(fixture.Repository, preview, token));
        Directory.Move(path, moved);
        var result = await service.PruneAsync(fixture.Repository, await service.PreviewPruneAsync(fixture.Repository, token), token);
        Assert.False(result.HasWarning, result.Message);
        Assert.Equal(path, Assert.Single(result.RemovedPaths));
        Assert.Empty((await service.PreviewPruneAsync(fixture.Repository, token)).Entries);
        Assert.Contains("refs/heads/stale", await fixture.GitAsync("show-ref", "--verify", "refs/heads/stale"));
        Assert.Contains("refs/heads/keep", await fixture.GitAsync("show-ref", "--verify", "refs/heads/keep"));
        var snapshot = await new GitRepositoryReader(fixture.Installation).RefreshAsync(fixture.Repository, token);
        Assert.Contains(snapshot.Entries, entry => entry.Path == locked && entry.IsLocked);
        Assert.True(File.Exists(Path.Combine(moved, "pending.txt")));
    }
}
