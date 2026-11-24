using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;

namespace SunnyParameterManager.Bridge;

// 프로젝트에 연결된 매개변수 정의(바인딩)를 읽는다. 값은 바꾸지 않는다.
internal static class DefinitionReader
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static DefinitionSnapshot Read(Document doc, bool countValues = true)
    {
        var snapshot = new DefinitionSnapshot();
        foreach (Category c in doc.Settings.Categories)
        {
            if (!c.AllowsBoundParameters || CategoryKey(c) is not { } key) continue;
            snapshot.AllCategories.Add(key); snapshot.CategoryNames[key] = c.Name;
        }
        snapshot.AllCategories.Sort(StringComparer.Ordinal);
        var it = doc.ParameterBindings.ForwardIterator();
        it.Reset();
        while (it.MoveNext())
        {
            if (it.Key is not InternalDefinition def || it.Current is not ElementBinding binding) continue;
            var info = Describe(doc, def, binding);
            foreach (Category c in binding.Categories) if (CategoryKey(c) is { } key) snapshot.CategoryNames.TryAdd(key, c.Name);
            if (countValues)
            {
                try
                {
                    var values = Values(doc, def, binding);
                    info.ValueCount = values.Count; info.ValueFingerprint = DefinitionRemovalChecks.Fingerprint(values);
                }
                catch (OutOfMemoryException) { throw; }
                catch { info.ValueCount = -1; }
            }
            snapshot.Definitions.Add(info);
        }
        return snapshot;
    }

    public static ParameterDefinitionInfo Describe(Document doc, InternalDefinition def, ElementBinding binding)
    {
        var element = doc.GetElement(def.Id) as ParameterElement;
        var info = new ParameterDefinitionInfo
        {
            Name = def.Name, DataType = def.GetDataType()?.TypeId ?? "", ElementId = def.Id.Value.ToString(Invariant), ElementUniqueId = element?.UniqueId ?? "", Revision = element?.VersionGuid.ToString("D") ?? "",
            Group = def.GetGroupTypeId()?.TypeId is { Length: > 0 } g ? g : SharedParameterAddition.OtherGroup,
            Binding = binding is TypeBinding ? "유형" : "인스턴스",
        };
        if (element is SharedParameterElement shared) { info.Guid = shared.GuidValue.ToString("D"); info.Key = "guid:" + info.Guid; }
        else info.Key = "id:" + info.ElementId;
        // 알 수 없는 카테고리를 버리면 ‘프로젝트 정보 단독’으로 잘못 판단할 수 있다.
        foreach (Category c in binding.Categories) info.Categories.Add(CategoryKey(c) ?? "category-id:" + c.Id.Value.ToString(Invariant));
        info.Categories.Sort(StringComparer.Ordinal);
        return info;
    }

    internal static string? CategoryKey(Category c)
    {
        try { var b = c.BuiltInCategory; return b == BuiltInCategory.INVALID ? null : b.ToString(); }
        catch { return null; }
    }

    // 값이 있는 요소 수. 확인하지 못하면 -1(삭제·전환을 허용하지 않음).
    internal static int CountValues(Document doc, InternalDefinition def, ElementBinding binding)
    {
        try { return Values(doc, def, binding).Count; }
        catch (OutOfMemoryException) { throw; }
        catch { return -1; }
    }

    // 값이 있는 요소별 원시값(빈 문자열 제외). 정리 전후 값 보존 확인에 쓴다.
    internal static Dictionary<string, string> Values(Document doc, InternalDefinition def, ElementBinding binding)
    {
        var ids = binding.Categories.Cast<Category>().Select(c => c.Id).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ids.Count == 0) return result;
        var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(ids));
        collector = binding is TypeBinding ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
        foreach (var e in collector.WherePasses(new ElementParameterFilter(ParameterFilterRuleFactory.CreateHasValueParameterRule(def.Id))))
        {
            var p = e.get_Parameter(def);
            if (p == null || !p.HasValue) continue;
            var raw = ParameterReader.Raw(p);
            if (p.StorageType == StorageType.String && raw.Length == 0) continue;
            result[e.UniqueId] = raw;
        }
        // 프로젝트 정보는 일반 수집에서 빠질 수 있어 직접 확인한다.
        if (binding is not TypeBinding && doc.ProjectInformation is { } info && binding.Categories.Contains(info.Category) && !result.ContainsKey(info.UniqueId))
        {
            var p = info.get_Parameter(def);
            if (p != null && p.HasValue && !(p.StorageType == StorageType.String && string.IsNullOrEmpty(p.AsString()))) result[info.UniqueId] = ParameterReader.Raw(p);
        }
        return result;
    }

    internal static (InternalDefinition Def, ElementBinding Binding)? Find(Document doc, string key)
    {
        var it = doc.ParameterBindings.ForwardIterator();
        it.Reset();
        while (it.MoveNext())
            if (it.Key is InternalDefinition def && it.Current is ElementBinding binding && Describe(doc, def, binding).Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return (def, binding);
        return null;
    }
}
