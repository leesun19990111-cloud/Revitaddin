// 실제 LinkRemovalEngine의 수집·삭제 검증 로직을 실행하는 API 대역. 실제 RVT 검증 아님.
namespace Autodesk.Revit.DB
{
 public sealed record ElementId(long Value) { public static ElementId InvalidElementId { get; } = new(-1); }
 public enum BuiltInCategory { OST_Coordination_Model = -200 }
 public enum CheckoutStatus { NotOwned, OwnedByOtherUser }
 public enum TransactionStatus { Started, Committed, RolledBack }
 public enum ExternalFileReferenceType { RevitLink, CADLink, DWFMarkup, KeynoteTable, Material, Unknown }
 public sealed record ModelPath(string Path);
 public static class ModelPathUtils { public static string ConvertModelPathToUserVisiblePath(ModelPath p) => p.Path; }
 public sealed class Category { public ElementId Id { get; set; } = new(1); public string Name { get; set; } = "카테고리"; }
 public class Element
 {
  private static long _next;
  public ElementId Id { get; } = new(++_next); public string UniqueId { get; } = Guid.NewGuid().ToString();
  public Guid VersionGuid { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "항목";
  public bool Pinned { get; set; } public Category? Category { get; set; } = new();
  public ElementId TypeId { get; set; } = ElementId.InvalidElementId; public bool OwnedOther { get; set; }
  public ExternalFileReference? FileReference { get; set; }
  public Dictionary<string, ExternalResourceReference> Resources { get; } = [];
  public ElementId GetTypeId() => TypeId;
  public bool IsExternalFileReference() => FileReference != null;
  public ExternalFileReference GetExternalFileReference() => FileReference!;
  public Dictionary<string, ExternalResourceReference> GetExternalResourceReferences() => Resources;
 }
 public class ElementType : Element { }
 public sealed class RevitLinkType : ElementType { public bool IsNestedLink { get; set; } }
 public sealed class RevitLinkInstance : Element { }
 public sealed class CADLinkType : ElementType { }
 public sealed class ImportInstance : Element { public bool IsLinked { get; set; } }
 public sealed class PointCloudType : ElementType { public string Path { get; set; } = "cloud.rcp"; public string GetPath() => Path; }
 public sealed class PointCloudInstance : Element { }
 public sealed class ImageType : ElementType { public string Path { get; set; } = "image.pdf"; }
 public sealed class ImageInstance : Element { }
 public sealed class KeynoteTable : Element { }
 public sealed class ExternalFileReference { public ExternalFileReferenceType ExternalFileReferenceType { get; set; } public string Path { get; set; } = "source.rvt"; public ModelPath GetPath() => new(Path); }
 public sealed class ExternalResourceReference { public string InSessionPath { get; set; } = "cloud-link"; public Dictionary<string,string> Info { get; } = []; public Dictionary<string,string> GetReferenceInformation() => Info; }
 public static class ExternalFileUtils { public static ICollection<ElementId> GetAllExternalFileReferences(Document doc) => doc.Elements.Where(e => e.FileReference != null).Select(e => e.Id).ToList(); }
 public static class ExternalResourceUtils { public static ICollection<ElementId> GetAllExternalResourceReferences(Document doc) => doc.Elements.Where(e => e.Resources.Count > 0).Select(e => e.Id).ToList(); }
 public static class WorksharingUtils { public static CheckoutStatus GetCheckoutStatus(Document doc, ElementId id) => doc.GetElement(id)?.OwnedOther == true ? CheckoutStatus.OwnedByOtherUser : CheckoutStatus.NotOwned; }
 public sealed class Document
 {
  public List<Element> Elements { get; set; } = []; public bool IsWorkshared { get; set; }
  public Dictionary<long,List<long>> Dependencies { get; } = []; public HashSet<long> RefuseDelete { get; } = [];
  public bool FailDelete; public List<ElementId> LastRequested { get; private set; } = [];
  public Element? GetElement(ElementId id) => Elements.SingleOrDefault(e => e.Id == id);
  public Element? GetElement(string id) => Elements.SingleOrDefault(e => e.UniqueId == id);
  public ICollection<ElementId> Delete(ICollection<ElementId> ids)
  {
   if (FailDelete) throw new InvalidOperationException("대역 삭제 거부");
   LastRequested = ids.ToList(); var deleted = ids.Select(id => id.Value).ToHashSet();
   int count;
   do { count = deleted.Count; foreach (var e in Elements.Where(e => deleted.Contains(e.GetTypeId().Value))) deleted.Add(e.Id.Value);
    foreach (var id in deleted.ToList()) foreach (var dependent in Dependencies.GetValueOrDefault(id, [])) deleted.Add(dependent);
   } while (count != deleted.Count);
   deleted.ExceptWith(RefuseDelete); Elements.RemoveAll(e => deleted.Contains(e.Id.Value)); return deleted.Select(id => new ElementId(id)).ToList();
  }
 }
 public sealed class FilteredElementCollector(Document doc) : IEnumerable<Element>
 {
  private IEnumerable<Element> _items = doc.Elements;
  public FilteredElementCollector WhereElementIsElementType() { _items = _items.Where(e => e is ElementType); return this; }
  public FilteredElementCollector WhereElementIsNotElementType() { _items = _items.Where(e => e is not ElementType); return this; }
  public IEnumerator<Element> GetEnumerator() => _items.GetEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
 }
 public sealed class Transaction(Document doc, string name) : IDisposable
 {
  private TransactionStatus _status; private List<Element> _before = [];
  public TransactionStatus Start() { _before = doc.Elements.ToList(); return _status = TransactionStatus.Started; }
  public TransactionStatus GetStatus() => _status;
  public TransactionStatus RollBack() { doc.Elements = _before; return _status = TransactionStatus.RolledBack; }
  public void Dispose() { }
 }
}
namespace Autodesk.Revit.DB.Architecture { public sealed class TopographyLinkType : Autodesk.Revit.DB.ElementType { } }
