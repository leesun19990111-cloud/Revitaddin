using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;

namespace SunnyParameterManager.Bridge;

internal sealed class ParameterReader(Document doc, ModelSnapshot model)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public List<string> Notices { get; } = [];
    private readonly Dictionary<string, MaterialEvidence> _materials = [];
    public MaterialEvidence ReadMaterial(Element owner, bool fresh = false)
    {
        var type = owner as ElementType ?? doc.GetElement(owner.GetTypeId()) as ElementType;
        if (type == null) return new() { Reason = "요소의 원본 유형을 확인할 수 없습니다" };
        if (!fresh && _materials.TryGetValue(type.UniqueId, out var material)) return material;
        var result = OuterMaterialReader.Read(doc, type);
        if (!fresh) _materials[type.UniqueId] = result;
        return result;
    }

    public List<ParameterRow> ReadAll(QueryPlan? query = null)
    {
        query?.Validate(); model.Query = query; model.Owners.Clear();
        var result = new List<ParameterRow>();
        // 프로젝트 정보만 필요하면 모델 전체의 요소 수집을 하지 않는다.
        var elements = (query == null || query.Scopes.Contains("유형") ? new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>() : Enumerable.Empty<Element>())
            .Concat(query == null || query.Scopes.Contains("인스턴스") ? new FilteredElementCollector(doc).WhereElementIsNotElementType().Cast<Element>() : Enumerable.Empty<Element>())
            .Where(e => e is View v ? !v.IsTemplate && v.ViewType is not (ViewType.Internal or ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Undefined) :
                e.Category?.CategoryType == CategoryType.Model && e is not RevitLinkInstance && e is not RevitLinkType)
            .Concat(doc.ProjectInformation == null || query != null && !query.Scopes.Contains("프로젝트 정보") ? [] : new Element[] { doc.ProjectInformation });
        int errors = 0;
        foreach (var element in elements.DistinctBy(e => e.UniqueId))
        {
            if (query != null)
            {
                var owner = Describe(element, query.IncludeOuterMaterial);
                if (!query.Scopes.Contains(owner.Scope)) continue;
                model.Owners.Add(owner);
            }
            var parameters = element.Parameters.Cast<Parameter>().ToList();
            // 소속 작업세트는 일부 요소의 열거에 없더라도 직접 접근이 지원된다.
            if (element is not ElementType && element is not ProjectInfo && element is not View && doc.IsWorkshared)
            {
                var workset = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (workset != null && parameters.All(p => p.Id != workset.Id)) parameters.Add(workset);
            }
            foreach (var p in parameters)
            {
                try
                {
                    if (p.Definition != null && (query == null || query.Includes(p.Definition.Name, Key(p))))
                        result.Add(Read(element, p, query == null || query.IncludeOuterMaterial));
                }
                catch (OutOfMemoryException) { throw; } // 메모리 오류로 빠진 값을 정상 조회 결과로 넘기지 않는다.
                catch (Exception ex) when (query != null) { throw new InvalidOperationException($"요소 {element.Id.Value}: 선택 항목 조회 실패. 누락으로 간주하지 않고 해당 파일 조회를 중단합니다. {ex.Message}", ex); }
                catch (Exception ex) { errors++; if (Notices.Count < 5) Notices.Add($"요소 {element.Id.Value}: 일부 매개변수 조회 실패 — {ex.Message}"); }
            }
        }
        if (errors > 0) Notices.Add($"매개변수 {errors:N0}개는 읽기에 실패하여 제외했습니다.");
        Notices.Add(query == null ? "모델 유형·인스턴스, 프로젝트 정보, 뷰·시트의 매개변수를 조회합니다. 뷰 템플릿 자체와 내부 뷰는 제외합니다. 링크 내부는 원본 파일을 별도로 등록하세요." :
            "룰셋 선택 조회: " + string.Join(", ", query.Names) + ". 다른 항목은 미조회이며 빈 값이 아닙니다. 링크 내부는 원본 파일을 별도로 등록하세요.");
        return result;
    }

    private ParameterRow Describe(Element owner, bool material)
    {
        var type = owner as ElementType ?? doc.GetElement(owner.GetTypeId()) as ElementType;
        var r = new ParameterRow
        {
            ModelPath = model.Path, ModelTitle = model.Title, OwnerId = owner.UniqueId, ElementId = owner.Id.Value.ToString(Invariant),
            Scope = owner is ProjectInfo ? "프로젝트 정보" : owner is ViewSheet ? "시트" : owner is View ? "뷰" : owner is ElementType ? "유형" : "인스턴스",
            Category = owner is ProjectInfo ? "프로젝트 정보" : owner.Category?.Name ?? "기타",
            CategoryKey = owner is ProjectInfo ? "project" : (owner.Category?.Id.Value ?? 0).ToString(Invariant),
            Family = type?.FamilyName ?? "", TypeName = owner is ProjectInfo ? "프로젝트 정보" : type?.Name ?? (owner is View ? "" : owner.Name ?? ""),
            TypeOwnerId = type?.UniqueId ?? "",
            ElementName = owner is View ? owner.Name ?? "" : "", SheetNumber = owner is ViewSheet sheet ? sheet.SheetNumber ?? "" : "",
            BlockReason = "대상 식별 정보 — 값 편집 불가"
        };
        if (material && owner is ElementType && KacRulePlanner.TypeCode(r.TypeName).Length > 0) r.OuterMaterial = ReadMaterial(owner);
        return r;
    }

    public ParameterRow Read(Element owner, Parameter p, bool material = true)
    {
        var r = Describe(owner, material);
        var spec = p.Definition.GetDataType();
        r.ParameterKey = Key(p); r.Name = p.Definition.Name; r.DataType = spec.TypeId; r.Storage = p.StorageType.ToString();
        r.HasValue = p.HasValue; r.Raw = Raw(p); r.Display = Display(p); r.BlockReason = p.IsReadOnly ? "읽기 전용 — 원본 Revit에서 변경 불가" : "";
        if (owner is View view)
        {
            if (view.IsTemplate) r.BlockReason = "읽기 전용 — 뷰 템플릿 자체는 편집 대상이 아닙니다.";
            else if (view.ViewTemplateId != ElementId.InvalidElementId)
            {
                try
                {
                    if (doc.GetElement(view.ViewTemplateId) is not View template) r.BlockReason = "읽기 전용 — 연결된 뷰 템플릿을 확인할 수 없습니다.";
                    else if (template.GetTemplateParameterIds().Contains(p.Id) && !template.GetNonControlledTemplateParameterIds().Contains(p.Id))
                        r.BlockReason = "읽기 전용 — 뷰 템플릿에서 제어하는 값입니다.";
                }
                catch { r.BlockReason = "읽기 전용 — 뷰 템플릿 제어 여부를 확인하지 못했습니다."; }
            }
        }
        if (p.StorageType == StorageType.String) r.Editor = "텍스트";
        else if (p.StorageType == StorageType.Integer && spec == SpecTypeId.Boolean.YesNo)
        {
            r.Editor = "예/아니요"; r.Display = p.HasValue ? p.AsInteger() == 1 ? "예" : "아니요" : "";
            r.ChoiceCatalog = "bool";
            model.Catalogs.TryAdd("bool", [new("0", "아니요"), new("1", "예")]);
        }
        else if (owner is not View && p.Id.Value == (long)BuiltInParameter.ELEM_PARTITION_PARAM && p.StorageType == StorageType.Integer && doc.IsWorkshared)
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
