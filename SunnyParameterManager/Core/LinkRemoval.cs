using System.Text.Json;

namespace SunnyParameterManager.Core;

public sealed class LinkRemovalItem
{
    public string UniqueId { get; set; } = "";
    public long ElementId { get; set; }
    public string Revision { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Root { get; set; }
    public bool Pinned { get; set; }
}

public sealed class LinkRemovalPlan
{
    public ModelSnapshot Model { get; set; } = new();
    public List<LinkRemovalItem> Targets { get; set; } = [];
    public List<LinkRemovalItem> Affected { get; set; } = [];
    public List<string> Notices { get; set; } = [];
    public string BlockReason { get; set; } = "";
    public int DependentCount { get { var ids = Targets.Select(t => t.UniqueId).ToHashSet(); return Affected.Count(x => !ids.Contains(x.UniqueId)); } }

    public void Validate(bool dependentsConfirmed)
    {
        if (BlockReason.Length > 0) throw new InvalidOperationException(BlockReason);
        if (string.IsNullOrWhiteSpace(Model.Path) || string.IsNullOrWhiteSpace(Model.Identity) || string.IsNullOrWhiteSpace(Model.CanonicalPath))
            throw new InvalidOperationException("삭제할 모델의 식별 정보가 없습니다. 다시 검사하세요.");
        if (Targets.Count == 0 || !Targets.Any(t => t.Root) || Affected.Count == 0)
            throw new InvalidOperationException("삭제 대상이 없거나 삭제 영향 검사가 완료되지 않았습니다.");
        foreach (var set in new[] { Targets, Affected })
            if (set.Any(x => x.UniqueId.Length == 0 || x.ElementId <= 0 || x.Revision.Length == 0) ||
                set.Select(x => x.UniqueId).Distinct().Count() != set.Count || set.Select(x => x.ElementId).Distinct().Count() != set.Count)
                throw new InvalidOperationException("삭제 대상 식별 정보가 없거나 중복됩니다. 다시 검사하세요.");
        var affectedIds = Affected.Select(a => a.UniqueId).ToHashSet();
        if (Targets.Any(t => !affectedIds.Contains(t.UniqueId)))
            throw new InvalidOperationException("일부 링크 정의가 삭제 영향 목록에 없습니다. 완전 제거를 확인할 수 없습니다.");
        if (DependentCount > 0 && !dependentsConfirmed)
            throw new InvalidOperationException("함께 삭제되는 종속 요소의 추가 확인이 필요합니다.");
    }

    public static void CheckSame(IEnumerable<LinkRemovalItem> expected, IEnumerable<LinkRemovalItem> actual)
    {
        static string Signature(IEnumerable<LinkRemovalItem> rows) => JsonSerializer.Serialize(rows.OrderBy(r => r.UniqueId, StringComparer.Ordinal));
        if (Signature(expected) != Signature(actual)) throw new InvalidOperationException("검사 후 링크 또는 삭제 영향 대상이 변경되었습니다. 다시 검사하세요.");
    }
}
