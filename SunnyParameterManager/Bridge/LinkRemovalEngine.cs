using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SunnyParameterManager.Core;
using System.Text.Json;

namespace SunnyParameterManager.Bridge;

// 프로젝트 문서 안의 링크 정의/배치만 제거한다. 원본 외부 파일·패밀리 문서는 열거나 삭제하지 않는다.
internal static class LinkRemovalEngine
{
    private static List<Element> Elements(Document doc) => new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>()
        .Concat(new FilteredElementCollector(doc).WhereElementIsNotElementType().Cast<Element>()).ToList();

    private static string Kind(Element e) => e switch
    {
        RevitLinkType => "Revit 링크 정의", RevitLinkInstance => "Revit 링크 배치",
        CADLinkType => "CAD 링크·가져오기 정의", ImportInstance i => i.IsLinked ? "CAD 링크 배치" : "가져오기 요소",
        PointCloudType => "포인트 클라우드 정의", PointCloudInstance => "포인트 클라우드 배치",
        ImageType => "이미지·PDF 정의", ImageInstance => "이미지·PDF 배치",
        TopographyLinkType => "지형 링크 정의",
        _ => e.Category?.Id.Value == (long)BuiltInCategory.OST_Coordination_Model ? "조정 모델" : ""
    };
    private static LinkRemovalItem Describe(Element e, string kind, bool root, string source = "") => new()
    {
        UniqueId = e.UniqueId, ElementId = e.Id.Value, Revision = e.VersionGuid.ToString("D"),
        Kind = kind, Name = e.Name ?? "", Category = e.Category?.Name ?? "", Root = root, Pinned = e.Pinned, Source = source
    };

    internal static LinkRemovalPlan Collect(Document doc, ModelSnapshot model)
    {
        var plan = new LinkRemovalPlan { Model = model };
        var elements = Elements(doc); var candidates = new Dictionary<long, LinkRemovalItem>();
        var external = ExternalFileUtils.GetAllExternalFileReferences(doc).Concat(ExternalResourceUtils.GetAllExternalResourceReferences(doc)).Distinct().ToHashSet();
        var allIds = elements.Select(e => e.Id).ToHashSet();
        if (external.Any(id => !allIds.Contains(id))) plan.BlockReason = "일부 외부 참조의 요소를 확인할 수 없습니다. 완전 제거를 보장할 수 없어 보류합니다.\n";
        var nested = elements.OfType<RevitLinkType>().Where(t => t.IsNestedLink).Select(t => t.Id.Value).ToHashSet();
        foreach (var e in elements)
        {
            var kind = Kind(e); string source = "";
            if (external.Contains(e.Id))
            {
                if (e.IsExternalFileReference())
                {
                    var reference = e.GetExternalFileReference();
                    var externalKind = reference.ExternalFileReferenceType.ToString();
                    // 키노트·재료 텍스처 등은 모델 링크가 아니라 프로젝트 설정이다.
                    if (kind.Length == 0 && externalKind is "KeynoteTable" or "AssemblyCodeTable" or "Material" or "Decal" or "SystemsAnalysisReport")
                    { plan.Notices.Add("유지하는 외부 설정: " + externalKind + " / " + e.Name); continue; }
                    if (kind.Length == 0 && externalKind == "DWFMarkup") kind = "DWF 마크업";
                    source = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetPath());
                }
                if (kind.Length == 0 && e is KeynoteTable) { plan.Notices.Add("유지하는 외부 설정: 키노트"); continue; }
                if (kind.Length == 0) { plan.BlockReason += $"지원 여부를 확인할 수 없는 외부 참조: {e.Name} ({e.GetType().Name}, ID {e.Id.Value})\n"; continue; }
                var resources = e.GetExternalResourceReferences().Values.Select(r => JsonSerializer.Serialize(new { r.InSessionPath, Data = r.GetReferenceInformation().OrderBy(p => p.Key, StringComparer.Ordinal).ToList() })).OrderBy(s => s, StringComparer.Ordinal);
                source += JsonSerializer.Serialize(resources);
            }
            if (kind.Length == 0) continue;
            if (e is ImageType image) source += image.Path;
            if (e is PointCloudType pointCloud) source += pointCloud.GetPath();
            bool isNested = nested.Contains(e.Id.Value) || nested.Contains(e.GetTypeId().Value);
            candidates[e.Id.Value] = Describe(e, isNested ? kind + " (상위 링크와 함께 제거)" : kind, !isNested, source);
        }
        // 일부 가져오기 요소는 일반 ElementType으로 표현된다. 실제 소속 유형을 함께 제거한다.
        foreach (var instance in elements.OfType<ImportInstance>())
            if (doc.GetElement(instance.GetTypeId()) is ElementType type && !candidates.ContainsKey(type.Id.Value))
                candidates[type.Id.Value] = Describe(type, "가져오기 유형", true);
        var typeIds = elements.OfType<ElementType>().Where(t => candidates.ContainsKey(t.Id.Value)).Select(t => t.Id.Value).ToHashSet();
        foreach (var e in elements.Where(e => e is not ElementType && typeIds.Contains(e.GetTypeId().Value)))
            if (!candidates.ContainsKey(e.Id.Value)) candidates[e.Id.Value] = Describe(e, "링크·가져오기 배치", !nested.Contains(e.GetTypeId().Value));
        plan.Targets = candidates.Values.OrderBy(x => x.Kind).ThenBy(x => x.Name).ThenBy(x => x.ElementId).ToList();
        return plan;
    }

    internal static LinkRemovalPlan Preview(Document doc, ModelSnapshot model)
    {
        var plan = Collect(doc, model);
        if (plan.BlockReason.Length > 0 || plan.Targets.Count == 0) return plan;
        CheckOwnership(doc, plan.Targets);
        ICollection<ElementId> deleted;
        using (var tx = new Transaction(doc, "Sunny 링크 삭제 영향 검사 — 저장하지 않음"))
        {
            if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("삭제 영향 검사를 시작할 수 없습니다.");
            try { deleted = doc.Delete(plan.Targets.Where(t => t.Root).Select(t => new ElementId(t.ElementId)).ToList()); }
            finally { if (tx.GetStatus() == TransactionStatus.Started && tx.RollBack() != TransactionStatus.RolledBack) throw new InvalidOperationException("삭제 영향 검사 취소 실패. 저장하지 않고 작업을 중단합니다."); }
        }
        var targetIds = plan.Targets.Select(t => t.UniqueId).ToHashSet();
        plan.Affected = deleted.Select(id => doc.GetElement(id) ?? throw new InvalidOperationException("삭제 검사 복원 후 요소를 찾을 수 없습니다."))
            .Select(e => Describe(e, targetIds.Contains(e.UniqueId) ? "링크·가져오기" : "함께 삭제되는 종속 요소", false)).ToList();
        LinkRemovalPlan.CheckSame(plan.Targets, Collect(doc, model).Targets);
        plan.Validate(true);
        return plan;
    }

    internal sealed record RemovalAction(Action Delete, Action Verify);
    internal static RemovalAction Prepare(Document doc, ModelSnapshot model, LinkRemovalPlan expected, bool dependentsConfirmed)
    {
        expected.Validate(dependentsConfirmed);
        if (model.Identity != expected.Model.Identity || !string.Equals(model.CanonicalPath, expected.Model.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("삭제 검사와 다른 모델입니다.");
        var actual = Preview(doc, model);
        if (actual.BlockReason.Length > 0) throw new InvalidOperationException(actual.BlockReason);
        LinkRemovalPlan.CheckSame(expected.Targets, actual.Targets);
        LinkRemovalPlan.CheckSame(expected.Affected, actual.Affected);
        CheckOwnership(doc, actual.Affected);
        var before = Elements(doc).Select(e => e.Id.Value).ToHashSet();
        var expectedIds = expected.Affected.Select(e => e.ElementId).ToHashSet();
        return new(() =>
        {
            var deleted = doc.Delete(actual.Targets.Where(t => t.Root).Select(t => new ElementId(t.ElementId)).ToList()).Select(id => id.Value).ToHashSet();
            if (!expectedIds.SetEquals(deleted)) throw new InvalidOperationException("실제 삭제 영향이 승인 목록과 다릅니다. 이번 모델을 취소합니다.");
        }, () =>
            {
                var remaining = Elements(doc).Select(e => e.Id.Value).ToHashSet();
                if (!before.Except(expectedIds).ToHashSet().SetEquals(remaining) || expected.Affected.Any(e => doc.GetElement(e.UniqueId) != null))
                    throw new InvalidOperationException("확정 후 삭제 결과가 승인 목록과 다릅니다. 저장하지 않습니다.");
                CheckComplete(doc, model);
            });
    }
    internal static void CheckComplete(Document doc, ModelSnapshot model)
    {
        var after = Collect(doc, model);
        if (after.Targets.Count > 0 || after.BlockReason.Length > 0)
            throw new InvalidOperationException("링크 정의가 남아 있거나 새 외부 참조가 발견되어 완전 제거를 확인하지 못했습니다. 이미 저장/동기화 단계였다면 반영 결과를 먼저 확인하고 재실행하지 마세요.");
    }
    private static void CheckOwnership(Document doc, IEnumerable<LinkRemovalItem> items)
    {
        if (doc.IsWorkshared && items.Any(e => WorksharingUtils.GetCheckoutStatus(doc, new ElementId(e.ElementId)) == CheckoutStatus.OwnedByOtherUser))
            throw new InvalidOperationException("삭제 대상 중 다른 사용자가 소유한 요소가 있습니다. 강제로 가져오지 않습니다.");
    }
}
