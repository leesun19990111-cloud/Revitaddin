// 실제 ParameterReader 코드를 실행하기 위한 대역. 실제 RVT 적용 검증을 대신하지 않는다.
using SunnyParameterManager.Core;
namespace Autodesk.Revit.DB
{
 public enum CategoryType { Model, Annotation, Internal }
 public enum ViewType { Undefined, FloorPlan, CeilingPlan, Elevation, Section, Detail, DraftingView, Legend, Schedule, ThreeD, DrawingSheet, Internal, ProjectBrowser, SystemBrowser }
 public enum StorageType { String, Integer, Double, ElementId }
 public enum BuiltInParameter { ELEM_PARTITION_PARAM = -1002053 }
 public enum WorksetKind { UserWorkset }
 public enum CheckoutStatus { NotOwned, OwnedByOtherUser }
 public static class WorksharingUtils { public static CheckoutStatus GetCheckoutStatus(Document doc, ElementId id) => doc.OtherOwner ? CheckoutStatus.OwnedByOtherUser : CheckoutStatus.NotOwned; }
 public static class GroupTypeId { public static ForgeTypeId IdentityData { get; } = new("identity"); }
 public static class UnitTypeId { public static ForgeTypeId Millimeters { get; } = new("mm"); }
 public sealed record ElementId(long Value) { public static ElementId InvalidElementId { get; } = new(-1); }
 public sealed record ForgeTypeId(string TypeId);
 public static class SpecTypeId
 {
  public static ForgeTypeId Length { get; } = new("length");
  public static ForgeTypeId Number { get; } = new("number");
  public static class Boolean { public static ForgeTypeId YesNo { get; } = new("bool"); }
  public static class Int { public static ForgeTypeId Integer { get; } = new("int"); }
  public static class Reference { public static ForgeTypeId Material { get; } = new("material"); }
 }
 public class Definition { public string Name { get; set; } = "뷰 구분-1"; public ForgeTypeId Spec { get; set; } = new("text"); public ForgeTypeId Group { get; set; } = new("other"); public ForgeTypeId GetDataType() => Spec; public ForgeTypeId GetGroupTypeId() => Group; }
 public class InternalDefinition : Definition { public bool VariesAcrossGroups { get; set; } }
 public sealed class Parameter
 {
  public ElementId Id { get; set; } = new(42); public Definition Definition { get; set; } = new();
  public StorageType StorageType { get; set; } = StorageType.String;
  public bool IsReadOnly { get; set; } public bool HasValue { get; set; } = true; public bool IsShared { get; set; }
  public Guid GUID { get; set; } = Guid.NewGuid(); public string Value { get; set; } = "기존"; public ElementId MaterialId { get; set; } = new(1);
  public bool FailRead { get; set; } public bool FailMemory { get; set; }
  public bool Set(string v) { if (IsReadOnly) throw new InvalidOperationException(); Value = v; return true; }
  public bool Set(int v) => Set(v.ToString());
  public string AsString() => FailMemory ? throw new OutOfMemoryException() : FailRead ? throw new InvalidOperationException("값 조회 실패") : Value; public string AsValueString() => Value; public int AsInteger() => int.TryParse(Value, out var n) ? n : 1; public double AsDouble() => double.TryParse(Value, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 1; public ElementId AsElementId() => MaterialId;
 }
 public sealed class Category { public CategoryType CategoryType { get; set; } = CategoryType.Model; public ElementId Id { get; set; } = new(1); public string Name { get; set; } = "모델"; }
 public class Element
 {
  private static long _next;
  public ElementId Id { get; } = new(++_next); public string UniqueId { get; } = Guid.NewGuid().ToString();
  public virtual string Name { get; set; } = "대상"; public Category? Category { get; set; } = new();
  public Guid VersionGuid { get; set; } = Guid.NewGuid();
  public bool FailGroupRead { get; set; }
  private ElementId _groupId = ElementId.InvalidElementId;
  public ElementId GroupId { get => FailGroupRead ? throw new InvalidOperationException("그룹 조회 실패") : _groupId; set => _groupId = value; }
  public List<Parameter> Parameters { get; } = [new()]; public ElementId TypeId { get; set; } = ElementId.InvalidElementId;
  public List<ElementId> MaterialIds { get; } = []; public List<ElementId> PaintMaterialIds { get; } = [];
  public ICollection<ElementId> GetMaterialIds(bool returnPaintMaterials) => returnPaintMaterials ? PaintMaterialIds : MaterialIds;
  public ElementId GetTypeId() => TypeId;
  public Parameter? get_Parameter(BuiltInParameter id) => Parameters.SingleOrDefault(p => p.Id.Value == (long)id);
 }
 public class ElementType : Element { public string FamilyName { get; set; } = "패밀리"; public bool CanBeRenamed { get; set; } = true; }
 public class Group : Element { }
 public class Family { public bool IsInPlace { get; set; } }
 public class FamilySymbol : ElementType { public Family Family { get; } = new(); }
 public class HostObjAttributes : ElementType { public CompoundStructure? Structure { get; set; } = new(); public CompoundStructure? GetCompoundStructure() => Structure; }
 public class CompoundStructure : IDisposable { public double Width { get; set; } = 1; public bool IsVerticallyCompound { get; set; } public bool Homogeneous { get; set; } = true; public int VariableLayerIndex { get; set; } = -1; public List<ElementId> LayerMaterials { get; } = []; public int LayerCount => LayerMaterials.Count; public ElementId GetMaterialId(int index) => LayerMaterials[index]; public bool IsVerticallyHomogeneous() => Homogeneous; public double GetWidth() => Width; public void Dispose() { } }
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
  public bool OtherOwner { get; set; } public List<Workset> Worksets { get; } = [];
  public Element? GetElement(string id) => Elements.FirstOrDefault(e => e.UniqueId == id);
  public Element? GetElement(ElementId id) => Elements.FirstOrDefault(e => e.Id == id);
  public Units GetUnits() => new(); public WorksetTable GetWorksetTable() => new(Worksets);
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
 public static class UnitUtils { public static bool IsMeasurableSpec(ForgeTypeId id) => false; public static double ConvertFromInternalUnits(double value, ForgeTypeId unit) => value * 304.8; }
 public sealed record WorksetId(int IntegerValue);
 public sealed class Workset { public string Name { get; set; } = "작업세트"; public Guid UniqueId { get; } = Guid.NewGuid(); public WorksetId Id { get; set; } = new(1); }
 public sealed class WorksetTable(List<Workset> items) { public Workset? GetWorkset(WorksetId id) => items.FirstOrDefault(w => w.Id == id) ?? new(); }
 public sealed class FilteredWorksetCollector(Document doc) : List<Workset>(doc.Worksets) { public FilteredWorksetCollector OfKind(WorksetKind kind) => this; }
}
namespace SunnyParameterManager.Bridge
{
 internal static class OuterMaterialReader { public static int Calls; public static MaterialEvidence Read(Autodesk.Revit.DB.Document doc, Autodesk.Revit.DB.ElementType type) { Calls++; return new() { Name = "KAC-철근콘크리트", Fingerprint = "test", Basis = "대역" }; } }
}
