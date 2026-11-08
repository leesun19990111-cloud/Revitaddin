namespace SunnyParameterManager.Core;

// 기준 모델의 공유 매개변수 정의를 대상별로 비교한다. 충돌은 자동 병합하지 않는다.
public static class DefinitionTransferPlanner
{
    public sealed record Decision(string Name, string Guid, string Status, string Reason, ValueEdit? Edit);
    public sealed record ValueDecision(string Name, string Target, string Status, string Reason, ValueEdit? Edit);

    public static QueryPlan DefinitionQuery() => new()
    {
        Scopes = ["프로젝트 정보"], Names = ["매개변수 정의 목록"], IncludeDefinitions = true, TransferOnly = true
    };

    public static QueryPlan ValueQuery(IEnumerable<ParameterDefinitionInfo> selected)
    {
        var list = selected.ToList();
        if (list.Count == 0) throw new InvalidOperationException("복제할 공유 매개변수를 먼저 선택하세요.");
        return new QueryPlan
        {
            Scopes = list.SelectMany(d => d.Binding == "유형" ? new[] { "유형" } :
                d.Categories.Contains("OST_ProjectInformation") ? new[] { "인스턴스", "프로젝트 정보" } : new[] { "인스턴스" }).Distinct().ToList(),
            Names = list.Select(d => d.Name).Distinct(StringComparer.Ordinal).ToList(),
            Keys = list.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TransferOnly = true
        };
    }

    public static IReadOnlyList<Decision> PlanDefinitions(ModelSnapshot source, ModelSnapshot target, IEnumerable<ParameterDefinitionInfo> selected)
    {
        if (source.Definitions == null || target.Definitions == null) throw new InvalidOperationException("기준 및 대상 모델의 매개변수 정의를 먼저 조회하세요.");
        if (source.Identity == target.Identity || string.Equals(source.CanonicalPath, target.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("기준 모델과 동일한 파일을 대상으로 선택할 수 없습니다.");
        var result = new List<Decision>();
        foreach (var def in selected)
        {
            if (!source.Definitions.Definitions.Any(d => d.Key == def.Key && d.Revision == def.Revision))
                throw new InvalidOperationException("기준 모델의 정의가 선택 당시와 다릅니다. 다시 조회하세요: " + def.Name);
            Decision Block(string reason) => new(def.Name, def.Guid, "제외", reason, null);
            if (!Guid.TryParse(def.Guid, out var guid)) { result.Add(Block("GUID가 없는 일반 프로젝트 매개변수입니다. 동일 정의로 복제할 수 없습니다.")); continue; }
            if (source.Definitions.Definitions.Count(d => d.Guid.Equals(def.Guid, StringComparison.OrdinalIgnoreCase)) != 1) { result.Add(Block("기준 모델에 같은 GUID 정의가 여러 개입니다.")); continue; }
            if (def.Categories.Count == 0 || def.Categories.Any(c => !target.Definitions.AllCategories.Contains(c, StringComparer.Ordinal)))
            { result.Add(Block("대상 모델에 연결할 수 없는 카테고리가 있습니다: " + string.Join(", ", def.Categories.Except(target.Definitions.AllCategories)))); continue; }
            var sameGuid = target.Definitions.Definitions.Where(d => d.Guid.Equals(def.Guid, StringComparison.OrdinalIgnoreCase)).ToList();
            var sameName = target.Definitions.Definitions.Where(d => d.Name == def.Name).ToList();
            if (sameGuid.Count > 1 || sameName.Any(d => !d.Guid.Equals(def.Guid, StringComparison.OrdinalIgnoreCase)))
            { result.Add(Block("같은 이름의 다른 매개변수 또는 중복 GUID가 이미 있습니다.")); continue; }
            if (sameGuid.Count == 1)
            {
                var existing = sameGuid[0];
                if (existing.Name != def.Name || existing.DataType != def.DataType || existing.Binding != def.Binding ||
                    SpecRulePlanner.BaseId(existing.Group) != SpecRulePlanner.BaseId(def.Group) ||
                    !existing.Categories.OrderBy(x => x).SequenceEqual(def.Categories.OrderBy(x => x)))
                    result.Add(Block("같은 GUID가 있지만 이름·자료형·그룹·유형/인스턴스·카테고리 연결이 다릅니다."));
                else result.Add(new(def.Name, def.Guid, "이미 있음", "정의가 일치합니다. 추가하지 않습니다.", null));
                continue;
            }
            var key = "guid:" + guid.ToString("D");
            var row = new ParameterRow { ModelPath = target.Path, ModelTitle = target.Title, OwnerId = "def:add:" + guid.ToString("D"),
                Scope = "매개변수 정의", TypeName = "매개변수 추가", ParameterKey = key, Name = def.Name, DataType = def.DataType, Display = "(없음)" };
            var edit = new ValueEdit { Original = row, Reason = "기준 모델: " + source.Title,
                DefinitionFix = new DefinitionFix { Action = SpecRulePlanner.ActionAdd, Name = def.Name, Guid = guid.ToString("D"),
                    DataType = def.DataType, Group = SpecRulePlanner.BaseId(def.Group), Binding = def.Binding, Categories = [.. def.Categories] } };
            result.Add(new(def.Name, def.Guid, "추가 예정", "기준 모델과 같은 GUID 및 정의로 추가합니다.", edit));
        }
        return result;
    }

    // 값 전송은 정의 적용 뒤 새 조회에 대해서만 수행한다. 모호한 대상은 누락 이유로 남긴다.
    public static IReadOnlyList<ValueDecision> PlanValues(ModelSnapshot source, ModelSnapshot target, IEnumerable<ParameterDefinitionInfo> selected)
    {
        var keys = selected.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetIndex = target.Rows.Where(r => keys.Contains(r.ParameterKey)).GroupBy(r => MatchKey(r))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<ValueDecision>();
        foreach (var src in source.Rows.Where(r => keys.Contains(r.ParameterKey) && r.HasValue && (r.Storage != "String" || r.Raw.Length > 0)))
        {
            ValueDecision Skip(string reason) => new(src.Name, src.TargetName, "제외", reason, null);
            if (src.Scope is not ("유형" or "인스턴스" or "프로젝트 정보") || src.Storage is not ("String" or "Integer" or "Double") || src.Editor is "선택" or "")
            { result.Add(Skip("이 값 형식은 자동 복사하지 않습니다.")); continue; }
            var matches = targetIndex.GetValueOrDefault(MatchKey(src), []);
            if (matches.Count != 1) { result.Add(Skip(matches.Count == 0 ? "같은 대상이 없습니다. 인스턴스는 동일 UniqueId만 허용합니다." : "대상 요소가 여러 개로 식별됩니다.")); continue; }
            var dst = matches[0];
            if (!dst.Editable || dst.Storage != src.Storage || dst.DataType != src.DataType || dst.Editor != src.Editor)
            { result.Add(Skip(dst.Editable ? "자료형 또는 입력 형식이 다릅니다." : dst.BlockReason)); continue; }
            if (dst.HasValue && dst.Raw == src.Raw) { result.Add(new(src.Name, src.TargetName, "같음", "기존 값과 같습니다.", null)); continue; }
            var edit = new ValueEdit { Original = dst, Value = src.Display, Mode = "원시값", RawValue = src.Raw,
                Reason = "기준 모델: " + source.Title + (src.Scope == "인스턴스" ? " · 동일 UniqueId" : " · 동일 유형/프로젝트 정보") };
            result.Add(new(src.Name, src.TargetName, "변경 예정", dst.ValueLabel + " → " + src.ValueLabel, edit));
        }
        return result;
    }

    private static string MatchKey(ParameterRow row) => row.Scope switch
    {
        "인스턴스" => "i|" + row.OwnerId + "|" + row.ParameterKey,
        "프로젝트 정보" => "p|" + row.ParameterKey,
        "유형" => "t|" + row.TypeKey + "|" + row.ParameterKey,
        _ => "skip|" + row.Key
    };
}
