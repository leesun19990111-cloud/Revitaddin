// 실제 정의 리더·삭제 실행부의 제어 흐름 검사. Autodesk DB·중앙 동기화를 대신하지 않는다.
using SunnyParameterManager.Core;
namespace Autodesk.Revit.DB
{
    public enum BuiltInCategory { INVALID, OST_ProjectInformation, OST_Walls }
    public enum StorageType { String }
    public enum CheckoutStatus { NotOwned, OwnedByOtherUser }
    public sealed record ElementId(long Value);
    public sealed record ForgeTypeId(string TypeId);
    public class Category
    {
        public ElementId Id { get; set; } = new(1);
        public string Name { get; set; } = "프로젝트 정보";
        public bool AllowsBoundParameters => true;
        public BuiltInCategory BuiltInCategory { get; set; } = BuiltInCategory.OST_ProjectInformation;
    }
    public class InternalDefinition
    {
        public ElementId Id { get; set; } = new(10);
        public string Name { get; set; } = "구조";
        public ForgeTypeId DataType { get; set; } = new("autodesk.spec:spec.string-2.0.0");
        public ForgeTypeId Group { get; set; } = new("");
        public ForgeTypeId GetDataType() => DataType;
        public ForgeTypeId GetGroupTypeId() => Group;
    }
    public class Parameter
    {
        public InternalDefinition Definition { get; set; } = new();
        public string Key { get; set; } = "";
        public bool HasValue { get; set; } = true;
        public string Value { get; set; } = "기존";
        public StorageType StorageType => StorageType.String;
        public string AsString() => Value;
    }
    public class Element
    {
        public ElementId Id { get; set; } = new(1);
        public string UniqueId { get; set; } = Guid.NewGuid().ToString("D");
        public Guid VersionGuid { get; set; } = Guid.NewGuid();
        public Category Category { get; set; } = new();
        public List<Parameter> Parameters { get; } = [];
        public Parameter? get_Parameter(InternalDefinition d) => Parameters.SingleOrDefault(p => p.Definition.Id == d.Id);
    }
    public class ParameterElement : Element { }
    public sealed class SharedParameterElement : ParameterElement { public Guid GuidValue { get; } = Guid.NewGuid(); }
    public class ElementBinding { public List<Category> Categories { get; set; } = []; }
    public sealed class TypeBinding : ElementBinding { }
    public sealed class BindingMap(Document doc)
    {
        public Dictionary<InternalDefinition, ElementBinding> Items { get; } = [];
        public Action? OnRemove;
        public bool RejectRemove;
        public Iterator ForwardIterator() => new(Items);
        public bool Remove(InternalDefinition d)
        {
            if (RejectRemove) return false;
            var result = Items.Remove(d);
            foreach (var e in doc.Elements) e.Parameters.RemoveAll(p => p.Definition.Id == d.Id);
            OnRemove?.Invoke(); return result;
        }
    }
    public sealed class Iterator(Dictionary<InternalDefinition, ElementBinding> items)
    {
        private IEnumerator<KeyValuePair<InternalDefinition, ElementBinding>> _enumerator = items.GetEnumerator();
        public void Reset() => _enumerator = items.GetEnumerator();
        public bool MoveNext() => _enumerator.MoveNext();
        public object Key => _enumerator.Current.Key;
        public object Current => _enumerator.Current.Value;
    }
    public sealed class Settings { public List<Category> Categories { get; } = []; }
    public sealed class Document
    {
        public Document() { ParameterBindings = new(this); Elements.Add(ProjectInformation); Settings.Categories.Add(ProjectInformation.Category); }
        public bool IsWorkshared { get; set; }
        public HashSet<long> OtherOwned { get; } = [];
        public bool CascadeDelete;
        public Element ProjectInformation { get; } = new();
        public List<Element> Elements { get; } = [];
        public Settings Settings { get; } = new();
        public BindingMap ParameterBindings { get; }
        public Element? GetElement(ElementId id) => Elements.SingleOrDefault(e => e.Id == id);
        public ICollection<ElementId> Delete(ElementId id)
        {
            Elements.RemoveAll(e => e.Id == id);
            return CascadeDelete ? [id, new(999)] : [id];
        }
    }
    public sealed class ElementMulticategoryFilter(List<ElementId> ids) { public List<ElementId> Ids => ids; }
    public sealed class ElementParameterFilter(ElementId id) { public ElementId Id => id; }
    public static class ParameterFilterRuleFactory { public static ElementId CreateHasValueParameterRule(ElementId id) => id; }
    public sealed class FilteredElementCollector(Document doc) : IEnumerable<Element>, IDisposable
    {
        private IEnumerable<Element> _items = doc.Elements;
        public FilteredElementCollector WherePasses(ElementMulticategoryFilter f) { _items = _items.Where(e => f.Ids.Contains(e.Category.Id)); return this; }
        public FilteredElementCollector WherePasses(ElementParameterFilter f) { _items = _items.Where(e => e.Parameters.Any(p => p.Definition.Id == f.Id && p.HasValue)); return this; }
        public FilteredElementCollector WhereElementIsElementType() { _items = []; return this; }
        public FilteredElementCollector WhereElementIsNotElementType() => this;
        public ICollection<ElementId> ToElementIds() => _items.Select(e => e.Id).ToList();
        public IEnumerator<Element> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { }
    }
    public static class WorksharingUtils
    {
        public static CheckoutStatus GetCheckoutStatus(Document doc, ElementId id) => doc.OtherOwned.Contains(id.Value) ? CheckoutStatus.OwnedByOtherUser : CheckoutStatus.NotOwned;
    }
}
namespace SunnyParameterManager.Bridge
{
    internal static class ParameterReader
    {
        internal static string Raw(Autodesk.Revit.DB.Parameter p) => p.Value;
        internal static string Key(Autodesk.Revit.DB.Parameter p) => p.Key;
    }
    internal static class ModelEngine { internal sealed record WriteAction(Action Set, Action Verify); }
}
