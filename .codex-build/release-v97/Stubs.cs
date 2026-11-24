using System.Collections;
using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public record ElementId(int Value) { public static ElementId InvalidElementId = new(-1); }
 public class Element { public List<Parameter> Parameters = new(); public Parameter? get_Parameter(BuiltInParameter p)=>null; public ElementId Id {get;set;}=new(0); public string Name {get;set;}=""; public bool IsValidObject {get;set;}=true; public virtual ElementId GetTypeId()=>ElementId.InvalidElementId; }
 public class ElementType:Element{public string FamilyName {get;set;}="";}
 public class Family:Element { public List<ElementId> Symbols=new(); public ICollection<ElementId> GetFamilySymbolIds()=>Symbols; }
 public class FamilySymbol:ElementType { public Family Family {get;set;}=null!; }
 public class FamilyInstance:Element { public FamilySymbol Symbol {get;set;}=null!; public override ElementId GetTypeId()=>Symbol.Id; }
 public class View:Element { public bool IsTemplate {get;set;} public ViewType ViewType {get;set;} }
 public class ViewSheet:View{} public class ViewSchedule:View{} public class Material:Element{public string MaterialClass {get;set;}="";}
 public enum ViewType { Legend } public enum TransactionStatus { Uninitialized, Started, Committed, RolledBack }
 public class Document { public string PathName {get;set;}="test.rvt"; public string Title=>"test"; public List<Element> Elements=new(); public Element? GetElement(ElementId id)=>Elements.FirstOrDefault(e=>e.Id==id); }
 public class FilteredElementCollector:IEnumerable<Element> {
  public List<Element> ToElements()=>rows.ToList();
  public FilteredElementCollector WhereElementIsNotElementType(){rows=rows.Where(e=>e is not ElementType);return this;}
  IEnumerable<Element> rows;
  public FilteredElementCollector(Document doc){rows=doc.Elements;}
  public FilteredElementCollector OfClass(Type t){rows=rows.Where(e=>t.IsInstanceOfType(e));return this;}
  public FilteredElementCollector WhereElementIsElementType(){rows=rows.OfType<ElementType>();return this;}
  public IEnumerator<Element> GetEnumerator()=>rows.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
}
namespace Autodesk.Revit.DB {
 public enum BuiltInParameter { ALL_MODEL_DESCRIPTION }
 public enum StorageType { ElementId, String }
 public record ForgeTypeId(string Name);
 public static class SpecTypeId {public static class Reference {public static ForgeTypeId Material=new("material");}}
 public class Definition { public string Name="재료";public ForgeTypeId DataType=SpecTypeId.Reference.Material; public ForgeTypeId GetDataType()=>DataType; }
 public class Parameter {
  public ElementId Id=new(1);public bool IsReadOnly;public bool FailSet;public bool IgnoreSet;public StorageType StorageType=StorageType.ElementId;public Definition Definition=new();
  public ElementId Value=ElementId.InvalidElementId;public ElementId AsElementId()=>Value;public string AsString()=>"";
  public bool Set(ElementId id){if(IsReadOnly||FailSet)throw new InvalidOperationException("locked");if(!IgnoreSet)Value=id;return !IgnoreSet;}
 }
 public class HostObjAttributes:ElementType {public CompoundStructure? Structure;public CompoundStructure? GetCompoundStructure()=>Structure;public void SetCompoundStructure(CompoundStructure s)=>Structure=s;}
 public class CompoundStructure {
  public List<CompoundStructureLayer> Layers=new();
  public int StructuralMaterialIndex=1, VariableLayerIndex=2, ExteriorShell=1, InteriorShell=1;
  public bool VerticallyCompound=true; public string Regions="원래 수직 구역";
  public int Resets; public IList<CompoundStructureLayer> GetLayers()=>Layers;
  public void SetMaterialId(int index,ElementId materialId)=>Layers[index].MaterialId=materialId;
  // 구 버그가 실제로 테스트에서 실패하도록 전체 재설정의 부작용을 모사한다.
  public void SetLayers(IList<CompoundStructureLayer> layers){Layers=layers.ToList();StructuralMaterialIndex=-1;VariableLayerIndex=-1;Resets++;}
 }
 public class CompoundStructureLayer {public int LayerId=99, Priority=7; public bool Wraps=true;public string Deck="원래 데크";public double Width;public int Function;public ElementId MaterialId;public CompoundStructureLayer(double w,int f,ElementId id){Width=w;Function=f;MaterialId=id;}}
 public class SubTransaction:IDisposable {
  Document doc;Dictionary<Parameter,ElementId> before=new();TransactionStatus status;
  public SubTransaction(Document d){doc=d;}
  public TransactionStatus Start(){before=doc.Elements.SelectMany(e=>e.Parameters).ToDictionary(p=>p,p=>p.Value);return status=TransactionStatus.Started;}
  public TransactionStatus Commit()=>status=TransactionStatus.Committed;
  public TransactionStatus GetStatus()=>status;
  public void RollBack(){foreach(var p in before)p.Key.Value=p.Value;status=TransactionStatus.RolledBack;}
  public void Dispose(){if(status==TransactionStatus.Started)RollBack();}
 }
}
namespace Autodesk.Revit.UI { public class ExternalEvent {public static ExternalEvent Create(object h)=>new(); public void Raise(){} } }
namespace WallSplitter {
 internal class NamerExternalEventHandler {
  public Document TargetDocument=null!; public PropertyRequest? PendingProperties;
  public List<(ElementId,string)>? PendingRenames; public bool PendingMergeDuplicateMaterials;
  public class PropertyRequest {public NamerWindow.NamerCategory Category; public ElementId Id=null!; public string Name="";}
 }
 internal static class NamerCommand {public class RenameResult { public TransactionStatus Status;public List<string> Failed=new();public int Renamed;}}
 internal class NamerPropertiesWindow:System.Windows.Window {public string? NewName;public NamerPropertiesWindow(Document d,Element e,NamerWindow.NamerCategory c,string n,string? r){}}
}
