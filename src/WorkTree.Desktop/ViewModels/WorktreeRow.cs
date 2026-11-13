using System.IO;
using WorkTree.Core;

namespace WorkTree.Desktop.ViewModels;

internal sealed class WorktreeRow(WorktreeEntry entry) : Bindable
{
    private WorktreeDetails _details = new(WorktreeAvailability.Unknown);
    public WorktreeEntry Entry { get; private set; } = entry;

    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Entry.Path)) is { Length: > 0 } name
        ? name : Entry.Path;
    public string MainMarker => Entry.IsMain ? " · 主工作区" : "";
    public string PathText => Entry.Path;
    public string Branch => Entry.Branch ?? (Entry.IsDetached ? "游离 HEAD" : "—");
    public string Commit => Entry.Head ?? "—";
    public string Kind => Entry.IsMain ? "主工作区" : "关联工作区";
    public string Lock => Entry.IsLocked ? Entry.LockReason is { Length: > 0 } reason
        ? $"已锁定 · {reason}" : "已锁定" : "未锁定";
    public string? Prune => Entry.IsPrunable ? $"可清理登记 · {Entry.PruneReason}" : null;
    public string? Error => _details.Error;
    public string CheckedAt => _details.CheckedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "尚未检查";
    public bool CanOpen => !Entry.IsBare && (_details.Availability == WorktreeAvailability.Available
        || (_details.Availability == WorktreeAvailability.Unknown && !Entry.IsPrunable));
    public bool CanAdjustScope => !Entry.IsMain && !Entry.IsLocked && !Entry.IsPrunable && CanOpen
        && _details.Mode is SparseMode.Unknown or SparseMode.Cone;
    public bool CanRemove => !Entry.IsMain && !Entry.IsLocked && !Entry.IsPrunable && CanOpen;
    public bool CanReuseScope => CanOpen && _details.Mode is SparseMode.Unknown or SparseMode.Cone;

    public string Mode => _details.Mode switch
    {
        SparseMode.Full => "全量",
        SparseMode.Cone => "Cone",
        SparseMode.NonCone => "非 Cone",
        _ => "—"
    };

    public string SparseScope => _details.SparsePaths switch
    {
        { Count: > 0 } paths => string.Join(Environment.NewLine, paths),
        { Count: 0 } when _details.Mode == SparseMode.Cone => "仅根目录文件",
        _ when _details.Mode == SparseMode.Full => "全部目录",
        _ => "—"
    };

    public string State => _details.Availability switch
    {
        WorktreeAvailability.Bare => "裸仓库",
        WorktreeAvailability.Missing => "目录缺失",
        WorktreeAvailability.Unavailable => "无法访问",
        _ => _details.Changes switch
        {
            ChangeState.Clean => "干净",
            ChangeState.Modified => "有修改",
            _ when Error is not null => "状态未知",
            _ when Entry.IsBare => "裸仓库",
            _ when Entry.IsPrunable => "待核实失效",
            _ => "待检查"
        }
    };

    public void Update(WorktreeDetails details)
    {
        _details = details;
        Notify(string.Empty);
    }

    public void UpdateEntry(WorktreeEntry entry)
    {
        if (Entry == entry)
            return;

        if (Entry.IsBare != entry.IsBare || Entry.IsPrunable != entry.IsPrunable)
            _details = new WorktreeDetails(WorktreeAvailability.Unknown);
        Entry = entry;
        Notify(string.Empty);
    }
}
