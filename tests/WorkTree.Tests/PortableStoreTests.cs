using WorkTree.Core;
using Xunit;

namespace WorkTree.Tests;

public sealed class PortableStoreTests
{
    [Fact]
    public async Task RetainsPreferencesAndKeepsSnapshotsSeparateByRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "WorkTree-tests", Guid.NewGuid().ToString("N"));
        var store = new PortableStore(root);
        var main = Path.Combine(root, "仓库 A");
        var repo = new RepositoryLocation(main, Path.Combine(main, ".git"), main);
        var another = repo with { CommonDirectory = Path.Combine(root, "另一个仓库", ".git") };
        var tool = new ExternalTool("编辑器", Path.Combine(root, "editor.exe"), ["--new-window", "{worktree}"]);
        var settings = new AppSettings { Repositories = [repo], SelectedRepository = repo.CommonDirectory, Tools = [tool] };
        var snapshot = new WorktreeSnapshot(DateTimeOffset.Now,
            [new WorktreeEntry(main, "abc123", "main", true, false, false, false, null, false, null)]);

        try
        {
            await store.SaveSettingsAsync(settings);
            await store.SaveSnapshotAsync(repo, snapshot);
            var restarted = new PortableStore(root);
            var restored = restarted.LoadSettings();
            Assert.Equal(repo, Assert.Single(restored.Repositories));
            Assert.Equal(repo.CommonDirectory, restored.SelectedRepository);
            Assert.Equal(["--new-window", main], Assert.Single(restored.Tools).ArgumentsFor(main));
            Assert.Equal(snapshot.Entries, restarted.LoadSnapshot(repo)?.Entries);
            Assert.Null(restarted.LoadSnapshot(another));
        }
        finally
        {
            var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WorkTree-tests"));
            var resolved = Path.GetFullPath(root);
            if (Directory.Exists(resolved) && Path.GetDirectoryName(resolved) == testParent
                && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
