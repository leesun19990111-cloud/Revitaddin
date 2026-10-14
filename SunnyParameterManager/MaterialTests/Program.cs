using Autodesk.Revit.DB;
using SunnyParameterManager.Bridge;
using SunnyParameterManager.Core;

var doc = new Document();
doc.Materials[1] = new Material { UniqueId = "mat-1", Name = "KAC-외장" };
doc.Materials[2] = new Material { UniqueId = "mat-2", Name = "KAC-내장" };
var count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); count++; Console.WriteLine("PASS " + name); }
CompoundStructure Structure() => new() { Layers = [new(new(1), 1), new(new(2), 2)] };
var wall = new WallType { Structure = Structure() };
var first = OuterMaterialReader.Read(doc, wall);
Check(first.Name == "KAC-외장" && first.Basis == "벽 외측층", "실제 리더 벽 외측층");
Check(OuterMaterialReader.Read(doc, new FloorType { Structure = Structure() }).Name == "KAC-외장", "실제 리더 바닥 상단층");
Check(OuterMaterialReader.Read(doc, new RoofType { Structure = Structure() }).Name == "KAC-외장", "실제 리더 지붕 상단층");
Check(OuterMaterialReader.Read(doc, new CeilingType { Structure = Structure() }).Name == "KAC-내장", "실제 리더 천장 하단층");
wall.Structure!.Layers[0].Width = 0;
Check(OuterMaterialReader.Read(doc, wall).Name == "KAC-외장", "두께 0의 최외곽 막층 유지");
Check(OuterMaterialReader.Read(doc, wall).Fingerprint != first.Fingerprint, "층 구성 변경 근거 감지");
wall.Structure = Structure(); doc.Materials[1].Name = "KAC-교체";
Check(OuterMaterialReader.Read(doc, wall).Fingerprint != first.Fingerprint, "재료 이름 변경 근거 감지");
doc.Materials[1].Name = "KAC-외장"; wall.Structure.Layers[0] = new(new(-1), 1);
Check(OuterMaterialReader.Read(doc, wall).Reason.Contains("안쪽"), "미지정 외층을 내층으로 대체 금지");
wall.Structure = Structure(); wall.Structure.IsVerticallyCompound = true;
Check(OuterMaterialReader.Read(doc, wall).Reason.Contains("수직"), "수직 복합벽 외측 추정 금지");
wall.Structure.Homogeneous = true;
Check(OuterMaterialReader.Read(doc, wall).Name == "KAC-외장", "수직 복합 플래그라도 높이 전체가 같은 평행층이면 외측 판독");
var family = new ElementType { Parameters = [new(1)] };
Check(OuterMaterialReader.Read(doc, family).Name == "KAC-외장", "일반 유형 단일 재료");
family.Parameters.Add(new(1));
Check(OuterMaterialReader.Read(doc, family).Name == "KAC-외장", "같은 재료 중복 지정은 단일 재료");
family.Parameters.Add(new(2));
Check(OuterMaterialReader.Read(doc, family).Reason.Contains("여러"), "일반 유형 복수 재료 외측 추정 금지");
family.Parameters = [new(1), new(-1)];
Check(OuterMaterialReader.Read(doc, family).Reason.Contains("미지정"), "미지정 유형 재료 혼입 차단");
family.Parameters = [new(1)]; family.GeometryMaterials = [new(2)];
Check(OuterMaterialReader.Read(doc, family).Reason.Contains("여러"), "유형 형상과 매개변수의 복수 재료 확인");
family.Parameters.Clear(); family.GeometryMaterials.Clear();
Check(OuterMaterialReader.Read(doc, family).Reason.Contains("없음"), "재료 없는 유형 유지");
Check(OuterMaterialReader.Read(doc, new Element()).Reason.Contains("유형"), "인스턴스 재료 추정 차단");
family.FailRead = true;
Check(OuterMaterialReader.Read(doc, family).Reason.Contains("실패"), "재료 읽기 실패는 보류 이유 제공");
Console.WriteLine($"TOTAL {count} PASS · 실제 리더 코드의 API 대역 검사이며 Revit 실파일 검증 아님");

namespace Autodesk.Revit.DB
{
 public record ElementId(long Value);
 public enum StorageType { ElementId }
 public class Definition { public object GetDataType() => SpecTypeId.Reference.Material; }
 public static class SpecTypeId { public static class Reference { public static object Material { get; } = new(); } }
 public class Parameter(long id)
 { public StorageType StorageType => StorageType.ElementId; public Definition Definition { get; } = new(); public bool HasValue => true; public ElementId AsElementId() => new(id); }
 public class Element { public string UniqueId { get; set; } = "type-1"; }
 public class ElementType : Element
 {
  public List<Parameter> Parameters { get; set; } = []; public List<ElementId> GeometryMaterials { get; set; } = []; public bool FailRead;
  public IList<ElementId> GetMaterialIds(bool paint) => FailRead ? throw new Exception("대역 오류") : GeometryMaterials;
 }
 public class HostObjAttributes : ElementType { public CompoundStructure? Structure; public CompoundStructure? GetCompoundStructure() => Structure; }
 public class WallType : HostObjAttributes { }
 public class FloorType : HostObjAttributes { }
 public class RoofType : HostObjAttributes { }
 public class CeilingType : HostObjAttributes { }
 public class Material : Element { public string Name { get; set; } = ""; }
 public class Document { public Dictionary<long, Material> Materials { get; } = []; public Element? GetElement(ElementId id) => Materials.GetValueOrDefault(id.Value); }
 public class CompoundStructure : IDisposable { public List<CompoundStructureLayer> Layers = []; public int LayerCount => Layers.Count; public bool IsVerticallyCompound, Homogeneous; public bool IsVerticallyHomogeneous() => Homogeneous; public IList<CompoundStructureLayer> GetLayers() => Layers; public void Dispose() { } }
 public class CompoundStructureLayer(ElementId id, double width) { public ElementId MaterialId = id; public double Width = width; public int Function = 1; }
}
namespace SunnyParameterManager.Bridge
{
 internal static class ParameterReader { public static string Key(Parameter p) => "id:" + p.AsElementId().Value; }
}
