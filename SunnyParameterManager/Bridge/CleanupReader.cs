using Autodesk.Revit.DB;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge;

internal sealed class CleanupReader(Document doc)
{
    private readonly Dictionary<string, (double? Width, string Basis, string Issue)> _thickness = [];
    internal CleanupEvidence Evidence(Element owner)
    {
        var type = owner as ElementType ?? doc.GetElement(owner.GetTypeId()) as ElementType;
        var result = new CleanupEvidence { Version = owner.VersionGuid.ToString("D"), InPlace = type is FamilySymbol symbol && symbol.Family.IsInPlace, CanRename = owner is ElementType et && et.CanBeRenamed };
        if (type != null && owner is ElementType && CleanupPlanner.IsKac(type.Name))
        {
            if (!_thickness.TryGetValue(type.UniqueId, out var thickness)) _thickness[type.UniqueId] = thickness = Thickness(type);
            result.ThicknessMm = thickness.Width; result.ThicknessBasis = thickness.Basis; result.ThicknessIssue = thickness.Issue;
        }
        return result;
    }
    internal ParameterRow NameRow(ParameterRow owner)
    {
        var row = System.Text.Json.JsonSerializer.Deserialize<ParameterRow>(System.Text.Json.JsonSerializer.Serialize(owner))!;
        row.ParameterKey = CleanupPlanner.NameKey; row.Name = "유형명 정리"; row.Storage = "String"; row.DataType = "sunny:type-name";
        row.Raw = row.Display = owner.TypeName; row.HasValue = true;
        row.BlockReason = "유형 정리 전용 — 룰셋 변경 검토에서 확인";
        return row;
    }
    internal static (double? Width, string Basis, string Issue) Thickness(ElementType type)
    {
        try
        {
            if (type is HostObjAttributes host)
            {
                using var structure = host.GetCompoundStructure();
                if (structure == null) return (null, "", "복합 구조 총 두께 없음");
                if (structure.IsVerticallyCompound && !structure.IsVerticallyHomogeneous()) return (null, "", "높이에 따라 두께가 다른 복합 구조");
                if (structure.VariableLayerIndex >= 0) return (null, "", "가변 두께 레이어 — 유형 두께 확정 불가");
                return Width(structure.GetWidth(), "복합 구조 전체 두께(mm)");
            }
            if (type is not FamilySymbol) return (null, "", "총 두께 지원 유형 아님");
            var candidates = type.Parameters.Cast<Parameter>().Where(p => new[] { "총 두께", "전체 두께", "두께", "Total Thickness", "Thickness" }.Contains(p.Definition?.Name) && p.StorageType == StorageType.Double && p.Definition!.GetDataType() == SpecTypeId.Length).ToList();
            if (candidates.Count != 1 || !candidates[0].HasValue) return (null, "", "유형의 길이형 두께 매개변수가 없거나 여러 개임 — 인스턴스/형상으로 추정하지 않음");
            return Width(candidates[0].AsDouble(), "유형 매개변수: " + candidates[0].Definition.Name + " / " + ParameterReader.Key(candidates[0]));
        }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex) { return (null, "", "두께 판독 실패: " + ex.Message); }
    }
    private static (double?, string, string) Width(double feet, string basis)
    {
        var mm = UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        return double.IsFinite(mm) && mm > 0 ? (mm, basis, "") : (null, "", "0 또는 유효하지 않은 총 두께");
    }
}
