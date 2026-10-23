using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge;

// 실제 분리 사본에서 재현한 StairsRunType 이름 재생성만 보정한다.
// 유형 ID 교체·다른 시스템 유형·이름 충돌을 정상으로 간주하지 않는다.
internal static class StairTypeNames
{
    internal sealed record Expected(string UniqueId, long Id, string Family, long Category, string Name);

    internal static List<Expected> Capture(Document doc, IReadOnlyCollection<ValueEdit> edits)
    {
        var names = edits.Where(e => e.KacCleanup && e.Original.ParameterKey == CleanupPlanner.NameKey)
            .ToDictionary(e => e.Original.OwnerId, e => e.Value);
        var result = new List<Expected>();
        foreach (var group in edits.Where(e => e.Original.TypeOwnerId.Length > 0).GroupBy(e => e.Original.TypeOwnerId))
        {
            if (doc.GetElement(group.Key) is not StairsRunType type) continue;
            if (group.Any(e => e.Original.TypeName != type.Name || e.Original.Family != type.FamilyName))
                throw new InvalidOperationException("계단진행 유형의 조회 당시 이름·패밀리가 다릅니다. 다시 조회하세요: " + type.Id.Value);
            result.Add(new(type.UniqueId, type.Id.Value, type.FamilyName, type.Category?.Id.Value ?? 0, names.GetValueOrDefault(type.UniqueId, type.Name)));
        }
        return result;
    }

    internal static int Restore(Document doc, IReadOnlyCollection<Expected> expected)
    {
        var restore = new List<(StairsRunType Type, string Name)>();
        foreach (var item in expected)
        {
            if (doc.GetElement(item.UniqueId) is not StairsRunType type || type.Id.Value != item.Id ||
                type.FamilyName != item.Family || (type.Category?.Id.Value ?? 0) != item.Category)
                throw new InvalidOperationException("계단진행 유형의 식별 정보가 달라 이름을 복원하지 않습니다: " + item.Id);
            if (type.Name == item.Name) continue;
            if (!type.CanBeRenamed) throw new InvalidOperationException("계단진행 유형의 승인한 이름을 보존할 수 없습니다: " + item.Id);
            restore.Add((type, item.Name));
        }
        if (restore.Count == 0) return 0;
        using var tx = new Transaction(doc, "계단진행 유형의 승인한 이름 보존");
        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("계단 유형 이름 보존을 시작할 수 없습니다.");
        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetForcedModalHandling(true));
        foreach (var item in restore) item.Type.Name = item.Name;
        if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit이 계단 유형 이름 보존을 확정하지 못했습니다.");
        foreach (var item in expected)
        {
            if (doc.GetElement(item.UniqueId) is not StairsRunType type || type.Id.Value != item.Id || type.Name != item.Name ||
                type.FamilyName != item.Family || (type.Category?.Id.Value ?? 0) != item.Category)
                throw new InvalidOperationException("계단진행 유형 이름 보존 후 검증이 실패했습니다: " + item.Id);
        }
        return restore.Count;
    }
}
