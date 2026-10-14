using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;

namespace SunnyParameterManager.Bridge;

internal sealed class ParameterReader(Document doc, ModelSnapshot model)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public List<string> Notices { get; } = [];

    public List<ParameterRow> ReadAll()
    {
        var result = new List<ParameterRow>();
        var elements = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>()
            .Concat(new FilteredElementCollector(doc).WhereElementIsNotElementType().Cast<Element>())
            .Where(e => e.Category?.CategoryType == CategoryType.Model && e is not RevitLinkInstance && e is not RevitLinkType)
            .Concat(doc.ProjectInformation == null ? [] : new Element[] { doc.ProjectInformation });
        int errors = 0;
        foreach (var element in elements.DistinctBy(e => e.UniqueId))
        {
            var parameters = element.Parameters.Cast<Parameter>().ToList();
            // 소속 작업세트는 일부 요소의 열거에 없더라도 직접 접근이 지원된다.
            if (element is not ElementType && element is not ProjectInfo && doc.IsWorkshared)
            {
                var workset = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (workset != null && parameters.All(p => p.Id != workset.Id)) parameters.Add(workset);
            }
            foreach (var p in parameters)
            {
                try { if (p.Definition != null) result.Add(Read(element, p)); }
                catch (Exception ex) { errors++; if (Notices.Count < 5) Notices.Add($"요소 {element.Id.Value}: 일부 매개변수 조회 실패 — {ex.Message}"); }
            }
        }
        if (errors > 0) Notices.Add($"매개변수 {errors:N0}개는 읽기에 실패하여 제외했습니다.");
        Notices.Add("모델 카테고리의 유형·인스턴스 및 프로젝트 정보를 조회합니다. 뷰·시트·연결된 모델 내부는 별도 파일 등록 대상입니다.");
        return result;
    }

    public ParameterRow Read(Element owner, Parameter p)
    {
        var type = owner as ElementType ?? doc.GetElement(owner.GetTypeId()) as ElementType;
        var spec = p.Definition.GetDataType();
        var r = new ParameterRow
        {
            ModelPath = model.Path, ModelTitle = model.Title, OwnerId = owner.UniqueId, ElementId = owner.Id.Value.ToString(Invariant),
            Scope = owner is ProjectInfo ? "프로젝트 정보" : owner is ElementType ? "유형" : "인스턴스",
            Category = owner is ProjectInfo ? "프로젝트 정보" : owner.Category?.Name ?? "기타",
            CategoryKey = owner is ProjectInfo ? "project" : (owner.Category?.Id.Value ?? 0).ToString(Invariant),
            Family = type?.FamilyName ?? "", TypeName = owner is ProjectInfo ? "프로젝트 정보" : type?.Name ?? owner.Name ?? "",
            ParameterKey = Key(p), Name = p.Definition.Name, DataType = spec.TypeId, Storage = p.StorageType.ToString(),
            HasValue = p.HasValue, Raw = Raw(p), Display = Display(p), BlockReason = p.IsReadOnly ? "읽기 전용 — 원본 Revit에서 변경 불가" : ""
        };
        if (p.StorageType == StorageType.String) r.Editor = "텍스트";
        else if (p.StorageType == StorageType.Integer && spec == SpecTypeId.Boolean.YesNo)
        {
            r.Editor = "예/아니요"; r.Display = p.HasValue ? p.AsInteger() == 1 ? "예" : "아니요" : "";
            r.ChoiceCatalog = "bool";
            model.Catalogs.TryAdd("bool", [new("0", "아니요"), new("1", "예")]);
        }
        else if (p.Id.Value == (long)BuiltInParameter.ELEM_PARTITION_PARAM && p.StorageType == StorageType.Integer && doc.IsWorkshared)
        {
            r.Editor = "선택"; r.ChoiceCatalog = "workset";
            if (!model.Catalogs.ContainsKey(r.ChoiceCatalog)) model.Catalogs[r.ChoiceCatalog] = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => new ValueChoice(w.UniqueId.ToString("D"), w.Name)).OrderBy(x => x.Name).ToList();
            r.Display = p.HasValue ? doc.GetWorksetTable().GetWorkset(new WorksetId(p.AsInteger()))?.Name ?? r.Display : "";
        }
        else if (p.StorageType == StorageType.Integer && p.Id.Value > 0 && spec == SpecTypeId.Int.Integer) r.Editor = "정수";
        else if (p.StorageType == StorageType.Double && spec == SpecTypeId.Number) r.Editor = "숫자";
        else if (p.StorageType == StorageType.Double && UnitUtils.IsMeasurableSpec(spec))
        {
            r.Editor = "단위 숫자";
            r.Unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId().TypeId;
        }
        else if (p.StorageType == StorageType.ElementId && spec == SpecTypeId.Reference.Material)
        {
            r.Editor = "선택"; r.ChoiceCatalog = "material";
            if (!model.Catalogs.ContainsKey(r.ChoiceCatalog)) model.Catalogs[r.ChoiceCatalog] = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Select(m => new ValueChoice(m.UniqueId, m.Name)).OrderBy(x => x.Name).ToList();
        }
        else if (r.BlockReason.Length == 0) r.BlockReason = "표시 전용 — 이 참조/전용 열거값 편집기는 첫 버전에서 미지원";
        r.Choices = model.Catalogs.GetValueOrDefault(r.ChoiceCatalog, []);
        return r;
    }

    internal static string Key(Parameter p) => p.IsShared ? "guid:" + p.GUID.ToString("D") : (p.Id.Value < 0 ? "builtin:" : "id:") + p.Id.Value.ToString(Invariant);
    internal static Parameter? Resolve(Element owner, string key)
    {
        // 공유 GUID getter의 다른 소유 범위 대리값을 사용하지 않는다.
        var matches = owner.Parameters.Cast<Parameter>().Where(p => Key(p) == key).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1) return null;
        if (key.StartsWith("builtin:") && long.TryParse(key[8..], out var id) && id < 0 && id >= int.MinValue)
            return owner.get_Parameter((BuiltInParameter)id);
        return null;
    }
    internal static string Raw(Parameter p) => p.StorageType switch
    {
        StorageType.String => p.AsString() ?? "", StorageType.Integer => p.AsInteger().ToString(Invariant),
        StorageType.Double => p.AsDouble().ToString("R", Invariant), StorageType.ElementId => p.AsElementId().Value.ToString(Invariant), _ => ""
    };
    private string Display(Parameter p)
    {
        if (!p.HasValue) return "";
        if (p.StorageType == StorageType.String) return p.AsString() ?? "";
        if (p.StorageType == StorageType.ElementId) return doc.GetElement(p.AsElementId())?.Name ?? p.AsValueString() ?? Raw(p);
        return p.AsValueString() ?? Raw(p);
    }
}
