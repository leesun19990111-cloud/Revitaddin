// 실제 ParameterReader 코드를 실행하기 위한 대역. 실제 RVT 적용 검증을 대신하지 않는다.
using SunnyParameterManager.Core;
namespace Autodesk.Revit.DB
{
 public enum CategoryType { Model, Annotation, Internal }
 public enum ViewType { Undefined, FloorPlan, CeilingPlan, Elevation, Section, Detail, DraftingView, Legend, Schedule, ThreeD, DrawingSheet, Internal, ProjectBrowser, SystemBrowser }
 public enum StorageType { String, Integer, Double, ElementId }
 public enum BuiltInParameter { ELEM_PARTITION_PARAM = -100 }
 public enum WorksetKind { UserWorkset }
 public sealed record ElementId(long Value) { public static ElementId InvalidElementId { get; } = new(-1); }
 public sealed record ForgeTypeId(string TypeId);
 public static class SpecTypeId
 {
  public static ForgeTypeId Number { get; } = new("number");
  public static class Boolean { public static ForgeTypeId YesNo { get; } = new("bool"); }
  public static class Int { public static ForgeTypeId Integer { get; } = new("int"); }
  public static class Reference { public static ForgeTypeId Material { get; } = new("material"); }
 }
 public sealed class Definition { public string Name { get; set; } = "뷰 구분-1"; public ForgeTypeId Spec { get; set; } = new("text"); public ForgeTypeId GetDataType() => Spec; }
 public sealed class Parameter
 {
  public ElementId Id { get; set; } = new(42); public Definition Definition { get; set; } = new();
  public StorageType StorageType { get; set; } = StorageType.String;
  public bool IsReadOnly { get; set; } public bool HasValue { get; set; } = true; public bool IsShared { get; set; }
  public Guid GUID { get; set; } = Guid.NewGuid(); public string Value { get; set; } = "기존";
  public bool FailRead { get; set; } public bool FailMemory { get; set; }
  public string AsString() => FailMemory ? throw new OutOfMemoryException() : FailRead ? throw new InvalidOperationException("값 조회 실패") : Value; public string AsValueString() => Value; public int AsInteger() => 1; public double AsDouble() => 1; public ElementId AsElementId() => new(1);
 }
 public sealed class Category { public CategoryType CategoryType { get; set; } = CategoryType.Model; public ElementId Id { get; set; } = new(1); public string Name { get; set; } = "모델"; }
 public class Element
 {
  private static long _next;
  public ElementId Id { get; } = new(++_next); public string UniqueId { get; } = Guid.NewGuid().ToString();
  public virtual string Name { get; set; } = "대상"; public Category? Category { get; set; } = new();
  public List<Parameter> Parameters { get; } = [new()]; public ElementId TypeId { get; set; } = ElementId.InvalidElementId;
  public ElementId GetTypeId() => TypeId;
  public Parameter? get_Parameter(BuiltInParameter id) => Parameters.SingleOrDefault(p => p.Id.Value == (long)id);
 }
 public class ElementType : Element { public string FamilyName { get; set; } = "패밀리"; }
 public sealed class ProjectInfo : Element { }
 public sealed class RevitLinkType : ElementType { }
 public sealed class RevitLinkInstance : Element { }
 public sealed class Material : Element { }
 public class View : Element
 {
  public View() { Category = new() { CategoryType = CategoryType.Internal, Name = "뷰" }; }
  public bool IsTemplate { get; set; } public ViewType ViewType { get; set; } = ViewType.FloorPlan;
  public ElementId ViewTemplateId { get; set; } = ElementId.InvalidElementId;
  public List<ElementId> TemplateParameters { get; } = []; public List<ElementId> NonControlled { get; } = [];
  public bool FailTemplateRead { get; set; }
  public ICollection<ElementId> GetTemplateParameterIds() => FailTemplateRead ? throw new InvalidOperationException("대역 읽기 실패") : TemplateParameters;
  public ICollection<ElementId> GetNonControlledTemplateParameterIds() => NonControlled;
 }
 public sealed class ViewSheet : View { public string SheetNumber { get; set; } = "A-001"; public ViewSheet() { ViewType = ViewType.DrawingSheet; } }
 public sealed class Document
 {
  public List<Element> Elements { get; } = []; public ProjectInfo? ProjectInformation { get; set; } public bool IsWorkshared { get; set; }
  public Element? GetElement(ElementId id) => Elements.FirstOrDefault(e => e.Id == id);
  public Units GetUnits() => new(); public WorksetTable GetWorksetTable() => new();
 }
 public sealed class FilteredElementCollector(Document doc) : IEnumerable<Element>
 {
  private IEnumerable<Element> _items = doc.Elements;
  public FilteredElementCollector WhereElementIsElementType() { _items = _items.Where(e => e is ElementType); return this; }
  public FilteredElementCollector WhereElementIsNotElementType() { _items = _items.Where(e => e is not ElementType); return this; }
  public FilteredElementCollector OfClass(Type type) { _items = _items.Where(e => type.IsInstanceOfType(e)); return this; }
  public IEnumerator<Element> GetEnumerator() => _items.GetEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
 }
 public sealed class Units { public FormatOptions GetFormatOptions(ForgeTypeId id) => new(); }
 public sealed class FormatOptions { public ForgeTypeId GetUnitTypeId() => new("unit"); }
 public static class UnitUtils { public static bool IsMeasurableSpec(ForgeTypeId id) => false; }
 public sealed record WorksetId(int IntegerValue);
 public sealed class Workset { public string Name { get; set; } = "작업세트"; public Guid UniqueId { get; } = Guid.NewGuid(); }
 public sealed class WorksetTable { public Workset? GetWorkset(WorksetId id) => new(); }
 public sealed class FilteredWorksetCollector(Document doc) : List<Workset> { public FilteredWorksetCollector OfKind(WorksetKind kind) => this; }
}
namespace SunnyParameterManager.Bridge
{
 internal static class OuterMaterialReader { public static int Calls; public static MaterialEvidence Read(Autodesk.Revit.DB.Document doc, Autodesk.Revit.DB.ElementType type) { Calls++; return new() { Name = "KAC-철근콘크리트", Fingerprint = "test", Basis = "대역" }; } }
}
