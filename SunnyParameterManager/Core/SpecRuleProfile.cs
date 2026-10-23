using System.Text.RegularExpressions;
using System.Text.Json;

namespace SunnyParameterManager.Core;

// 속성규격 엑셀(이름·유형/인스턴스·데이터 유형·그룹·카테고리)과 모델의 매개변수 정의를 비교한다.
// 값이 아니라 정의(바인딩)를 다루므로 다른 룰셋과 섞지 않는다.
public sealed class SpecRuleProfile
{
    public string Source { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    // 이전 이름·띄어쓰기만 다른 중복 매개변수의 값을 기준 매개변수로 옮긴 뒤 이전 정의를 제거한다.
    public bool MergeDuplicates { get; set; }
    // 모델에 없는 매개변수는 다른 조회 모델의 같은 이름 공유 매개변수 GUID가 하나로 확인될 때만 추가한다.
    public bool AddMissing { get; set; }
    // 삭제 전용: 정확한 항목명이며 프로젝트 정보에만 연결된 정의를 값 이동 없이 제거한다.
    // 다른 검사·정리와 혼합하지 않는다. 남길 정의가 필요한 항목은 KeepName으로 명시한다.
    public bool RemoveOnly { get; set; }
    public int RemovalSafetyVersion { get; set; }
    public List<SpecRemovalTarget> RemovalTargets { get; set; } = [];
    public List<SpecParameter> Parameters { get; set; } = [];
}

public sealed class SpecRemovalTarget
{
    public string Name { get; set; } = "";
    public string KeepName { get; set; } = "";
    public List<string> Categories { get; set; } = [];
}

public sealed class SpecParameter
{
    public string Name { get; set; } = "";
    // 엑셀의 ‘새 이름←이전 이름’에서 읽은 이전 이름. 값 이동 후 제거 대상이다.
    public List<string> Legacy { get; set; } = [];
    public string Binding { get; set; } = "인스턴스";
    public string DataType { get; set; } = "";
    public string Group { get; set; } = "";
    // all: 매개변수를 연결할 수 있는 모든 카테고리. ExcludeCategories만 뺀다.
    public bool AllCategories { get; set; }
    public List<string> Categories { get; set; } = [];
    public List<string> ExcludeCategories { get; set; } = [];
    public int SourceRow { get; set; }
}

public static class SpecRulePlanner
{
    public const string DefinitionScope = "매개변수 정의";
    public const string ActionAdd = "매개변수 추가";
    public const string ActionBinding = "유형/인스턴스 전환";
    public const string ActionCategories = "카테고리 추가";
    public const string ActionGroup = "그룹 변경";
    public const string ActionMerge = "중복 병합·제거";
    public const string ActionRemove = "중복 삭제";

    // 엑셀 표기 → Revit 식별자(버전 접미사 제외). Bridge도 같은 표기를 Revit 상수로 바꾼다.
    public static readonly IReadOnlyDictionary<string, string> DataTypes = new Dictionary<string, string>
    {
        ["문자"] = "autodesk.spec:spec.string", ["길이"] = "autodesk.spec.aec:length", ["면적"] = "autodesk.spec.aec:area",
        ["체적"] = "autodesk.spec.aec:volume", ["예/아니요"] = "autodesk.spec:spec.bool",
    };
    public static readonly IReadOnlyDictionary<string, string> Groups = new Dictionary<string, string>
    {
        ["기타"] = SharedParameterAddition.OtherGroup, ["문자"] = "autodesk.parameter.group:text", ["치수"] = "autodesk.parameter.group:geometry",
        ["ID 데이터"] = "autodesk.parameter.group:identityData", ["IFC 매개변수"] = "autodesk.parameter.group:ifc", ["일반"] = "autodesk.parameter.group:general",
        ["데이터"] = "autodesk.parameter.group:data", ["가시성"] = "autodesk.parameter.group:visibility",
    };
    public static string BaseId(string typeId) => typeId == SharedParameterAddition.OtherGroup ? typeId : Regex.Replace(typeId ?? "", @"-[0-9]+\.[0-9]+\.[0-9]+\z", "");
    public static string Compact(string name) => Regex.Replace(name ?? "", @"\s+", "").ToUpperInvariant();
    public static string GroupLabel(string typeId) => Groups.FirstOrDefault(g => g.Value == BaseId(typeId)).Key ?? typeId;
    public static string TypeLabel(string typeId) => DataTypes.FirstOrDefault(g => g.Value == BaseId(typeId)).Key ?? typeId;

    public static void Validate(RuleSet file)
    {
        var p = file.Spec;
        if (file.Version != 5 || string.IsNullOrWhiteSpace(file.Name) || file.Kac != null || file.Space != null || file.Cleanup || file.Rules == null || file.Rules.Count != 0 ||
            p == null || string.IsNullOrWhiteSpace(p.Source) || !Regex.IsMatch(p.SourceSha256 ?? "", @"\A[0-9a-fA-F]{64}\z") || p.Parameters == null || p.RemovalTargets == null || p.Parameters.Count > 2000)
            throw new InvalidOperationException("버전5 속성규격 룰셋의 출처·매개변수 표를 확인하세요. 다른 규칙과 혼합할 수 없습니다.");
        if (p.RemoveOnly)
        {
            if (p.RemovalSafetyVersion != 1 || p.AddMissing || p.MergeDuplicates || p.Parameters.Count != 0 || p.RemovalTargets.Count is < 1 or > 100)
                throw new InvalidOperationException("안전 삭제 룰셋을 새로 불러오세요. 삭제 전용은 안전검증 버전1과 정확한 삭제 대상 목록만 사용합니다.");
            var deletionNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in p.RemovalTargets)
                if (target == null || string.IsNullOrWhiteSpace(target.Name) || target.Name != target.Name.Trim() || target.Name.Length > 200 || !deletionNames.Add(target.Name) ||
                    target.KeepName == null || target.KeepName.Length > 200 || target.KeepName != target.KeepName.Trim() || target.KeepName == target.Name ||
                    target.Categories == null || !target.Categories.SequenceEqual(new[] { "OST_ProjectInformation" }))
                    throw new InvalidOperationException("안전 삭제는 정확한 항목명과 프로젝트 정보 단독 카테고리를 지정하세요. 남길 이름은 삭제 이름과 달라야 합니다.");
            if (p.RemovalTargets.Any(t => deletionNames.Contains(t.KeepName))) throw new InvalidOperationException("남길 매개변수가 다른 삭제 대상에 포함되어 있습니다.");
            return;
        }
        if (p.Parameters.Count == 0 || p.RemovalSafetyVersion != 0 || p.RemovalTargets.Count != 0)
            throw new InvalidOperationException("일반 속성규격과 삭제 전용 조건을 섞을 수 없습니다.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var compact = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in p.Parameters)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.Name) || s.Name != s.Name.Trim() || s.Name.Length > 200 || !names.Add(s.Name) || !compact.Add(Compact(s.Name)) ||
                s.Binding is not ("인스턴스" or "유형") || !DataTypes.ContainsKey(s.DataType ?? "") || !Groups.ContainsKey(s.Group ?? "") || s.SourceRow < 1 ||
                s.Legacy == null || s.Categories == null || s.ExcludeCategories == null || s.AllCategories == (s.Categories.Count > 0) ||
                !s.AllCategories && s.ExcludeCategories.Count > 0)
                throw new InvalidOperationException("속성규격 매개변수 표를 확인하세요(이름 중복·유형·데이터 유형·그룹·카테고리): " + s?.Name);
            foreach (var c in s.Categories.Concat(s.ExcludeCategories))
                if (!Regex.IsMatch(c ?? "", @"\AOST_[A-Za-z0-9_]{1,80}\z")) throw new InvalidOperationException(s.Name + ": 카테고리는 Revit 기본 카테고리 이름(OST_…)으로 지정하세요: " + c);
        }
        foreach (var s in p.Parameters)
            foreach (var legacy in s.Legacy)
                if (string.IsNullOrWhiteSpace(legacy) || legacy == s.Name || legacy.Length > 200)
                    throw new InvalidOperationException(s.Name + ": 이전 이름은 비어 있거나 자기 이름과 같을 수 없습니다: " + legacy);
        var legacyAll = p.Parameters.SelectMany(s => s.Legacy).ToList();
        if (legacyAll.Count != legacyAll.Distinct(StringComparer.Ordinal).Count()) throw new InvalidOperationException("같은 이전 이름이 여러 매개변수에 지정되었습니다.");
    }

    // 모델의 각 정의가 어느 규격 항목에 속하는지 정한다. 이전 이름이 다른 규격 이름과 같아도(예: 구조) 데이터 유형으로 구분한다.
    internal static SpecParameter? Claim(ParameterDefinitionInfo d, SpecRuleProfile profile)
    {
        var byName = profile.Parameters.FirstOrDefault(s => s.Name == d.Name);
        if (byName != null && BaseId(d.DataType) == DataTypes[byName.DataType]) return byName;
        var legacy = profile.Parameters.FirstOrDefault(s => s.Legacy.Contains(d.Name, StringComparer.Ordinal));
        if (legacy != null && (byName == null || BaseId(d.DataType) == DataTypes[legacy.DataType])) return legacy;
        return byName ?? profile.Parameters.FirstOrDefault(s => Compact(s.Name) == Compact(d.Name)) ??
            profile.Parameters.FirstOrDefault(s => s.Legacy.Any(l => Compact(l) == Compact(d.Name)));
    }

    public static RulePlan Plan(RuleSet file, IEnumerable<ModelSnapshot> models, IEnumerable<ValueEdit> pending)
    {
        Validate(file);
        if (pending.Any(e => e.DefinitionFix == null)) throw new InvalidOperationException("속성규격 정리는 값 변경과 섞어 적용하지 않습니다. 기존 변경 예정을 적용하거나 정리한 뒤 다시 불러오세요.");
        var profile = file.Spec!;
        var list = models.ToList();
        if (list.Any(m => m.Definitions == null)) throw new InvalidOperationException("매개변수 정의를 아직 조회하지 않은 파일이 있습니다. 속성규격 룰셋을 선택한 상태에서 다시 조회하세요.");
        var plan = new RulePlan();
        if (profile.RemoveOnly)
        {
            var removal = PlanRemoveOnly(file, list, plan);
            RequireRemovalPending(removal, pending);
            return removal;
        }
        if (pending.Any(e => e.DefinitionFix?.Removal != null)) throw new InvalidOperationException("삭제 전용 변경이 남아 있습니다. 기존 변경을 정리한 뒤 다른 룰셋을 사용하세요.");

        // 같은 이름·데이터 유형의 공유 GUID를 사용하는 모델 수로 센다. 최다 GUID가 유일할 때만 추가·병합 기준으로 쓴다.
        var canonical = new Dictionary<string, ParameterDefinitionInfo>(StringComparer.Ordinal);
        foreach (var s in profile.Parameters)
        {
            var found = list.SelectMany(m => m.Definitions!.Definitions.Where(d => d.Name == s.Name && d.Guid.Length > 0 && BaseId(d.DataType) == DataTypes[s.DataType])
                .GroupBy(d => d.Guid, StringComparer.OrdinalIgnoreCase).Select(g => g.First())).ToList();
            var counts = found.GroupBy(d => d.Guid, StringComparer.OrdinalIgnoreCase).Select(g => (Guid: g.Key, Count: g.Count(), First: g.First())).OrderByDescending(x => x.Count).ToList();
            if (counts.Count == 0) continue;
            if (counts.Count > 1 && counts[0].Count == counts[1].Count)
            {
                plan.Notes.Add($"[규격 GUID 확인 필요] {s.Name}: 모델마다 다른 공유 GUID가 같은 수로 쓰입니다({string.Join(", ", counts.Select(c => c.Guid + " " + c.Count + "개"))}). 추가·병합 기준을 정하지 않습니다.");
                continue;
            }
            if (counts.Count > 1) plan.Notes.Add($"[규격 GUID 다수] {s.Name}: 가장 많은 모델({counts[0].Count}개)이 쓰는 GUID {counts[0].Guid}를 기준으로 합니다. 다른 GUID {counts.Count - 1}종은 해당 모델에서 보고만 합니다.");
            canonical[s.Name] = counts[0].First;
        }

        int fixes = 0, reports = 0, ok = 0;
        foreach (var model in list)
        {
            var info = model.Definitions!;
            var title = model.Title;
            void Report(string text) { reports++; plan.Notes.Add($"[확인] {title} / {text}"); }
            void Fix(string action, string name, string key, string before, string after, string reason, DefinitionFix fix)
            {
                fixes++;
                fix.Action = action; fix.Name = name;
                var row = new ParameterRow
                {
                    ModelPath = model.Path, ModelTitle = title, OwnerId = "def:" + action + ":" + key, ElementId = fix.ElementId, Scope = DefinitionScope,
                    Category = DefinitionScope, Family = DefinitionScope, TypeName = action, Name = name, ParameterKey = key, Display = before, HasValue = before.Length > 0,
                    BlockReason = "매개변수 정의 정리 — 값 편집 불가",
                };
                plan.Edits.Add(new ValueEdit { Original = row, Value = after, Reason = "룰셋: " + file.Name + " / " + profile.Source + " / " + reason, DefinitionFix = fix });
            }

            var claims = info.Definitions.Select(d => (Def: d, Spec: Claim(d, profile))).ToList();
            // 규격과 무관한 중복도 보고한다(같은 이름 또는 띄어쓰기·대소문자만 다른 이름).
            foreach (var g in claims.Where(c => c.Spec == null).GroupBy(c => Compact(c.Def.Name)).Where(g => g.Count() > 1))
                Report($"규격 외 중복 매개변수: {string.Join(" / ", g.Select(c => Describe(c.Def)))}");

            foreach (var s in profile.Parameters)
            {
                var source = $"{s.SourceRow}행 {s.Name}";
                int fixesBefore = fixes, reportsBefore = reports;
                var mine = claims.Where(c => ReferenceEquals(c.Spec, s)).Select(c => c.Def).ToList();
                var exact = mine.Where(d => d.Name == s.Name).ToList();
                var similar = mine.Where(d => d.Name != s.Name).ToList();
                canonical.TryGetValue(s.Name, out var canon);
                var expectedType = DataTypes[s.DataType];
                var expectedGroup = Groups[s.Group];
                var required = s.AllCategories ? info.AllCategories.Except(s.ExcludeCategories, StringComparer.Ordinal).ToList() : s.Categories.Where(c => info.AllCategories.Contains(c, StringComparer.Ordinal)).ToList();
                var unbindable = s.Categories.Where(c => !info.AllCategories.Contains(c, StringComparer.Ordinal)).ToList();
                if (unbindable.Count > 0) Report($"{s.Name}: 규격 카테고리 {string.Join(", ", unbindable)}는 이 모델에서 매개변수를 연결할 수 없는 카테고리입니다 ({source}).");

                ParameterDefinitionInfo? keeper = null;
                bool adding = false;
                if (exact.Count == 0)
                {
                    var what = similar.Count == 0 ? "매개변수 없음" : $"규격 이름은 없고 {string.Join(" / ", similar.Select(Describe))}만 있음";
                    if (!profile.AddMissing || similar.Count > 0 && !profile.MergeDuplicates) { Report($"{s.Name}: {what}. 룰셋의 추가·병합 허용이 꺼져 있어 보고만 합니다 ({source})."); continue; }
                    if (canon == null)
                    {
                        Report($"{s.Name}: {what}. 다른 조회 모델에 같은 이름·데이터 유형의 공유 매개변수가 없어 추가할 GUID가 없습니다" +
                            (similar.Count > 0 ? "(공유 매개변수 이름은 Revit에서 바꿀 수 없음)" : "") + $" ({source}).");
                        continue;
                    }
                    if (info.Definitions.Any(d => string.Equals(d.Guid, canon.Guid, StringComparison.OrdinalIgnoreCase)))
                    {
                        Report($"{s.Name}: 기준 GUID {canon.Guid}가 다른 이름({info.Definitions.First(d => string.Equals(d.Guid, canon.Guid, StringComparison.OrdinalIgnoreCase)).Name})으로 이미 있습니다. 추가하지 않습니다 ({source}).");
                        continue;
                    }
                    adding = true;
                }
                else
                {
                    keeper = canon == null ? exact.Count == 1 ? exact[0] : null : exact.FirstOrDefault(d => string.Equals(d.Guid, canon.Guid, StringComparison.OrdinalIgnoreCase)) ?? (exact.Count == 1 ? exact[0] : null);
                    if (keeper == null)
                    {
                        Report($"{s.Name}: 같은 이름 정의 {exact.Count}개 중 기준을 정할 수 없습니다 — {string.Join(" / ", exact.Select(Describe))} ({source})");
                        continue;
                    }
                    if (canon != null && !string.Equals(keeper.Guid, canon.Guid, StringComparison.OrdinalIgnoreCase))
                        Report($"{s.Name}: 다른 모델 다수의 공유 GUID({canon.Guid})와 다른 정의입니다 — {Describe(keeper)}. 보고만 합니다.");
                }

                var keeperKey = adding ? "guid:" + canon!.Guid : keeper!.Key;
                var keeperBinding = adding ? s.Binding : keeper!.Binding;
                var keeperType = BaseId(adding ? canon!.DataType : keeper!.DataType);
                if (adding)
                {
                    Fix(ActionAdd, s.Name, keeperKey, "(없음)", $"{s.Binding} · {s.DataType} · 그룹 {s.Group} · 카테고리 {CategoryText(required, info)}",
                        source + " · 다른 모델과 같은 GUID " + canon!.Guid, new DefinitionFix
                        { Guid = canon.Guid, DataType = canon.DataType, Group = expectedGroup, Binding = s.Binding, Categories = required });
                }
                else
                {
                    var k = keeper!;
                    if (BaseId(k.DataType) != expectedType)
                        Report($"{s.Name}: 데이터 유형이 규격과 다릅니다(현재 {TypeLabel(k.DataType)} / 규격 {s.DataType}). Revit은 기존 매개변수의 데이터 유형을 바꿀 수 없어 보고만 합니다 ({source}).");
                    if (k.Binding != s.Binding && k.ValueCount != 0)
                        Report($"{s.Name}: {k.Binding} 매개변수지만 규격은 {s.Binding}입니다. 값이 있는 요소 {(k.ValueCount < 0 ? "수 확인 불가" : k.ValueCount.ToString("N0") + "개")}가 있어 전환하면 값이 사라지므로 보고만 합니다 ({source}).");
                    else if (k.Binding != s.Binding)
                    {
                        Fix(ActionBinding, s.Name, k.Key, $"{k.Binding} · {k.Categories.Count:N0}개 카테고리 · 그룹 {GroupLabel(k.Group)}", $"{s.Binding} · 카테고리 {CategoryText(required, info)} · 그룹 {s.Group}",
                            source + " · 입력된 값 없음 확인", new DefinitionFix
                            { ElementId = k.ElementId, ElementUniqueId = k.ElementUniqueId, Binding = s.Binding, Categories = Union(k.Categories, required), Group = expectedGroup, ValueCount = k.ValueCount });
                        keeperBinding = s.Binding;
                    }
                    if (k.Binding == keeperBinding)
                    {
                        var missing = required.Except(k.Categories, StringComparer.Ordinal).ToList();
                        if (missing.Count > 0)
                            Fix(ActionCategories, s.Name, k.Key, $"{k.Categories.Count:N0}개 카테고리", $"+{missing.Count:N0}개: {CategoryText(missing, info)}", source, new DefinitionFix
                            { ElementId = k.ElementId, ElementUniqueId = k.ElementUniqueId, Binding = k.Binding, Categories = Union(k.Categories, missing), Group = BaseId(k.Group), ValueCount = k.ValueCount });
                        if (BaseId(k.Group) != expectedGroup)
                            Fix(ActionGroup, s.Name, k.Key, GroupLabel(k.Group), s.Group, source, new DefinitionFix { ElementId = k.ElementId, ElementUniqueId = k.ElementUniqueId, Group = expectedGroup, Binding = k.Binding, ValueCount = k.ValueCount });
                        var extra = k.Categories.Except(required, StringComparer.Ordinal).Except(s.ExcludeCategories, StringComparer.Ordinal).ToList();
                        if (extra.Count > 0 && !s.AllCategories)
                            Report($"{s.Name}: 규격 밖 카테고리 {extra.Count}개에도 연결되어 있습니다({CategoryText(extra, info)}). 값 손실 우려로 제거하지 않습니다.");
                        var excluded = s.ExcludeCategories.Where(c => k.Categories.Contains(c, StringComparer.Ordinal)).ToList();
                        if (excluded.Count > 0)
                            Report($"{s.Name}: 규격에서 제외한 카테고리({CategoryText(excluded, info)})에 연결되어 있습니다. 값 손실 우려로 제거하지 않습니다.");
                    }
                }

                // 기준 외 같은 이름 정의·이전 이름·띄어쓰기만 다른 정의는 값을 옮긴 뒤 제거한다.
                foreach (var d in exact.Where(d => !ReferenceEquals(d, keeper)).Concat(similar))
                {
                    if (!profile.MergeDuplicates) { Report($"{s.Name}: 중복 정의 {Describe(d)} — 병합 허용이 꺼져 있어 보고만 합니다."); continue; }
                    if (d.ValueCount < 0) { Report($"{s.Name}: 중복 정의 {Describe(d)}의 값 존재 여부를 확인하지 못해 제거하지 않습니다."); continue; }
                    if (d.ValueCount > 0 && (d.Binding != keeperBinding || BaseId(d.DataType) != keeperType))
                    {
                        Report($"{s.Name}: 중복 정의 {Describe(d)}에 값이 있는 요소 {d.ValueCount:N0}개가 있지만 기준과 유형/인스턴스 또는 데이터 유형이 달라 옮길 수 없습니다. 보고만 합니다.");
                        continue;
                    }
                    Fix(ActionMerge, s.Name, d.Key, $"{d.Name} ({DefinitionKind(d)}, 값 있는 요소 {d.ValueCount:N0}개)", $"값을 '{s.Name}'로 옮긴 뒤 '{d.Name}' 정의 제거",
                        source + (d.ValueCount > 0 ? " · 같은 요소에 서로 다른 값이 있으면 이 모델 전체를 보류" : " · 값 없음"), new DefinitionFix
                        { ElementId = d.ElementId, ElementUniqueId = d.ElementUniqueId, SourceName = d.Name, TargetKey = keeperKey, Binding = d.Binding, ValueCount = d.ValueCount });
                }
                if (!adding && fixes == fixesBefore && reports == reportsBefore) ok++;
            }
        }
        foreach (var edit in pending)
            if (plan.Edits.All(e => e.Original.Key != edit.Original.Key))
                throw new InvalidOperationException("이전에 준비한 매개변수 정의 정리가 이번 결과와 다릅니다. 변경 예정을 모두 취소한 뒤 다시 불러오세요.");
        plan.MatchedOwners = list.Count;
        plan.Notes.Insert(0, $"속성규격 {profile.Parameters.Count:N0}개 × 파일 {list.Count:N0}개 · 규격 일치 {ok:N0}건 · 정리 예정 {fixes:N0}건 · 확인 필요(보고만) {reports:N0}건");
        return plan;
    }

    private static RulePlan PlanRemoveOnly(RuleSet file, List<ModelSnapshot> list, RulePlan plan)
    {
        var profile = file.Spec!;
        int removes = 0, skips = 0;
        foreach (var model in list)
        {
            var defs = model.Definitions!.Definitions;
            foreach (var s in profile.RemovalTargets)
            {
                var targets = defs.Where(d => d.Name == s.Name).ToList();
                if (targets.Count == 0) continue;
                var keepers = defs.Where(d => d.Name == s.KeepName).ToList();
                if (s.KeepName.Length > 0 && keepers.Count != 1)
                {
                    skips += targets.Count;
                    plan.Notes.Add($"[확인] {model.Title} / {s.Name}: 남길 '{s.KeepName}' 정의가 {keepers.Count}개라 삭제하지 않습니다.");
                    continue;
                }
                foreach (var d in targets)
                {
                    if (!d.Categories.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(s.Categories.OrderBy(c => c, StringComparer.Ordinal), StringComparer.Ordinal))
                    {
                        skips++;
                        plan.Notes.Add($"[확인] {model.Title} / '{d.Name}': 카테고리가 {string.Join(", ", s.Categories.Select(c => CategoryName(c, model.Definitions!)))}만이 아니라 삭제하지 않습니다(현재 {string.Join(", ", d.Categories.Select(c => CategoryName(c, model.Definitions!)))}).");
                        continue;
                    }
                    var keeper = s.KeepName.Length > 0 ? keepers[0] : null;
                    if (keeper != null && !keeper.Categories.Contains("OST_ProjectInformation", StringComparer.Ordinal))
                    {
                        skips++; plan.Notes.Add($"[확인] {model.Title} / '{d.Name}': 남길 '{s.KeepName}'가 프로젝트 정보에 연결되어 있지 않아 삭제하지 않습니다."); continue;
                    }
                    if (!DefinitionRemovalChecks.HasEvidence(d) || keeper != null && !DefinitionRemovalChecks.HasEvidence(keeper))
                    {
                        skips++; plan.Notes.Add($"[확인] {model.Title} / '{d.Name}': 정의·기존 값의 안전검증 정보가 없습니다. 0.3.7 이상에서 다시 조회하세요."); continue;
                    }
                    removes++;
                    var row = new ParameterRow
                    {
                        ModelPath = model.Path, ModelTitle = model.Title, OwnerId = "def:" + ActionRemove + ":" + d.Key, ElementId = d.ElementId, Scope = DefinitionScope,
                        Category = DefinitionScope, Family = DefinitionScope, TypeName = ActionRemove, Name = d.Name, ParameterKey = d.Key, HasValue = true,
                        Display = $"{d.Name} ({DefinitionKind(d)}, 값 있는 요소 {(d.ValueCount < 0 ? "확인 불가" : d.ValueCount.ToString("N0") + "개")})",
                        BlockReason = "매개변수 정의 정리 — 값 편집 불가",
                    };
                    plan.Edits.Add(new ValueEdit
                    {
                        Original = row, Value = $"'{d.Name}' 삭제 (값은 옮기지 않음" + (keeper == null ? ")" : $" · '{s.KeepName}' 유지)"), Reason = "룰셋: " + file.Name + " / " + profile.Source + " / 정확한 이름·프로젝트 정보 단독 연결",
                        DefinitionFix = new DefinitionFix { Action = ActionRemove, Name = keeper?.Name ?? d.Name, ElementId = d.ElementId, ElementUniqueId = d.ElementUniqueId, SourceName = d.Name, TargetKey = keeper?.Key ?? "", Binding = d.Binding, ValueCount = d.ValueCount,
                            Removal = new() { Source = DefinitionRemovalChecks.Copy(d), Keeper = keeper == null ? null : DefinitionRemovalChecks.Copy(keeper), AllowedCategories = [.. s.Categories] } },
                    });
                }
            }
        }
        plan.MatchedOwners = list.Count;
        plan.Notes.Insert(0, $"삭제 전용 · 파일 {list.Count:N0}개 · 삭제 예정 {removes:N0}개 · 조건 불충족으로 유지 {skips:N0}개");
        return plan;
    }

    public static void RequireRemovalPending(RulePlan plan, IEnumerable<ValueEdit> pending)
    {
        var expected = plan.Edits.ToDictionary(e => e.Original.Key);
        foreach (var edit in pending)
            if (edit.DefinitionFix?.Action != ActionRemove || edit.DefinitionFix.Removal == null ||
                !expected.TryGetValue(edit.Original.Key, out var fresh) || edit.Value != fresh.Value || edit.Mode != fresh.Mode ||
                JsonSerializer.Serialize(edit.DefinitionFix) != JsonSerializer.Serialize(fresh.DefinitionFix))
                throw new InvalidOperationException("다른 정리 또는 이전 조회의 변경 예정이 남아 있어 삭제 전용 작업을 준비하지 않았습니다. ‘변경 내용 확인’에서 기존 변경을 제외한 뒤 삭제 룰셋을 다시 검토하세요. 기존 변경은 보존했습니다.");
    }

    public static int EntryCount(SpecRuleProfile profile) => profile.RemoveOnly ? profile.RemovalTargets.Count : profile.Parameters.Count;

    private static List<string> Union(IEnumerable<string> a, IEnumerable<string> b) => a.Concat(b).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
    private static string DefinitionKind(ParameterDefinitionInfo d) => (d.Guid.Length > 0 ? "공유" : "프로젝트") + " " + d.Binding + " · " + TypeLabel(d.DataType) + " · " + GroupLabel(d.Group);
    private static string Describe(ParameterDefinitionInfo d) => $"'{d.Name}'({DefinitionKind(d)}" + (d.Guid.Length > 0 ? " · GUID " + d.Guid[..Math.Min(8, d.Guid.Length)] + "…" : "") + ")";
    public static string CategoryName(string key, DefinitionSnapshot info) => info.CategoryNames.GetValueOrDefault(key, key);
    private static string CategoryText(IReadOnlyCollection<string> keys, DefinitionSnapshot info) =>
        keys.Count > 8 ? string.Join(", ", keys.Take(8).Select(k => CategoryName(k, info))) + $" 외 {keys.Count - 8:N0}개" : string.Join(", ", keys.Select(k => CategoryName(k, info)));
}
