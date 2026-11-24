using Autodesk.Revit.DB;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge;

// 승인한 유형 이름만 새 작업 로컬에서 재검증하고, 확정 뒤 다른 유형 이름 변화도 검사한다.
internal static class TypeCodeWriter
{
    internal static List<CleanupWriter.Write> Prepare(Document doc, ModelSnapshot model, List<ValueEdit> edits)
    {
        if (edits.Any(e => !e.KacCleanup || e.Original.ParameterKey != CleanupPlanner.NameKey || e.Mode != "입력" || e.MaterialSource != null))
            throw new InvalidOperationException("유형 코드 보정 외 변경을 함께 적용할 수 없습니다.");
        var reader = new ParameterReader(doc, model);
        model.Rows = reader.ReadAll(QueryPlan.FromRules(new RuleSet { Version = 7, Name = "세 자리 유형 코드 보정", TypeCodeFix = true }));
        var fresh = TypeCodePlanner.Plan(model.Owners.Concat(model.Rows), []).Edits.ToDictionary(e => e.Original.Key);
        var beforeNames = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
            .ToDictionary(t => t.UniqueId, t => t.Name);
        var approved = edits.ToDictionary(e => e.Original.OwnerId, e => e.Value);
        var actions = new List<CleanupWriter.Write>();
        var finalNames = new Lazy<Dictionary<string, string>>(() => new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
            .ToDictionary(t => t.UniqueId, t => t.Name));
        foreach (var edit in edits)
        {
            var before = edit.Original;
            if (!fresh.TryGetValue(before.Key, out var current) || current.Value != edit.Value ||
                current.Original.Raw != before.Raw || current.Original.TypeKey != before.TypeKey ||
                current.Original.TypeOwnerId != before.TypeOwnerId || current.Original.Cleanup?.Version != before.Cleanup?.Version)
                throw new InvalidOperationException(before.TypeName + ": 조회 이후 유형 또는 보정 결과가 달라졌습니다. 다시 조회하세요.");
            if (doc.GetElement(before.OwnerId) is not ElementType type || !type.CanBeRenamed || type.Name != before.TypeName ||
                (type.Category?.Id.Value.ToString() ?? "0") != before.CategoryKey || type.FamilyName != before.Family)
                throw new InvalidOperationException(before.TypeName + ": 유형을 다시 확인할 수 없거나 이름 변경이 허용되지 않습니다.");
            if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, type.Id) == CheckoutStatus.OwnedByOtherUser)
                throw new InvalidOperationException("다른 사용자가 소유한 유형입니다: " + before.TypeName);
            actions.Add(new CleanupWriter.Write(() => type.Name = edit.Value, () =>
            {
                if (doc.GetElement(before.OwnerId) is not ElementType after || after.Name != edit.Value ||
                    (after.Category?.Id.Value.ToString() ?? "0") != before.CategoryKey || after.FamilyName != before.Family)
                    throw new InvalidOperationException("확정 후 유형 이름/소속이 승인 결과와 다릅니다: " + before.TypeName);
                var actual = finalNames.Value;
                if (actual.Count != beforeNames.Count || beforeNames.Any(kv => !actual.TryGetValue(kv.Key, out var value) ||
                    value != approved.GetValueOrDefault(kv.Key, kv.Value)))
                    throw new InvalidOperationException("확정 후 승인하지 않은 유형 이름 변화가 발생했습니다. 이번 묶음을 취소합니다.");
            }));
        }
        return actions;
    }
}
