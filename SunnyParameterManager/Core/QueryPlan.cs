namespace SunnyParameterManager.Core;

// 조회 범위는 결과와 함께 보관한다. null만 기존 전체 조회이며 빈 목록은 전체 조회가 아니다.
public sealed class QueryPlan
{
    public int Version { get; set; } = 1;
    public List<string> Scopes { get; set; } = [];
    public List<string> Names { get; set; } = [];
    public List<string> Keys { get; set; } = [];
    public bool IncludeOuterMaterial { get; set; }
    public bool IncludeCleanup { get; set; }
    public bool IncludeDefinitions { get; set; }

    public void Validate()
    {
        if (Version != 1 || Scopes == null || Names == null || Keys == null || Scopes.Count is < 1 or > 3 ||
            Scopes.Any(s => s is not ("유형" or "인스턴스" or "프로젝트 정보")) || Names.Count is < 1 or > 50000 ||
            Names.Any(string.IsNullOrWhiteSpace) || Keys.Count > 50000 || Keys.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("선택 조회 범위가 올바르지 않습니다. 룰셋을 다시 불러오세요.");
        if (IncludeCleanup && (!Scopes.Contains("유형") || !Scopes.Contains("인스턴스")))
            throw new InvalidOperationException("유형 정리는 유형·인스턴스 전체 근거 조회가 필요합니다.");
    }
    public bool Includes(string name, string key) => Names.Contains(name, StringComparer.Ordinal) || Keys.Contains(key, StringComparer.OrdinalIgnoreCase);
    public bool Covers(QueryPlan required)
    {
        Validate(); required.Validate();
        return required.Scopes.All(Scopes.Contains) && required.Names.All(Names.Contains) &&
            required.Keys.All(k => Keys.Contains(k, StringComparer.OrdinalIgnoreCase)) && (!required.IncludeOuterMaterial || IncludeOuterMaterial) && (!required.IncludeCleanup || IncludeCleanup) && (!required.IncludeDefinitions || IncludeDefinitions);
    }
    public static QueryPlan FromRules(RuleSet rules)
    {
        RulePlanner.Validate(rules);
        var plan = new QueryPlan();
        if (rules.Version == 5)
        {
            // 정의 목록이 본체다. 프로젝트 정보만 함께 읽어 요소 전체 수집을 피한다.
            plan.Scopes = ["프로젝트 정보"];
            plan.Names = rules.Spec!.RemoveOnly ? rules.Spec.RemovalTargets.SelectMany(t => new[] { t.Name, t.KeepName }).Where(n => n.Length > 0).Distinct().ToList() : rules.Spec.Parameters.Select(p => p.Name).Distinct().ToList();
            plan.IncludeDefinitions = true;
        }
        else if (rules.Version == 4)
        {
            plan.Scopes = ["유형", "인스턴스"];
            plan.Names = ["유형명 정리", .. CleanupPlanner.Flags];
            plan.Keys = [CleanupPlanner.WorksetKey, CleanupPlanner.RoomKey, KacRulePlanner.IfcInstanceKey];
            plan.IncludeCleanup = true;
        }
        else if (rules.Version == 3)
        {
            plan.Scopes = ["프로젝트 정보"]; plan.Names = ["공항코드", "건축물번호"];
            if (rules.Space!.MapAssetNumber) plan.Names.Add("자산번호");
        }
        else if (rules.Version == 2)
        {
            // 반대 범위의 필드 존재도 기존 KAC 누락 판단에 필요하다. 바인딩/적용 범위는 바꾸지 않는다.
            plan.Scopes = ["유형", "인스턴스"];
            plan.Names = ["다음으로 IFC로 내보내기", "제작정보", "KBIMS-부위코드", "조달청표준공사코드", "재료명", "업체명"];
            if (rules.Kac!.NormalizeFloorName) plan.Names.Add("층");
            plan.Keys = [KacRulePlanner.IfcTypeKey, KacRulePlanner.IfcInstanceKey];
            plan.IncludeOuterMaterial = true;
        }
        else
        {
            plan.Scopes = rules.Rules.Select(r => r.Match.Scope).Distinct().ToList();
            plan.Names = rules.Rules.SelectMany(r => r.Set).Select(v => v.Name).Distinct().ToList();
            plan.Keys = rules.Rules.SelectMany(r => r.Set).Where(v => v.ParameterKey != null).Select(v => v.ParameterKey!).Distinct().ToList();
        }
        plan.Validate(); return plan;
    }
    public static void RequireCoverage(IEnumerable<ModelSnapshot> models, RuleSet rules)
    {
        var required = FromRules(rules);
        foreach (var model in models)
            if (model.Query != null && !model.Query.Covers(required) || required.IncludeCleanup && model.Query?.IncludeCleanup != true || required.IncludeDefinitions && model.Definitions == null)
                throw new InvalidOperationException(model.Title + ": 이 룰셋에 필요한 항목을 아직 조회하지 않았습니다. 변경 예정을 적용하거나 정리한 뒤 ‘다시 조회’를 누르세요. 미조회 값은 빈 값이나 ND로 처리하지 않습니다.");
    }
}
