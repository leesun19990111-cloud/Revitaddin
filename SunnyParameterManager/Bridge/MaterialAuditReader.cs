using Autodesk.Revit.DB;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge;

// 유형 재료와 배치 요소의 재료 슬롯을 읽기만 한다. 동일 유형·슬롯·재료는 수량으로 묶는다.
internal static class MaterialAuditReader
{
    internal static List<MaterialAuditItem> Read(Document doc, string model)
    {
        var found = new Dictionary<string, MaterialAuditItem>(StringComparer.Ordinal);
        void Add(ElementType type, Element owner, string issue, string scope, string slot, string material, string detail = "", int count = 1)
        {
            var key = string.Join("\u001f", type.UniqueId, issue, scope, slot, material, detail);
            if (!found.TryGetValue(key, out var row))
            {
                row = new MaterialAuditItem { Model = model, Category = type.Category?.Name ?? "", Family = type.FamilyName ?? "",
                    TypeName = type.Name ?? "", TypeId = type.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Issue = issue, Scope = scope, Slot = slot, Material = material, Detail = detail,
                    ExampleElementId = owner.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                found.Add(key, row);
            }
            row.ElementCount += count;
        }
        void MaterialCheck(ElementType type, Element owner, string scope, string slot, ElementId id, int count = 1)
        {
            if (id == ElementId.InvalidElementId) return;
            if (doc.GetElement(id) is not Material material)
            {
                Add(type, owner, "재료 확인 필요", scope, slot, "", "재료가 지정되지 않았거나 참조를 찾지 못했습니다", count);
                return;
            }
            if (!material.Name.StartsWith("KAC-", StringComparison.Ordinal))
                Add(type, owner, "KAC- 이외 재료", scope, slot, material.Name, count: count);
        }
        static IEnumerable<Parameter> MaterialParameters(Element e) => e.Parameters.Cast<Parameter>()
            .Where(p => p.StorageType == StorageType.ElementId && p.Definition.GetDataType() == SpecTypeId.Reference.Material);

        var types = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
            .Where(t => t.Category?.CategoryType == CategoryType.Model && t is not RevitLinkType).ToList();
        var byId = types.ToDictionary(t => t.Id.Value);
        // 실제 배치된 요소로 유형의 사용 여부와 수량을 먼저 확인한다. 미배치 유형은 보고하지 않는다.
        var placed = new Dictionary<long, (Element Example, int Count)>();
        foreach (var instance in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (instance.Category?.CategoryType != CategoryType.Model || instance is RevitLinkInstance ||
                !byId.TryGetValue(instance.GetTypeId().Value, out var type)) continue;
            var typeId = type.Id.Value;
            placed[typeId] = placed.TryGetValue(typeId, out var prior) ? (prior.Example, prior.Count + 1) : (instance, 1);
            try
            {
                foreach (var p in MaterialParameters(instance))
                {
                    var name = p.Definition.Name;
                    var id = p.HasValue ? p.AsElementId() : ElementId.InvalidElementId;
                    var currentMaterial = id != ElementId.InvalidElementId && doc.GetElement(id) is Material assigned ? assigned.Name : "";
                    Add(type, instance, "인스턴스 재료 매개변수", "인스턴스 매개변수", name, currentMaterial, "유형이 아닌 배치 요소에서 재료 선택 가능");
                    if (p.HasValue) MaterialCheck(type, instance, "인스턴스 매개변수", name, id);
                }
                foreach (var id in instance.GetMaterialIds(false)) MaterialCheck(type, instance, "인스턴스 형상", "형상 재료", id);
                foreach (var id in instance.GetMaterialIds(true)) MaterialCheck(type, instance, "페인트", "페인트 재료", id);
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex) { Add(type, instance, "재료 확인 필요", "인스턴스", "조회", "", ex.Message); }
        }
        foreach (var type in types)
        {
            if (!placed.TryGetValue(type.Id.Value, out var use)) continue;
            try
            {
                foreach (var p in MaterialParameters(type))
                    if (p.HasValue) MaterialCheck(type, use.Example, "유형 매개변수", p.Definition.Name, p.AsElementId(), use.Count);
                if (type is HostObjAttributes host)
                {
                    using var structure = host.GetCompoundStructure();
                    if (structure != null)
                        for (var i = 0; i < structure.LayerCount; i++) MaterialCheck(type, use.Example, "복합구조 층", $"{i + 1}층", structure.GetMaterialId(i), use.Count);
                }
                foreach (var id in type.GetMaterialIds(false)) MaterialCheck(type, use.Example, "유형 형상", "형상 재료", id, use.Count);
            }
            catch (OutOfMemoryException) { throw; }
            catch (Exception ex) { Add(type, use.Example, "재료 확인 필요", "유형", "조회", "", ex.Message, use.Count); }
        }
        return found.Values.OrderBy(x => x.Model, StringComparer.Ordinal).ThenBy(x => x.Category, StringComparer.Ordinal)
            .ThenBy(x => x.Family, StringComparer.Ordinal).ThenBy(x => x.TypeName, StringComparer.Ordinal)
            .ThenBy(x => x.Issue, StringComparer.Ordinal).ThenBy(x => x.Slot, StringComparer.Ordinal).ToList();
    }
}
