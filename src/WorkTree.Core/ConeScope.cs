namespace WorkTree.Core;

public sealed class ConeScope
{
    private readonly HashSet<string> _selected;
    private readonly HashSet<string> _ancestors = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Directories { get; }

    public ConeScope(IEnumerable<string> directories)
    {
        var paths = directories.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Any(path => !IsDirectoryPath(path)))
            throw new ArgumentException("请选择 Git 树中的有效目录。");

        var all = paths.ToHashSet(StringComparer.Ordinal);
        var explicitPaths = paths.Where(path => !Parents(path).Any(all.Contains))
            .Order(StringComparer.Ordinal).ToArray();
        Directories = Array.AsReadOnly(explicitPaths);
        _selected = explicitPaths.ToHashSet(StringComparer.Ordinal);
        foreach (var path in explicitPaths)
            _ancestors.UnionWith(Parents(path));
    }

    public bool ContainsDirectory(string path) => _selected.Contains(path) || Parents(path).Any(_selected.Contains);

    public bool ContainsFile(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash < 0)
            return true;
        var parent = path[..slash];
        return _ancestors.Contains(parent) || ContainsDirectory(parent);
    }

    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public static bool IsDirectoryPath(string path) =>
        path.Length > 0 && path.Split('/').All(part =>
            part.Length > 0 && part is not "." and not ".." &&
            !part.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
            part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    private static IEnumerable<string> Parents(string path)
    {
        for (var end = path.LastIndexOf('/'); end >= 0; end = path.LastIndexOf('/', end - 1))
            yield return path[..end];
    }
}
