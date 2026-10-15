using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SunnyParameterManager.Bridge;

// 재료를 읽기만 한다. 구조재료·가변층·기능·우선순위를 포함해 층 정의를 절대 다시 만들지 않는다.
internal static class OuterMaterialReader
{
    public static MaterialEvidence Read(Document doc, Element owner)
    {
        try
        {
            if (owner is not ElementType type) return Missing("유형 재료만 지원합니다", false);
            if (type is HostObjAttributes host)
            {
                using var structure = host.GetCompoundStructure();
                if (structure != null && structure.LayerCount > 0)
                {
                    // IsVerticallyCompound만으로는 일정한 평행층 벽까지 제외된다.
                    if (structure.IsVerticallyCompound && !structure.IsVerticallyHomogeneous()) return Missing("수직 복합벽은 높이에 따라 외측 재료가 달라 확인이 필요합니다");
                    var layers = structure.GetLayers();
                    var bottom = type is CeilingType;
                    var index = bottom ? layers.Count - 1 : 0;
                    var basis = type is WallType ? "벽 외측층" : bottom ? "천장 최하단층" : "바닥·지붕 최상단층";
                    if (doc.GetElement(layers[index].MaterialId) is not Material material)
                        return Missing(basis + "의 재료가 미지정/카테고리별입니다. 안쪽 층으로 대체하지 않습니다");
                    var layerEvidence = layers.Select(l => new { Id = l.MaterialId.Value, l.Width, Function = l.Function.ToString() }).ToArray();
                    return Found(material, basis, new { Type = type.UniqueId, Index = index, Layers = layerEvidence, Material = material.UniqueId, material.Name });
                }
            }
            // 일반 패밀리에는 외측 순서가 없다. 지정된 재료가 하나로 확인될 때만 사용한다.
            var parameters = type.Parameters.Cast<Parameter>().Where(p => p.StorageType == StorageType.ElementId && p.Definition.GetDataType() == SpecTypeId.Reference.Material).ToList();
            if (parameters.Any(p => !p.HasValue || doc.GetElement(p.AsElementId()) is not Material))
                return Missing("유형의 재료 매개변수에 미지정/카테고리별 값이 있어 외측 재료를 확정할 수 없습니다");
            var ids = parameters.Select(p => p.AsElementId()).Concat(type.GetMaterialIds(false)).DistinctBy(id => id.Value).ToList();
            if (ids.Count != 1 || doc.GetElement(ids[0]) is not Material single)
                return Missing(ids.Count == 0 ? "유형에 지정된 재료 없음 — 인스턴스 재료를 임의로 대입하지 않습니다" : "일반 패밀리의 여러 재료 중 외측을 판별할 수 없습니다");
            return Found(single, "유형 단일 재료", new { Type = type.UniqueId, Material = single.UniqueId, single.Name,
                Parameters = parameters.Select(p => ParameterReader.Key(p)).OrderBy(x => x).ToArray() });
        }
        catch (Exception ex) { return Missing("재료 조회 실패: " + ex.Message, false); }
    }
    private static MaterialEvidence Missing(string reason, bool fallbackAllowed = true) => new() { Reason = reason, FallbackAllowed = fallbackAllowed };
    private static MaterialEvidence Found(Material material, string basis, object source) => new()
    {
        Name = material.Name, Basis = basis,
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(source))))
    };
}
