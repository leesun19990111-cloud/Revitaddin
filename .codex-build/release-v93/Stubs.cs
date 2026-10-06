using System.Collections;
using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public record ElementId(int Value) { public static ElementId InvalidElementId = new(-1); }
 public class Element { public ElementId Id {get;set;}=new(0); public string Name {get;set;}=""; public bool IsValidObject {get;set;}=true; public virtual ElementId GetTypeId()=>ElementId.InvalidElementId; }
 public class ElementType:Element{}
 public class Family:Element { public List<ElementId> Symbols=new(); public ICollection<ElementId> GetFamilySymbolIds()=>Symbols; }
 public class FamilySymbol:ElementType { public Family Family {get;set;}=null!; }
 public class FamilyInstance:Element { public FamilySymbol Symbol {get;set;}=null!; public override ElementId GetTypeId()=>Symbol.Id; }
 public class View:Element { public bool IsTemplate {get;set;} public ViewType ViewType {get;set;} }
 public class ViewSheet:View{} public class ViewSchedule:View{} public class Material:Element{}
 public enum ViewType { Legend } public enum TransactionStatus { Committed, RolledBack }
 public class Document { public string PathName {get;set;}="test.rvt"; public string Title=>"test"; public List<Element> Elements=new(); public Element? GetElement(ElementId id)=>Elements.FirstOrDefault(e=>e.Id==id); }
 public class FilteredElementCollector:IEnumerable<Element> {
  IEnumerable<Element> rows;
  public FilteredElementCollector(Document doc){rows=doc.Elements;}
  public FilteredElementCollector OfClass(Type t){rows=rows.Where(e=>t.IsInstanceOfType(e));return this;}
  public FilteredElementCollector WhereElementIsElementType(){rows=rows.OfType<ElementType>();return this;}
  public IEnumerator<Element> GetEnumerator()=>rows.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
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
