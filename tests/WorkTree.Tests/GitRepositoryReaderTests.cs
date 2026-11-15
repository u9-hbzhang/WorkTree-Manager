using System.Diagnostics;
using System.Text;
using WorkTree.Core;
using WorkTree.Git;
using Xunit;

namespace WorkTree.Tests;

public sealed class GitRepositoryReaderTests
{
    [Fact]
    public async Task ResolvesOneDirectorySelectionFromTheSameLinkedWorktree()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        using var fixture = await SparseWorktreeServiceTests.Fixture.CreateAsync(token);
        var linked = Path.Combine(fixture.Root, "关联工作区");
        await fixture.GitAsync("worktree", "add", "-b", "explorer-topic", linked);
        var reader = new GitRepositoryReader(fixture.Installation);
        var first = Path.Combine(linked, "业务 A", "Child");
        var second = Path.Combine(linked, "Shared");
        var selection = await reader.OpenDirectorySelectionAsync([first, second, first], token);
        Assert.Equal("explorer-topic", selection.Source.Branch);
        Assert.Equal(linked, selection.Source.Path);
        Assert.Equal(fixture.Main, selection.Repository.MainPath);
        Assert.Equal(["Shared", "业务 A/Child"], selection.Directories);
        var root = await reader.OpenDirectorySelectionAsync([linked], token);
        Assert.Empty(root.Directories);
        await Assert.ThrowsAsync<GitException>(() =>
            reader.OpenDirectorySelectionAsync([first, Path.Combine(fixture.Main, "Shared")], token));
        await Assert.ThrowsAsync<GitException>(() =>
            reader.OpenDirectorySelectionAsync([Path.Combine(linked, "root.txt")], token));
    }

    [Fact]
    public async Task ReadsMainLinkedDetachedLockedAndMissingTrees()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var git = (await GitDiscovery.DetectAsync(null, token)).Installation
            ?? throw new InvalidOperationException("These integration tests require Git 2.43 or newer.");
        var root = Path.Combine(Path.GetTempPath(), "WorkTree-tests", Guid.NewGuid().ToString("N"));
        var main = Path.Combine(root, "主仓库 A");
        var linked = Path.Combine(root, "稀疏目录 B");
        var detached = Path.Combine(root, "游离 C");
        var missing = Path.Combine(root, "待失效 D");
        Directory.CreateDirectory(main);

        try
        {
            await Git("init", "-b", "main");
            Directory.CreateDirectory(Path.Combine(main, "业务 源码"));
            Directory.CreateDirectory(Path.Combine(main, "Other"));
            await File.WriteAllTextAsync(Path.Combine(main, "业务 源码", "sample.txt"), "original", token);
            await File.WriteAllTextAsync(Path.Combine(main, "Other", "excluded.txt"), "original", token);
            await Git("add", ".");
            await Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
            await Git("worktree", "add", "--no-checkout", "-b", "topic", linked);
            await Git("-C", linked, "sparse-checkout", "set", "--cone", "--no-sparse-index", "--", "业务 源码");
            await Git("-C", linked, "reset", "--hard", "HEAD");
            await Git("worktree", "lock", "--reason", "保留 A", linked);
            await Git("worktree", "add", "--detach", detached);
            await Git("-C", detached, "sparse-checkout", "set", "--no-cone", "--", "/Other/");
            await Git("worktree", "add", "-b", "abandoned", missing);
            Directory.Move(missing, Path.Combine(root, "已移动 D"));
            await File.AppendAllTextAsync(Path.Combine(linked, "业务 源码", "sample.txt"), " modified", token);

            var reader = new GitRepositoryReader(git);
            var (repository, snapshot) = await reader.OpenAsync(Path.Combine(linked, "业务 源码"), token);
            var (fromMain, _) = await reader.OpenAsync(main, token);
            Assert.Equal(fromMain.CommonDirectory, repository.CommonDirectory);
            Assert.Equal(main, repository.MainPath);
            Assert.Equal(linked, repository.AccessPath);
            Assert.Equal(4, snapshot.Entries.Length);
            Assert.True(snapshot.Entries[0].IsMain);

            var linkedEntry = Assert.Single(snapshot.Entries, entry => entry.Path == linked);
            Assert.True(linkedEntry.IsLocked);
            Assert.Equal("保留 A", linkedEntry.LockReason);
            Assert.Equal("topic", linkedEntry.Branch);
            var linkedDetails = await reader.ReadDetailsAsync(repository, linkedEntry, token);
            Assert.Equal(SparseMode.Cone, linkedDetails.Mode);
            Assert.Equal(["业务 源码"], linkedDetails.SparsePaths);
            Assert.Equal(ChangeState.Modified, linkedDetails.Changes);
            Assert.False(Directory.Exists(Path.Combine(linked, "Other")));

            var detachedEntry = Assert.Single(snapshot.Entries, entry => entry.IsDetached);
            Assert.Null(detachedEntry.Branch);
            var detachedDetails = await reader.ReadDetailsAsync(repository, detachedEntry, token);
            Assert.Equal(SparseMode.NonCone, detachedDetails.Mode);
            Assert.Equal(["/Other/"], detachedDetails.SparsePaths);
            Assert.Equal(ChangeState.Clean, detachedDetails.Changes);

            var missingEntry = Assert.Single(snapshot.Entries, entry => entry.Path == missing);
            Assert.True(missingEntry.IsPrunable);
            var missingDetails = await reader.ReadDetailsAsync(repository, missingEntry, token);
            Assert.Equal(WorktreeAvailability.Missing, missingDetails.Availability);
            Assert.Equal(ChangeState.Unknown, missingDetails.Changes);
            var mainDetails = await reader.ReadDetailsAsync(repository, snapshot.Entries[0], token);
            Assert.Equal(SparseMode.Full, mainDetails.Mode);
            Assert.Equal(ChangeState.Clean, mainDetails.Changes);

            await Git("-C", main, "worktree", "unlock", linked);
            var refreshed = await reader.RefreshAsync(repository, token);
            Assert.False(Assert.Single(refreshed.Entries, entry => entry.Path == linked).IsLocked);

            // An unrelated Git repository at a formerly registered path must not be reported as clean.
            Directory.CreateDirectory(missing);
            await Git("-C", missing, "init", "-b", "main");
            var replaced = await reader.ReadDetailsAsync(repository, missingEntry, token);
            Assert.Equal(WorktreeAvailability.Unavailable, replaced.Availability);
            Assert.Equal(ChangeState.Unknown, replaced.Changes);
        }
        finally
        {
            // Only delete the exact temporary fixture directory created by this test.
            var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WorkTree-tests"));
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) == testParent && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            {
                foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(resolved, recursive: true);
            }
        }

        async Task Git(params string[] args)
        {
            var start = new ProcessStartInfo(git.Executable)
            {
                WorkingDirectory = main,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(token);
            var output = process.StandardOutput.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {await error}\n{await output}");
        }
    }
}
