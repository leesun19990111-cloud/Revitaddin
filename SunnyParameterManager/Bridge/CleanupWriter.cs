using Autodesk.Revit.DB;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge;

// 룰셋 근거를 새 로컬에서 다시 계산한다. 이름 변경은 일반 매개변수 편집과 혼합하지 않는다.
internal static class CleanupWriter
{
    internal sealed record Write(Action Set, Action Verify);
    // 기존 대역 검사는 기본 고정 정책으로 검증한다. 실제 적용 요청은 항상 선택 룰셋을 전달한다.
    internal static List<Write> Prepare(Document doc, ModelSnapshot model, List<ValueEdit> edits) => Prepare(doc, model, edits, CleanupPlanner.Rules());
    internal static List<Write> Prepare(Document doc, ModelSnapshot model, List<ValueEdit> edits, RuleSet rules)
    {
        RulePlanner.Validate(rules);
        if (rules.Version != 4) throw new InvalidOperationException("유형 정리 룰셋을 다시 선택하고 변경을 검토하세요.");
        if (edits.Any(e => !e.KacCleanup || e.Mode != "입력" || e.MaterialSource != null)) throw new InvalidOperationException("유형 정리와 다른 종류의 변경을 섞어 적용할 수 없습니다.");
        var reader = new ParameterReader(doc, model);
        model.Rows = reader.ReadAll(QueryPlan.FromRules(rules)); model.ConnectCatalogs();
        var fresh = CleanupPlanner.Plan(model.Owners.Concat(model.Rows), [], rules.PreserveWorksets).Edits.ToDictionary(e => e.Original.Key);
        var approvedNames = edits.Where(e => e.Original.ParameterKey == CleanupPlanner.NameKey).ToDictionary(e => e.Original.OwnerId, e => e.Value);
        var actions = new List<Write>();
        var finalNames = new Lazy<Dictionary<(long Category, string Name), int>>(() => new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
            .GroupBy(t => (t.Category?.Id.Value ?? 0, t.Name.ToUpperInvariant())).ToDictionary(g => g.Key, g => g.Count()));
        foreach (var edit in edits)
        {
            var before = edit.Original;
            if (!fresh.TryGetValue(before.Key, out var proposed) || proposed.Value != edit.Value || proposed.ReferenceKey != edit.ReferenceKey)
                throw new InvalidOperationException(before.TypeName + " / " + before.Name + ": 조회 후 정리 규칙의 결과/참조 대상이 달라졌습니다. 다시 조회하세요.");
            var actual = proposed.Original;
            if (before.Cleanup?.Version != actual.Cleanup?.Version || before.Raw != actual.Raw || before.HasValue != actual.HasValue || before.TypeKey != actual.TypeKey || before.TypeOwnerId != actual.TypeOwnerId || before.IdentityDataParameter != actual.IdentityDataParameter || before.Cleanup?.ThicknessMm != actual.Cleanup?.ThicknessMm || before.Cleanup?.ThicknessBasis != actual.Cleanup?.ThicknessBasis)
                throw new InvalidOperationException(before.TypeName + ": 조회 이후 요소·유형·두께 근거가 달라졌습니다. 다시 조회하세요.");
            var owner = doc.GetElement(before.OwnerId) ?? throw new InvalidOperationException("대상 요소가 삭제되었습니다.");
            if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, owner.Id) == CheckoutStatus.OwnedByOtherUser)
                throw new InvalidOperationException("다른 사용자가 소유한 요소입니다: " + before.ElementId);
            bool rename = before.ParameterKey == CleanupPlanner.NameKey;
            Action set;
            string raw = edit.Value;
            if (rename)
            {
                if (owner is not ElementType) throw new InvalidOperationException("유형 이름 변경 대상이 유형이 아닙니다.");
                set = () => owner.Name = edit.Value;
            }
            else
            {
                EditPlanner.CheckFresh(before, actual);
                var p = ParameterReader.Resolve(owner, before.ParameterKey) ?? throw new InvalidOperationException("매개변수 연결 실패");
                if (before.ParameterKey == CleanupPlanner.WorksetKey)
                {
                    var w = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Single(w => w.UniqueId.ToString("D") == edit.ReferenceKey && w.Name == edit.Value);
                    raw = w.Id.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture); set = () => Require(p.Set(w.Id.IntegerValue));
                }
                else if (actual.Editor == "예/아니요") { var n = edit.Value == "예" ? 1 : 0; raw = n.ToString(); set = () => Require(p.Set(n)); }
                else if (p.StorageType == StorageType.String && before.ParameterKey == KacRulePlanner.IfcInstanceKey) set = () => Require(p.Set(edit.Value));
                else throw new InvalidOperationException("허용하지 않은 유형 정리 필드입니다.");
            }
            actions.Add(new(set, () =>
            {
                var after = doc.GetElement(before.OwnerId) ?? throw new InvalidOperationException("확정 후 대상 요소가 없어졌습니다.");
                var type = after as ElementType ?? doc.GetElement(after.GetTypeId()) as ElementType;
                var expectedName = approvedNames.GetValueOrDefault(before.TypeOwnerId, before.TypeName);
                if ((type?.UniqueId ?? "") != before.TypeOwnerId || (type?.Name ?? after.Name ?? "") != expectedName || (type?.FamilyName ?? "") != before.Family || after.Category?.Id.Value.ToString() != before.CategoryKey)
                    throw new InvalidOperationException("확정 후 승인하지 않은 유형/카테고리 변화가 발생했습니다: " + before.ElementId);
                if (rename)
                {
                    var thickness = CleanupReader.Thickness((ElementType)after);
                    if (after.Name != edit.Value || thickness.Width != before.Cleanup!.ThicknessMm || thickness.Basis != before.Cleanup.ThicknessBasis)
                        throw new InvalidOperationException("확정 후 유형 이름 또는 두께 근거가 달라졌습니다.");
                    if (finalNames.Value.GetValueOrDefault((after.Category?.Id.Value ?? 0, edit.Value.ToUpperInvariant())) != 1)
                        throw new InvalidOperationException("확정 후 동일 카테고리 유형명 중복입니다.");
                }
                else
                {
                    var p = ParameterReader.Resolve(after, before.ParameterKey) ?? throw new InvalidOperationException("확정 후 매개변수가 없어졌습니다.");
                    EditPlanner.CheckGroupFresh(before, reader.Read(after, p, false));
                    if (ParameterReader.Raw(p) != raw || p.StorageType.ToString() != before.Storage || p.Definition.GetDataType().TypeId != before.DataType || p.Definition.Name != before.Name)
                        throw new InvalidOperationException("확정 후 요청값/매개변수 식별 불일치: " + before.Name);
                    if (before.IdentityDataParameter != (p.Definition.GetGroupTypeId() == GroupTypeId.IdentityData))
                        throw new InvalidOperationException("확정 후 매개변수 그룹이 달라졌습니다.");
                    if (before.ParameterKey == CleanupPlanner.WorksetKey && !new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Any(w => w.UniqueId.ToString("D") == edit.ReferenceKey && w.Name == edit.Value && w.Id.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture) == raw))
                        throw new InvalidOperationException("확정 후 작업세트 식별/이름이 달라졌습니다.");
                }
            }));
        }
        return actions;
    }
    private static void Require(bool success) { if (!success) throw new InvalidOperationException("Revit이 유형 정리 변경을 거부했습니다."); }
}
