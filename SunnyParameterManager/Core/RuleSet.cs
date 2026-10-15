using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunnyParameterManager.Core;

// 실행 코드가 아닌 선언형 데이터만 읽는다. 알 수 없는 조건/속성을 무시하면 대상이 넓어지므로 거부한다.
public sealed class RuleSet
{
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public bool SampleOnly { get; set; }
    public List<ParameterRule> Rules { get; set; } = [];
    public KacRuleProfile? Kac { get; set; }
    public SpaceRuleProfile? Space { get; set; }
}

public sealed class ParameterRule
{
    public string Name { get; set; } = "";
    public RuleMatch Match { get; set; } = new();
    public List<RuleValue> Set { get; set; } = [];
}

public sealed class RuleMatch
{
    public string Scope { get; set; } = "";
    public string? Model { get; set; }
    public string? Category { get; set; }
    public string? Family { get; set; }
    public string? Type { get; set; }
    public string TypeMatch { get; set; } = "일치";
}

public sealed class RuleValue
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Storage { get; set; } = "";
    public string? ParameterKey { get; set; }
    // 빈 문자열은 명시적 지우기지만 누락/null은 오류다.
    public string? Value { get; set; }
}

public sealed class RulePlan
{
    public List<ValueEdit> Edits { get; } = [];
    public List<string> Notes { get; } = [];
    public int MatchedOwners { get; set; }
}

public static class RulePlanner
{
    public static RuleSet Parse(string json)
    {
        if (json.Length > 2 * 1024 * 1024) throw new InvalidOperationException("룰셋은 2MB 이하만 지원합니다.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            CheckProperties(document.RootElement);
            var rules = JsonSerializer.Deserialize<RuleSet>(json, new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 });
            Validate(rules);
            return rules!;
        }
        catch (JsonException ex) { throw new InvalidOperationException("룰셋 형식이 올바르지 않습니다. 파일을 수정한 뒤 다시 불러오세요. " + ex.Message, ex); }
    }

    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new InvalidOperationException("룰셋에 같은 속성이 중복되었습니다: " + p.Name);
                CheckProperties(p.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckProperties(item);
    }

    private static void Validate(RuleSet? file)
    {
        if (file?.Version == 3)
        {
            SpaceRulePlanner.Validate(file);
            return;
        }
        if (file?.Version == 2)
        {
            KacRulePlanner.Validate(file);
            return;
        }
        if (file == null || file.Version != 1 || file.Kac != null || file.Space != null || string.IsNullOrWhiteSpace(file.Name) || file.Rules == null || file.Rules.Count is < 1 or > 500)
            throw new InvalidOperationException("룰셋 버전(1), 이름, 1~500개 규칙이 필요합니다.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in file.Rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Name) || !names.Add(rule.Name) || rule.Match == null || rule.Set == null || rule.Set.Count is < 1 or > 100)
                throw new InvalidOperationException("규칙 이름은 비어 있거나 중복될 수 없으며, 조건과 1~100개 입력값이 필요합니다.");
            var match = rule.Match;
            if (match.Scope is not ("유형" or "인스턴스" or "프로젝트 정보") || match.TypeMatch is not ("일치" or "시작" or "포함"))
                throw new InvalidOperationException(rule.Name + ": 지원하지 않는 범위 또는 유형명 비교 방식입니다.");
            if (match.Scope != "프로젝트 정보" && string.IsNullOrWhiteSpace(match.Type))
                throw new InvalidOperationException(rule.Name + ": 유형명 조건을 반드시 지정하세요. 전체 유형 자동 적용은 지원하지 않습니다.");
            if (new[] { match.Model, match.Category, match.Family, match.Type }.Any(s => s != null && string.IsNullOrWhiteSpace(s)))
                throw new InvalidOperationException(rule.Name + ": 비어 있는 조건 대신 해당 속성을 생략하세요.");
            foreach (var value in rule.Set)
            {
                if (value == null || string.IsNullOrWhiteSpace(value.Name) || string.IsNullOrWhiteSpace(value.DataType) || value.Storage is not ("String" or "Integer" or "Double" or "ElementId") || value.Value == null)
                    throw new InvalidOperationException(rule.Name + ": 매개변수 이름·자료형·저장형식·입력값을 모두 지정하세요.");
                if (value.ParameterKey != null &&
                    !(value.ParameterKey.StartsWith("guid:", StringComparison.Ordinal) && Guid.TryParse(value.ParameterKey[5..], out _) ||
                      value.ParameterKey.StartsWith("builtin:", StringComparison.Ordinal) && long.TryParse(value.ParameterKey[8..], out var id) && id < 0))
                    throw new InvalidOperationException(rule.Name + ": 매개변수 식별자는 공유 GUID 또는 기본 매개변수 ID만 지원합니다. 모델별 id는 사용할 수 없습니다.");
            }
        }
    }

    public static RulePlan Plan(RuleSet file, IEnumerable<ParameterRow> available, IEnumerable<ValueEdit> pending, bool sample)
    {
        Validate(file);
        if (file.SampleOnly && !sample) throw new InvalidOperationException("연습용 룰셋은 실제 모델에 사용할 수 없습니다.");
        if (file.Version == 2) return KacRulePlanner.Plan(file, available, pending, sample);
        if (file.Version == 3) return SpaceRulePlanner.Plan(file, available, pending);
        var owners = available.GroupBy(r => (r.ModelPath, r.OwnerId, r.Scope)).Select(g => g.ToList()).ToList();
        var assignments = new Dictionary<string, (ParameterRow Row, string Value, string Rule)>();
        var matched = new HashSet<(string, string, string)>();
        var errors = new HashSet<string>();
        var plan = new RulePlan();
        foreach (var rule in file.Rules)
        {
            var targets = owners.Where(g => Matches(rule.Match, g[0])).ToList();
            if (targets.Count == 0) { plan.Notes.Add(rule.Name + ": 조건에 맞는 대상 없음"); continue; }
            foreach (var owner in targets)
            {
                var first = owner[0]; matched.Add((first.ModelPath, first.OwnerId, first.Scope));
                foreach (var value in rule.Set)
                {
                    var candidates = owner.Where(r => r.Name == value.Name && r.DataType == value.DataType && r.Storage == value.Storage &&
                        (value.ParameterKey == null || string.Equals(value.ParameterKey, r.ParameterKey, StringComparison.OrdinalIgnoreCase))).ToList();
                    var label = $"{rule.Name} / {first.ModelTitle} / {first.TypeName} / {value.Name}";
                    if (candidates.Count != 1) { errors.Add(label + $": 매개변수 {candidates.Count}개 — 유일하게 연결되지 않습니다."); continue; }
                    var row = candidates[0];
                    var error = EditPlanner.Validate(row, value.Value!);
                    if (error.Length > 0) { errors.Add(label + ": " + error); continue; }
                    if (assignments.TryGetValue(row.Key, out var previous) && previous.Value != value.Value)
                        errors.Add(label + ": 다른 규칙(" + previous.Rule + ")과 입력값이 충돌합니다.");
                    else assignments[row.Key] = (row, value.Value!, rule.Name);
                }
            }
        }
        foreach (var edit in pending)
            if (assignments.TryGetValue(edit.Original.Key, out var assigned) && (edit.Mode != "입력" || edit.Value != assigned.Value))
                errors.Add(edit.Model + " / " + edit.Field + ": 이미 준비한 변경과 룰셋 값이 다릅니다. 기존 변경을 제외한 후 다시 불러오세요.");
        if (errors.Count > 0) throw new InvalidOperationException("룰셋 전체를 보류했습니다. 기존 변경 예정 목록은 그대로입니다.\n" + string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? $"\n외 {errors.Count - 12}개 문제" : ""));
        foreach (var assignment in assignments.Values)
            foreach (var edit in EditPlanner.Stage([assignment.Row], assignment.Value))
            {
                edit.Reason = "룰셋: " + file.Name + " / " + assignment.Rule;
                plan.Edits.Add(edit);
            }
        plan.MatchedOwners = matched.Count;
        plan.Notes.Insert(0, $"조건 일치 {matched.Count:N0}개 대상 · 변경 {plan.Edits.Count:N0}개 값 · 동일값 {assignments.Count - plan.Edits.Count:N0}개 제외");
        return plan;
    }

    private static bool Matches(RuleMatch m, ParameterRow r) => m.Scope == r.Scope &&
        (m.Model == null || m.Model == r.ModelTitle) && (m.Category == null || m.Category == r.Category) && (m.Family == null || m.Family == r.Family) &&
        (m.Type == null || (m.TypeMatch switch { "시작" => r.TypeName.StartsWith(m.Type, StringComparison.Ordinal), "포함" => r.TypeName.Contains(m.Type, StringComparison.Ordinal), _ => r.TypeName == m.Type }));
}
