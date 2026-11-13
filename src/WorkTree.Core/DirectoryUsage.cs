namespace WorkTree.Core;

public sealed record DirectoryUsage(long Bytes, int SkippedEntries)
{
    public static DirectoryUsage Measure(string path, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.Directory) == 0)
            throw new IOException("工作区路径不再是文件夹。");
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("工作区根目录是链接，无法安全统计。");

        long bytes = 0;
        var skipped = 0;
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (directory == root && string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            skipped++;
                        else if ((attributes & FileAttributes.Directory) != 0)
                            directories.Push(entry);
                        else
                            bytes += new FileInfo(entry).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (directory == root)
                    throw;
                skipped++;
            }
        }

        return new DirectoryUsage(bytes, skipped);
    }
}
