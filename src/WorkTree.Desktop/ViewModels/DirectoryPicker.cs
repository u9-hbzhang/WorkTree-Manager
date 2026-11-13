using System.Collections.ObjectModel;
using WorkTree.Core;

namespace WorkTree.Desktop.ViewModels;

internal sealed class DirectoryPicker : Bindable
{
    private readonly ILookup<string, string> _children;
    private readonly HashSet<string> _available;
    private readonly Dictionary<string, DirectoryNode> _nodes = new(StringComparer.Ordinal);
    private readonly string[] _paths;
    private ConeScope _scope = new([]);

    public DirectoryPicker(string[] paths)
    {
        _paths = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        _available = paths.ToHashSet(StringComparer.Ordinal);
        _children = paths.ToLookup(ConeScope.Parent, StringComparer.Ordinal);
    }

    public ConeScope Scope => _scope;
    public string[] Selected => _scope.Directories.ToArray();
    public DirectoryNode[] Roots => Children("");
    public event EventHandler? SelectionChanged;

    public DirectoryNode[] Search(string query, int limit) => _paths
        .Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase))
        .Take(limit).Select(GetNode).ToArray();

    public string[] SelectDirectories(IEnumerable<string> paths)
    {
        var selection = paths.ToArray();
        ChangeScope(new ConeScope(selection.Where(_available.Contains)));
        return selection.Where(path => !_available.Contains(path)).ToArray();
    }

    private void ChangeScope(ConeScope scope)
    {
        _scope = scope;
        foreach (var node in _nodes.Values)
            node.NotifySelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private DirectoryNode[] Children(string parent) => _children[parent]
        .Order(StringComparer.OrdinalIgnoreCase).Select(GetNode).ToArray();

    private DirectoryNode GetNode(string path)
    {
        if (!_nodes.TryGetValue(path, out var node))
        {
            node = new DirectoryNode(this, path, _children.Contains(path));
            _nodes.Add(path, node);
        }
        return node;
    }

    internal sealed class DirectoryNode : Bindable
    {
        private readonly DirectoryPicker _owner;
        private bool _loaded;

        public DirectoryNode(DirectoryPicker owner, string path, bool hasChildren)
        {
            _owner = owner;
            Path = path;
            Name = path[(path.LastIndexOf('/') + 1)..];
            if (hasChildren)
                Children.Add(null);
        }

        public string Path { get; }
        public string Name { get; }
        public ObservableCollection<DirectoryNode?> Children { get; } = [];
        public bool CanToggle => !_owner.Scope.ContainsDirectory(ConeScope.Parent(Path));

        public bool Included
        {
            get => _owner.Scope.ContainsDirectory(Path);
            set
            {
                if (!CanToggle || value == Included)
                    return;

                var directories = _owner.Scope.Directories;
                _owner.ChangeScope(new ConeScope(value ? directories.Append(Path) : directories.Where(path => path != Path)));
            }
        }

        public void Expand()
        {
            if (_loaded)
                return;
            _loaded = true;
            Children.Clear();
            foreach (var child in _owner.Children(Path))
                Children.Add(child);
        }

        public void NotifySelection()
        {
            Notify(nameof(Included));
            Notify(nameof(CanToggle));
        }
    }
}
