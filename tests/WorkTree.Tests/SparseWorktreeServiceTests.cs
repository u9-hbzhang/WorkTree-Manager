using System.Diagnostics;
using WorkTree.Core;
using WorkTree.Git;
using Xunit;

namespace WorkTree.Tests;

public sealed class SparseWorktreeServiceTests
{
    [Fact]
    public async Task CreatesFromPinnedRevisionAndReusesExistingBranchAndScope()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var fixture = await Fixture.CreateAsync(timeout.Token);
        var token = timeout.Token;
        var start = await fixture.Service.ResolveCommitAsync(fixture.Main, "HEAD", token);
        await fixture.GitAsync("branch", "saved");
        await fixture.GitAsync("tag", "original");
        await fixture.GitAsync("update-ref", "refs/remotes/origin/release", start);
        await fixture.GitAsync("config", "filter.reject.smudge", "false");
        await fixture.GitAsync("config", "filter.reject.required", "true");
        var choices = await fixture.Service.ListBranchesAsync(fixture.Repository, token);
        Assert.Contains(choices, choice => choice.Reference == "refs/heads/main" && choice.OccupiedPath == fixture.Main);
        Assert.Contains(choices, choice => choice.Reference == "refs/heads/saved" && choice.OccupiedPath is null);
        Assert.Contains(choices, choice => choice.Reference == "refs/remotes/origin/release" && !choice.IsLocal);
        Assert.Contains(choices, choice => choice.Reference == "refs/tags/original");

        var directories = await fixture.Service.ListDirectoriesAsync(fixture.Main, start, token);
        Assert.Contains("业务 A/Child", directories);
        Assert.Contains("Shared", directories);
        var scope = new ConeScope(["业务 A/Child", "Shared"]);
        var path = Path.Combine(fixture.Root, "新工作区 X");
        var created = await fixture.Service.CreateAsync(fixture.Repository,
            new CreateSparseWorktree(path, "codex/topic", false, start, scope), null, token);
        Assert.Equal(start, created.Entry.Head);
        Assert.Equal("codex/topic", created.Entry.Branch);
        Assert.Equal(SparseMode.Cone, created.Details.Mode);
        Assert.True(File.Exists(Path.Combine(path, "root.txt")));
        Assert.True(File.Exists(Path.Combine(path, "业务 A", "parent.txt")));
        Assert.True(File.Exists(Path.Combine(path, "业务 A", "Child", "a.txt")));
        Assert.True(File.Exists(Path.Combine(path, "Shared", "shared.txt")));
        Assert.False(Directory.Exists(Path.Combine(path, "Other")));
        Assert.True(File.Exists(Path.Combine(fixture.Main, "Other", "other.txt")));
        Assert.Empty(await fixture.GitAsync("-C", path, "status", "--porcelain"));

        var recovered = await fixture.Service.ReadScopeAsync(fixture.Repository, created.Entry, token);
        Assert.Equal(scope.Directories, recovered.Directories);
        var secondPath = Path.Combine(fixture.Root, "继续旧分支");
        var second = await fixture.Service.CreateAsync(fixture.Repository,
            new CreateSparseWorktree(secondPath, "saved", true, start, recovered), null, token);
        Assert.Equal("saved", second.Entry.Branch);
        Assert.Equal(start, second.Entry.Head);
        Assert.True(File.Exists(Path.Combine(secondPath, "Shared", "shared.txt")));
        Assert.False(Directory.Exists(Path.Combine(secondPath, "Other")));

        var occupiedPath = Path.Combine(fixture.Root, "occupied");
        await Assert.ThrowsAsync<GitException>(() => fixture.Service.CreateAsync(fixture.Repository,
            new CreateSparseWorktree(occupiedPath, "saved", true, start, recovered), null, token));
        Assert.False(Directory.Exists(occupiedPath));

        var invalidPath = Path.Combine(fixture.Root, "bad-scope");
        await Assert.ThrowsAsync<GitException>(() => fixture.Service.CreateAsync(fixture.Repository,
            new CreateSparseWorktree(invalidPath, "codex/missing", false, start, new ConeScope(["missing"])), null, token));
        Assert.False(Directory.Exists(invalidPath));

        var rootOnly = await fixture.Service.PreviewAsync(fixture.Repository, second.Entry, new ConeScope([]), token);
        await fixture.Service.ApplyAsync(fixture.Repository, rootOnly, null, token);
        Assert.True(File.Exists(Path.Combine(secondPath, "root.txt")));
        Assert.False(Directory.Exists(Path.Combine(secondPath, "Shared")));
    }

    [Fact]
    public async Task ShrinkPreviewProtectsLocalContentAndApplyRechecksAfterConfirmation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var fixture = await Fixture.CreateAsync(timeout.Token);
        var token = timeout.Token;
        var head = await fixture.Service.ResolveCommitAsync(fixture.Main, "HEAD", token);
        var path = Path.Combine(fixture.Root, "work");
        var result = await fixture.Service.CreateAsync(fixture.Repository, new CreateSparseWorktree(
            path, "codex/adjust", false, head, new ConeScope(["业务 A", "Shared"])), null, token);
        var target = new ConeScope(["业务 A/Child", "Other"]);
        var outgoing = Path.Combine(path, "Shared", "shared.txt");
        var retained = Path.Combine(path, "业务 A", "parent.txt");
        await File.AppendAllTextAsync(retained, " retained", token);

        var preview = await fixture.Service.PreviewAsync(fixture.Repository, result.Entry, target, token);
        Assert.Equal(["Shared/shared.txt"], preview.RemovedFiles);
        Assert.Empty(preview.ProtectedFiles);

        await File.AppendAllTextAsync(outgoing, " edit", token);
        await Assert.ThrowsAsync<GitException>(() => fixture.Service.ApplyAsync(fixture.Repository, preview, null, token));
        Assert.EndsWith(" edit", await File.ReadAllTextAsync(outgoing, token));
        await fixture.GitAsync("-C", path, "restore", "--", "Shared/shared.txt");

        await File.WriteAllTextAsync(Path.Combine(path, "Shared", "local.log"), "ignored", token);
        await File.WriteAllTextAsync(Path.Combine(path, "Shared", "local.txt"), "untracked", token);
        await File.AppendAllTextAsync(outgoing, " staged", token);
        await fixture.GitAsync("-C", path, "add", "--", "Shared/shared.txt");
        var blocked = await fixture.Service.PreviewAsync(fixture.Repository, result.Entry, target, token);
        Assert.Contains("Shared/local.log", blocked.ProtectedFiles);
        Assert.Contains("Shared/local.txt", blocked.ProtectedFiles);
        Assert.Contains("Shared/shared.txt", blocked.ProtectedFiles);
        await Assert.ThrowsAsync<GitException>(() => fixture.Service.ApplyAsync(fixture.Repository, blocked, null, token));
        File.Delete(Path.Combine(path, "Shared", "local.log"));
        File.Delete(Path.Combine(path, "Shared", "local.txt"));
        await fixture.GitAsync("-C", path, "restore", "--staged", "--worktree", "--", "Shared/shared.txt");

        await fixture.GitAsync("-C", path, "update-index", "--assume-unchanged", "--", "Shared/shared.txt");
        var skipped = await fixture.Service.PreviewAsync(fixture.Repository, result.Entry, target, token);
        Assert.Contains(skipped.ProtectedFiles, file => file.StartsWith("Shared/shared.txt", StringComparison.Ordinal));
        Assert.NotEmpty(skipped.BlockingFiles);
        await Assert.ThrowsAsync<GitException>(() => fixture.Service.ApplyAsync(fixture.Repository, skipped,
            null, token, skipped.ConfirmationName));
        await fixture.GitAsync("-C", path, "update-index", "--no-assume-unchanged", "--", "Shared/shared.txt");

        Directory.CreateDirectory(Path.Combine(path, "Other"));
        var localOutsideScope = Path.Combine(path, "Other", "local.log");
        await File.WriteAllTextAsync(localOutsideScope, "do not overwrite", token);
        var collision = await fixture.Service.PreviewAsync(fixture.Repository, result.Entry, target, token);
        Assert.Contains("Other/local.log", collision.ProtectedFiles);
        Assert.Contains("Other/local.log", collision.BlockingFiles);
        await Assert.ThrowsAsync<GitException>(() => fixture.Service.ApplyAsync(fixture.Repository, collision,
            null, token, collision.ConfirmationName));
        File.Delete(localOutsideScope);

        var safe = await fixture.Service.PreviewAsync(fixture.Repository, result.Entry, target, token);
        var updated = await fixture.Service.ApplyAsync(fixture.Repository, safe, null, token);
        Assert.Equal(target.Directories, updated.Details.SparsePaths);
        Assert.False(File.Exists(outgoing));
        Assert.True(File.Exists(Path.Combine(path, "Other", "other.txt")));
        Assert.EndsWith(" retained", await File.ReadAllTextAsync(retained, token));
        Assert.True(File.Exists(Path.Combine(fixture.Main, "Shared", "shared.txt")));
    }

    [Fact]
    public async Task ConfirmedShrinkDiscardsOnlyPreviewedOutgoingChanges()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var fixture = await Fixture.CreateAsync(timeout.Token);
        var token = timeout.Token;
        var head = await fixture.Service.ResolveCommitAsync(fixture.Main, "HEAD", token);
        var path = Path.Combine(fixture.Root, "shrink");
        var created = await fixture.Service.CreateAsync(fixture.Repository, new CreateSparseWorktree(
            path, "codex/shrink", false, head, new ConeScope(["业务 A", "Shared"])), null, token);
        var scope = new ConeScope(["业务 A/Child"]);
        var retained = Path.Combine(path, "业务 A", "parent.txt");
        var retainedLocal = Path.Combine(path, "业务 A", "local.log");
        var ignored = Path.Combine(path, "Shared", "discard.log");
        var untracked = Path.Combine(path, "Shared", "[discard].txt");
        await File.AppendAllTextAsync(retained, " staged", token);
        await fixture.GitAsync("-C", path, "add", "--", "业务 A/parent.txt");
        await File.AppendAllTextAsync(retained, " unstaged", token);
        await File.WriteAllTextAsync(retainedLocal, "keep ignored content in a cone ancestor", token);
        await File.AppendAllTextAsync(Path.Combine(path, "Shared", "shared.txt"), " outgoing", token);
        await File.WriteAllTextAsync(Path.Combine(path, "Shared", "[staged].txt"), "staged addition", token);
        await fixture.GitAsync("-C", path, "add", "--", "Shared/shared.txt", "Shared/[staged].txt");
        await File.WriteAllTextAsync(ignored, "old", token);
        await File.WriteAllTextAsync(untracked, "untracked", token);
        var preview = await fixture.Service.PreviewAsync(fixture.Repository, created.Entry, scope, token);
        Assert.Empty(preview.BlockingFiles);
        Assert.Equal(["Shared/[discard].txt", "Shared/[staged].txt", "Shared/discard.log", "Shared/shared.txt"],
            preview.DiscardableFiles);
        await Assert.ThrowsAsync<GitException>(() => fixture.Service.ApplyAsync(fixture.Repository, preview, null, token));
        var timestamp = File.GetLastWriteTimeUtc(ignored);
        await File.WriteAllTextAsync(ignored, "new", token);
        File.SetLastWriteTimeUtc(ignored, timestamp);
        await Assert.ThrowsAsync<GitException>(() => fixture.Service.ApplyAsync(fixture.Repository, preview,
            null, token, preview.ConfirmationName));
        Assert.Equal("new", await File.ReadAllTextAsync(ignored, token));
        Assert.True(File.Exists(untracked));

        var current = await fixture.Service.PreviewAsync(fixture.Repository, created.Entry, scope, token);
        var applied = await fixture.Service.ApplyAsync(fixture.Repository, current, null, token, current.ConfirmationName);
        Assert.Equal(scope.Directories, applied.Details.SparsePaths);
        Assert.False(File.Exists(ignored));
        Assert.False(File.Exists(untracked));
        Assert.False(File.Exists(Path.Combine(path, "Shared", "[staged].txt")));
        Assert.False(File.Exists(Path.Combine(path, "Shared", "shared.txt")));
        Assert.Empty(await fixture.GitAsync("-C", path, "diff", "--cached", "--name-only", "--", "Shared"));
        Assert.EndsWith(" staged unstaged", await File.ReadAllTextAsync(retained, token));
        Assert.Contains("parent.txt", await fixture.GitAsync("-C", path,
            "diff", "--cached", "--name-only", "--", "业务 A/parent.txt"));
        Assert.True(File.Exists(retainedLocal));
        Assert.Equal("shared", await File.ReadAllTextAsync(Path.Combine(fixture.Main, "Shared", "shared.txt"), token));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly CancellationToken _token;
        private readonly GitInstallation _git;
        public GitInstallation Installation => _git;
        public string Root { get; }
        public string Main { get; }
        public RepositoryLocation Repository { get; private set; } = null!;
        public SparseWorktreeService Service { get; }

        private Fixture(GitInstallation git, CancellationToken token)
        {
            _git = git;
            _token = token;
            Root = Path.Combine(Path.GetTempPath(), "WorkTree-tests", Guid.NewGuid().ToString("N"));
            Main = Path.Combine(Root, "main");
            Service = new SparseWorktreeService(git);
        }

        public static async Task<Fixture> CreateAsync(CancellationToken token)
        {
            var git = (await GitDiscovery.DetectAsync(null, token)).Installation
                ?? throw new InvalidOperationException("Git is required for integration tests.");
            var fixture = new Fixture(git, token);
            try
            {
                Directory.CreateDirectory(fixture.Main);
                await fixture.GitAsync("init", "-b", "main");
                foreach (var (relative, content) in new (string, string)[]
                {
                    ("root.txt", "root"),
                    ("业务 A/parent.txt", "parent"),
                    ("业务 A/Child/a.txt", "a"),
                    ("Shared/shared.txt", "shared"),
                    ("Other/other.txt", "other"),
                    (".gitignore", "*.log\n"),
                    (".gitattributes", "Other/other.txt filter=reject\n")
                })
                {
                    var file = Path.Combine(fixture.Main, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllTextAsync(file, content, token);
                }
                await fixture.GitAsync("add", ".");
                await fixture.GitAsync("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                    "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
                (fixture.Repository, _) = await new GitRepositoryReader(git).OpenAsync(fixture.Main, token);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public async Task<string> GitAsync(params string[] args)
        {
            var start = new ProcessStartInfo(_git.Executable)
            {
                UseShellExecute = false, WorkingDirectory = Main, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(_token);
            var error = process.StandardError.ReadToEndAsync(_token);
            await process.WaitForExitAsync(_token);
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {await error}");
            return await output;
        }

        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WorkTree-tests"));
            var resolved = Path.GetFullPath(Root);
            if (!Directory.Exists(resolved) || Path.GetDirectoryName(resolved) != parent
                || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                return;
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(resolved, recursive: true);
        }
    }
}
