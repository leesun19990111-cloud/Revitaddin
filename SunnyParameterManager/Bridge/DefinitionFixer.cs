using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace SunnyParameterManager.Bridge;

// 속성규격 룰셋의 매개변수 정의 정리. 한 모델의 모든 정리를 같은 묶음에서 처리하고,
// 확정 후 정의·값 보존을 다시 검사해 하나라도 다르면 묶음 전체를 롤백한다.
internal static class DefinitionFixer
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] Order = [SpecRulePlanner.ActionAdd, SpecRulePlanner.ActionBinding, SpecRulePlanner.ActionCategories, SpecRulePlanner.ActionGroup, SpecRulePlanner.ActionMerge, SpecRulePlanner.ActionRemove];

    public static List<ModelEngine.WriteAction> Prepare(RevitApplication app, Document doc, IReadOnlyCollection<ValueEdit> edits)
    {
        if (edits.Any(e => e.DefinitionFix?.Action == SpecRulePlanner.ActionRemove)) return DefinitionRemoval.Prepare(doc, edits);
        if (edits.Any(e => e.DefinitionFix == null || e.AddParameter != null || e.KacCleanup || e.MaterialSource != null))
            throw new InvalidOperationException("매개변수 정의 정리는 값 변경과 같은 요청으로 처리하지 않습니다.");
        var fixes = edits.Select(e => (Edit: e, Fix: e.DefinitionFix!)).OrderBy(x => Array.IndexOf(Order, x.Fix.Action)).ToList();
        if (fixes.Any(x => Array.IndexOf(Order, x.Fix.Action) < 0)) throw new InvalidOperationException("알 수 없는 정의 정리 항목입니다.");
        var issues = new List<string>();
        var writes = new List<ModelEngine.WriteAction>();
        var current = DefinitionReader.Read(doc, false);
        foreach (var (edit, fix) in fixes)
        {
            try { writes.Add(PrepareOne(app, doc, current, fixes.Select(f => f.Fix).ToList(), edit, fix)); }
            catch (InvalidOperationException ex) { issues.Add($"{fix.Action} / {fix.Name}: {ex.Message}"); }
        }
        if (issues.Count > 0) throw new InvalidOperationException($"적용 전 재검증에서 정의 정리 {issues.Count:N0}건을 보류했습니다. 이 모델은 변경하지 않았습니다.\n" + string.Join("\n", issues.Take(15)) + (issues.Count > 15 ? $"\n외 {issues.Count - 15:N0}건" : ""));
        return writes;
    }

    private static ModelEngine.WriteAction PrepareOne(RevitApplication app, Document doc, DefinitionSnapshot current, List<DefinitionFix> all, ValueEdit edit, DefinitionFix fix)
    {
        var key = edit.Original.ParameterKey;
        if (fix.Action == SpecRulePlanner.ActionAdd)
        {
            if (!Guid.TryParse(fix.Guid, out var guid) || !string.Equals(key, "guid:" + guid.ToString("D"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("추가할 공유 매개변수 GUID가 올바르지 않습니다.");
            if (SharedParameterElement.Lookup(doc, guid) != null) throw new InvalidOperationException("조회 후 같은 GUID의 공유 매개변수가 이미 생겼습니다. 다시 조회하세요.");
            if (current.Definitions.Any(d => d.Name == fix.Name)) throw new InvalidOperationException("같은 이름의 매개변수가 이미 있습니다. 다시 조회하세요.");
            var spec = new ForgeTypeId(fix.DataType);
            if (!SpecUtils.IsSpec(spec)) throw new InvalidOperationException("데이터 유형을 확인할 수 없습니다: " + fix.DataType);
            var group = Group(fix.Group);
            var categories = Categories(doc, fix.Categories);
            return new(() =>
            {
                var previous = app.SharedParametersFilename;
                var temp = Path.Combine(Path.GetTempPath(), "SunnyParameterManager-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    File.WriteAllText(temp, "");
                    app.SharedParametersFilename = temp;
                    var file = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("임시 공유 매개변수 파일을 열 수 없습니다.");
                    var definition = file.Groups.Create("Sunny").Definitions.Create(new ExternalDefinitionCreationOptions(fix.Name, spec) { GUID = guid }) as ExternalDefinition
                        ?? throw new InvalidOperationException("공유 매개변수 정의를 만들 수 없습니다.");
                    var set = app.Create.NewCategorySet(); foreach (var c in categories) set.Insert(c);
                    ElementBinding binding = fix.Binding == "유형" ? app.Create.NewTypeBinding(set) : app.Create.NewInstanceBinding(set);
                    if (!doc.ParameterBindings.Insert(definition, binding, group)) throw new InvalidOperationException("Revit이 공유 매개변수를 추가하지 않았습니다: " + fix.Name);
                }
                finally
                {
                    try { app.SharedParametersFilename = previous; } catch { }
                    try { File.Delete(temp); } catch { }
                }
            }, () =>
            {
                var found = DefinitionReader.Find(doc, key) ?? throw new InvalidOperationException("확정 후 추가한 매개변수가 없습니다: " + fix.Name);
                CheckState(doc, found, fix.Name, fix.Binding, fix.Categories, fix.Group);
            });
        }

        var found = DefinitionReader.Find(doc, key) ?? throw new InvalidOperationException("조회 당시 매개변수 정의가 현재 모델에 없습니다. 다시 조회하세요.");
        var info = DefinitionReader.Describe(doc, found.Def, found.Binding);
        if (info.ElementUniqueId != fix.ElementUniqueId) throw new InvalidOperationException("조회 당시와 다른 매개변수 요소입니다. 다시 조회하세요.");
        if (info.Name != (fix.Action is SpecRulePlanner.ActionMerge or SpecRulePlanner.ActionRemove ? fix.SourceName : fix.Name)) throw new InvalidOperationException("매개변수 이름이 조회 당시와 다릅니다: " + info.Name);
        var before = DefinitionReader.Values(doc, found.Def, found.Binding);
        if (before.Count != fix.ValueCount) throw new InvalidOperationException($"조회 후 값이 있는 요소 수가 바뀌었습니다({fix.ValueCount:N0} → {before.Count:N0}). 다시 조회하세요.");

        if (fix.Action == SpecRulePlanner.ActionGroup)
        {
            var group = Group(fix.Group);
            return new(() => found.Def.SetGroupTypeId(group), () =>
            {
                var after = DefinitionReader.Find(doc, key) ?? throw new InvalidOperationException("확정 후 매개변수가 없습니다: " + fix.Name);
                if (SpecRulePlanner.BaseId(DefinitionReader.Describe(doc, after.Def, after.Binding).Group) != fix.Group) throw new InvalidOperationException("확정 후 그룹이 요청과 다릅니다: " + fix.Name);
                SameValues(doc, after, before, fix.Name);
            });
        }
        if (fix.Action is SpecRulePlanner.ActionCategories or SpecRulePlanner.ActionBinding)
        {
            if (fix.Action == SpecRulePlanner.ActionBinding && (info.Binding == fix.Binding || before.Count != 0)) throw new InvalidOperationException("값이 없는 매개변수만 유형/인스턴스를 전환합니다.");
            if (fix.Action == SpecRulePlanner.ActionCategories && info.Binding != fix.Binding) throw new InvalidOperationException("유형/인스턴스가 조회 당시와 다릅니다.");
            if (info.Categories.Except(fix.Categories).Any()) throw new InvalidOperationException("기존 연결 카테고리를 빼는 요청은 처리하지 않습니다.");
            var categories = Categories(doc, fix.Categories);
            var groupId = fix.Action == SpecRulePlanner.ActionBinding ? fix.Group : SpecRulePlanner.BaseId(info.Group);
            return new(() =>
            {
                var set = app.Create.NewCategorySet(); foreach (var c in categories) set.Insert(c);
                ElementBinding binding = fix.Binding == "유형" ? app.Create.NewTypeBinding(set) : app.Create.NewInstanceBinding(set);
                // 같은 묶음의 그룹 변경이 먼저 적용됐을 수 있으므로 현재 그룹을 유지한다.
                var group = fix.Action == SpecRulePlanner.ActionBinding ? Group(groupId) : found.Def.GetGroupTypeId() ?? new ForgeTypeId("");
                if (!doc.ParameterBindings.ReInsert(found.Def, binding, group)) throw new InvalidOperationException("Revit이 매개변수 연결 변경을 거부했습니다: " + fix.Name);
            }, () =>
            {
                var after = DefinitionReader.Find(doc, key) ?? throw new InvalidOperationException("확정 후 매개변수가 없어졌습니다: " + fix.Name);
                CheckState(doc, after, fix.Name, fix.Binding, fix.Categories, null);
                SameValues(doc, after, before, fix.Name);
            });
        }

        // 중복 병합: 값을 기준 매개변수로 옮기고 이전 정의의 연결을 제거한다.
        var addition = all.FirstOrDefault(f => f.Action == SpecRulePlanner.ActionAdd && "guid:" + f.Guid == fix.TargetKey);
        var target = DefinitionReader.Find(doc, fix.TargetKey);
        if (target == null && addition == null) throw new InvalidOperationException("값을 옮길 기준 매개변수가 없습니다. 다시 조회하세요.");
        var targetBinding = addition?.Binding ?? DefinitionReader.Describe(doc, target!.Value.Def, target.Value.Binding).Binding;
        var targetCategories = (addition?.Categories ?? DefinitionReader.Describe(doc, target!.Value.Def, target.Value.Binding).Categories)
            .Concat(all.Where(f => f.Action is SpecRulePlanner.ActionCategories or SpecRulePlanner.ActionBinding && Find(current, f) == fix.TargetKey).SelectMany(f => f.Categories)).ToHashSet(StringComparer.Ordinal);
        if (all.Any(f => f.Action == SpecRulePlanner.ActionBinding && Find(current, f) == fix.TargetKey))
            targetBinding = all.First(f => f.Action == SpecRulePlanner.ActionBinding && Find(current, f) == fix.TargetKey).Binding;
        if (before.Count > 0 && targetBinding != info.Binding) throw new InvalidOperationException("기준 매개변수와 유형/인스턴스가 달라 값을 옮길 수 없습니다.");
        var conflicts = new List<string>();
        foreach (var (uid, raw) in before)
        {
            var element = doc.GetElement(uid) ?? throw new InvalidOperationException("값이 있는 요소를 다시 찾을 수 없습니다: " + uid);
            var categoryKey = element is ProjectInfo ? BuiltInCategory.OST_ProjectInformation.ToString() : element.Category == null ? null : DefinitionReader.CategoryKey(element.Category);
            if (categoryKey == null || !targetCategories.Contains(categoryKey)) { conflicts.Add($"요소 {element.Id.Value}: 기준 매개변수가 이 카테고리({element.Category?.Name})에 연결되지 않습니다"); continue; }
            var source = element.get_Parameter(found.Def);
            if (source == null || source.StorageType is not (StorageType.String or StorageType.Integer or StorageType.Double)) { conflicts.Add($"요소 {element.Id.Value}: 옮길 수 없는 값 형식"); continue; }
            if (target == null) continue;
            var dst = element.get_Parameter(target.Value.Def);
            if (dst == null) continue; // 카테고리 추가 후 생긴다. 실제 입력 시 다시 확인한다.
            if (dst.StorageType != source.StorageType) { conflicts.Add($"요소 {element.Id.Value}: 저장 형식이 다릅니다"); continue; }
            if (HasValue(dst) && !Same(dst, ParameterReader.Raw(dst), raw)) conflicts.Add($"요소 {element.Id.Value}: '{fix.SourceName}'={source.AsValueString() ?? raw} / '{fix.Name}'={dst.AsValueString() ?? ParameterReader.Raw(dst)}");
            else if (dst.IsReadOnly && !HasValue(dst)) conflicts.Add($"요소 {element.Id.Value}: 기준 매개변수가 읽기 전용입니다");
        }
        if (conflicts.Count > 0)
            throw new InvalidOperationException($"'{fix.SourceName}' → '{fix.Name}' 값 이동 충돌 {conflicts.Count:N0}건. 어느 값이 맞는지 원본에서 정리한 뒤 다시 조회하세요.\n" + string.Join("\n", conflicts.Take(8)) + (conflicts.Count > 8 ? $"\n외 {conflicts.Count - 8:N0}건" : ""));
        var parameterElementId = found.Def.Id;
        var shared = doc.GetElement(parameterElementId) is SharedParameterElement;
        return new(() =>
        {
            var dstDef = (DefinitionReader.Find(doc, fix.TargetKey) ?? throw new InvalidOperationException("기준 매개변수를 찾을 수 없습니다: " + fix.Name)).Def;
            foreach (var (uid, raw) in before)
            {
                var element = doc.GetElement(uid);
                var src = element.get_Parameter(found.Def);
                var dst = element.get_Parameter(dstDef) ?? throw new InvalidOperationException($"요소 {element.Id.Value}에 기준 매개변수 '{fix.Name}'가 없습니다.");
                if (HasValue(dst)) { if (!Same(dst, ParameterReader.Raw(dst), raw)) throw new InvalidOperationException($"요소 {element.Id.Value}: 값 충돌"); continue; }
                if (dst.IsReadOnly) throw new InvalidOperationException($"요소 {element.Id.Value}: 기준 매개변수가 읽기 전용입니다.");
                var ok = src.StorageType switch
                {
                    StorageType.String => dst.Set(src.AsString()), StorageType.Integer => dst.Set(src.AsInteger()), StorageType.Double => dst.Set(src.AsDouble()), _ => false,
                };
                if (!ok) throw new InvalidOperationException($"요소 {element.Id.Value}: Revit이 값 이동을 거부했습니다.");
            }
            if (!doc.ParameterBindings.Remove(found.Def)) throw new InvalidOperationException("Revit이 이전 매개변수 연결 제거를 거부했습니다: " + fix.SourceName);
            // 프로젝트 매개변수 요소는 연결이 없어지면 남길 이유가 없다. 공유 매개변수 요소는 패밀리와 공유될 수 있어 남긴다.
            if (!shared && doc.GetElement(parameterElementId) != null) doc.Delete(parameterElementId);
        }, () =>
        {
            if (DefinitionReader.Find(doc, key) != null) throw new InvalidOperationException("확정 후에도 이전 매개변수 연결이 남아 있습니다: " + fix.SourceName);
            var dst = DefinitionReader.Find(doc, fix.TargetKey) ?? throw new InvalidOperationException("확정 후 기준 매개변수가 없습니다: " + fix.Name);
            foreach (var (uid, raw) in before)
            {
                var p = doc.GetElement(uid)?.get_Parameter(dst.Def) ?? throw new InvalidOperationException("확정 후 옮긴 값을 확인할 수 없습니다: " + uid);
                if (!Same(p, ParameterReader.Raw(p), raw)) throw new InvalidOperationException($"확정 후 옮긴 값이 다릅니다: 요소 {p.Element.Id.Value} / {fix.Name}");
            }
        });
    }

    private static string? Find(DefinitionSnapshot current, DefinitionFix f) => current.Definitions.FirstOrDefault(d => d.ElementUniqueId == f.ElementUniqueId && f.ElementUniqueId.Length > 0)?.Key;
    private static bool HasValue(Parameter p) => p.HasValue && !(p.StorageType == StorageType.String && string.IsNullOrEmpty(p.AsString()));
    private static bool Same(Parameter p, string a, string b) => p.StorageType == StorageType.Double
        ? double.TryParse(a, NumberStyles.Float, Invariant, out var x) && double.TryParse(b, NumberStyles.Float, Invariant, out var y) && Math.Abs(x - y) <= Math.Max(1e-9, Math.Abs(y) * 1e-10)
        : a == b;

    private static ForgeTypeId Group(string baseId)
    {
        if (baseId == SharedParameterAddition.OtherGroup) return new ForgeTypeId("");
        return ParameterUtils.GetAllBuiltInGroups().FirstOrDefault(g => SpecRulePlanner.BaseId(g.TypeId) == baseId) ?? throw new InvalidOperationException("매개변수 그룹을 확인할 수 없습니다: " + baseId);
    }

    private static List<Category> Categories(Document doc, IEnumerable<string> keys)
    {
        var result = new List<Category>();
        foreach (var key in keys)
        {
            if (!Enum.TryParse<BuiltInCategory>(key, out var bic) || Category.GetCategory(doc, bic) is not { AllowsBoundParameters: true } c)
                throw new InvalidOperationException("매개변수를 연결할 수 없는 카테고리입니다: " + key);
            result.Add(c);
        }
        if (result.Count == 0) throw new InvalidOperationException("연결할 카테고리가 없습니다.");
        return result;
    }

    private static void CheckState(Document doc, (InternalDefinition Def, ElementBinding Binding) found, string name, string binding, List<string> categories, string? group)
    {
        var info = DefinitionReader.Describe(doc, found.Def, found.Binding);
        if (info.Name != name || info.Binding != binding || categories.Except(info.Categories).Any() || group != null && SpecRulePlanner.BaseId(info.Group) != group)
            throw new InvalidOperationException($"확정 후 매개변수 정의가 요청과 다릅니다: {name} (유형 {info.Binding}, 카테고리 {info.Categories.Count}개, 그룹 {info.Group})");
    }

    private static void SameValues(Document doc, (InternalDefinition Def, ElementBinding Binding) found, Dictionary<string, string> before, string name)
    {
        var after = DefinitionReader.Values(doc, found.Def, found.Binding);
        var lost = before.Where(b => !after.TryGetValue(b.Key, out var v) || v != b.Value).Take(5).ToList();
        if (lost.Count > 0) throw new InvalidOperationException($"확정 후 '{name}'의 기존 값이 보존되지 않아 이번 묶음을 취소합니다. 예: " + string.Join(", ", lost.Select(l => l.Key)));
    }
}
