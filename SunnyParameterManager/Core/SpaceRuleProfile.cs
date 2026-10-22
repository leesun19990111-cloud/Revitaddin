using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

// SP 공간파일과 SA 건축파일의 프로젝트 정보만 지원한다. 업무 엑셀/확정표는 설치 파일과 분리한다.
public sealed class SpaceRuleProfile
{
    public string Scope { get; set; } = "프로젝트 정보";
    public string UnresolvedPolicy { get; set; } = "건축물번호유지-확인필요";
    // 명시한 룰셋만 누락값을 대체한다. 미등록/중복 건물과 비어 있지 않은 형식 오류는 제외한다.
    public string MissingBuildingNumberValue { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public List<SpaceBuilding> Buildings { get; set; } = [];
}

public sealed class SpaceBuilding
{
    public string Airport { get; set; } = "";
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string ManagementCode { get; set; } = "";
    public string Ownership { get; set; } = "";
    public int SourceRow { get; set; }
    // 사용자가 동일 건물로 확정한 파일명 속 건물명만 별도 연결한다.
    public List<string> NameAliases { get; set; } = [];
}

public static class SpaceRulePlanner
{
    private const string TextSpec = "autodesk.spec:spec.string-2.0.0";
    private static bool Format(string? value, string pattern) => value != null && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string Number(string value) => string.Join("-", value.Split('-').Select(s => s.TrimStart('0') is { Length: > 0 } n ? n : "0"));

    // 등록한 원본 경로를 사용한다. Revit이 임시 로컬에 붙인 제목으로 매칭하지 않는다.
    public static (string Airport, string Number)? FileKey(string path)
    {
        var identity = FileIdentity(path);
        return identity == null ? null : (identity.Value.Airport, identity.Value.Number);
    }

    private static string Alias(string name) => Regex.Replace(name, @"\s+", "").ToUpperInvariant();
    private static (string Airport, string Number, string Name)? FileIdentity(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096) return null;
        var filename = path.Replace('\\', '/').Split('/').Last();
        if (!filename.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(filename[..^4], @"\A(PUS|MWX|CJU)-([0-9]+(?:-[0-9]+)?)[_\-]?([^0-9_\-\s].*)-(?:SP|SA)\z",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return m.Success ? (m.Groups[1].Value.ToUpperInvariant(), Number(m.Groups[2].Value), m.Groups[3].Value) : null;
    }

    public static void Validate(RuleSet file)
    {
        var p = file.Space;
        if (file.Version != 3 || string.IsNullOrWhiteSpace(file.Name) || file.Kac != null || file.Rules == null || file.Rules.Count != 0 ||
            p == null || p.Scope != "프로젝트 정보" || p.UnresolvedPolicy != "건축물번호유지-확인필요" || p.MissingBuildingNumberValue is not ("" or "ND") || string.IsNullOrWhiteSpace(p.Source) ||
            !Format(p.SourceSha256, @"\A[0-9a-fA-F]{64}\z") || p.Buildings == null || p.Buildings.Count is < 1 or > 5000)
            throw new InvalidOperationException("버전3 공간파일 룰셋의 프로젝트 정보 범위·유지 정책·출처·건물 표를 확인하세요. 다른 규칙과 혼합할 수 없습니다.");
        var rows = new HashSet<int>();
        var aliases = new HashSet<(string, string)>();
        foreach (var b in p.Buildings)
        {
            if (b == null || b.Airport is not ("PUS" or "MWX" or "CJU") || !Format(b.Number, @"\A[0-9]{1,6}(?:-[0-9]{1,6})?\z") ||
                b.Name == null || b.ManagementCode == null || b.Ownership == null || b.ManagementCode.Length > 100 || b.Ownership.Length > 100 ||
                b.SourceRow < 1 || !rows.Add(b.SourceRow) || b.NameAliases == null || b.NameAliases.Count > 100)
                throw new InvalidOperationException("공간파일 건물 표의 공항·건물번호·원본 행을 확인하세요. 출처 행은 중복될 수 없습니다.");
            foreach (var name in b.NameAliases)
                if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || !aliases.Add((b.Airport, Alias(name))))
                    throw new InvalidOperationException("공간파일의 확정 건물명 별칭은 공항별로 비어 있거나 중복될 수 없습니다.");
        }
    }

    public static string? BuildingValue(SpaceBuilding b)
    {
        // 빈 칸을 위 행에서 상속하지 않는다. 0은 유효한 소유구분이며 글자는 숫자로 추측하지 않는다.
        if (!Format(b.ManagementCode, @"\A" + b.Airport + @"[0-9A-Z]+\z") || !Format(b.Ownership, @"\A[0-9]+\z")) return null;
        return b.ManagementCode[3..] + "-" + b.Ownership;
    }

    public static RulePlan Plan(RuleSet file, IEnumerable<ParameterRow> available, IEnumerable<ValueEdit> pending)
    {
        Validate(file);
        var profile = file.Space!;
        var table = profile.Buildings.GroupBy(b => (b.Airport, Number(b.Number))).ToDictionary(g => g.Key, g => g.ToList());
        var aliases = profile.Buildings.SelectMany(b => b.NameAliases.Select(n => (Key: (b.Airport, Alias(n)), Entry: b))).ToDictionary(a => a.Key, a => a.Entry);
        var plan = new RulePlan();
        var assignments = new Dictionary<string, (ParameterRow Row, string Value, string Reason)>();
        var errors = new List<string>();
        foreach (var model in available.GroupBy(r => r.ModelPath, StringComparer.OrdinalIgnoreCase))
        {
            var label = model.First().ModelTitle;
            var identity = FileIdentity(model.Key);
            var key = identity == null ? ((string Airport, string Number)?)null : (identity.Value.Airport, identity.Value.Number);
            if (key == null)
            {
                plan.Notes.Add(label + ": 제외 — 파일명이 공항코드-건물번호건물명-SP.rvt 또는 -SA.rvt 형식이 아닙니다. 파일명을 임의로 추정하지 않습니다.");
                continue;
            }
            var owners = model.Where(r => r.Scope == "프로젝트 정보").GroupBy(r => r.OwnerId).ToList();
            if (owners.Count != 1 || string.IsNullOrWhiteSpace(owners[0].Key))
            {
                errors.Add(label + ": 프로젝트 정보가 유일하게 조회되지 않았습니다. 새로 조회하세요.");
                continue;
            }
            plan.MatchedOwners++;
            var fields = owners[0].ToList();
            table.TryGetValue(key.Value, out var matches);
            var aliasMatched = aliases.TryGetValue((key.Value.Airport, Alias(identity!.Value.Name)), out var aliasEntry);
            var entry = aliasMatched ? aliasEntry : matches?.Count == 1 ? matches[0] : null;
            if (aliasMatched) plan.Notes.Add($"{label}: 확정 별칭 '{identity.Value.Name}' → {entry!.Name} ({entry.Airport}-{entry.Number}).");
            var building = entry == null ? null : BuildingValue(entry);
            var source = entry == null ? "파일명" : profile.Source + " / " + entry.SourceRow + "행";
            var missingReason = "";
            if (entry != null && profile.MissingBuildingNumberValue == "ND" &&
                (string.IsNullOrWhiteSpace(entry.ManagementCode) || string.IsNullOrWhiteSpace(entry.Ownership)))
            {
                missingReason = string.Join("·", new[] {
                    string.IsNullOrWhiteSpace(entry.ManagementCode) ? "건축물 관리코드" : "",
                    string.IsNullOrWhiteSpace(entry.Ownership) ? "소유구분" : "" }.Where(s => s.Length > 0)) + " 누락 → ND";
                building = "ND";
                plan.Notes.Add($"{label}: {missingReason}. 건축물번호의 기존 값도 ND로 교체합니다. ({source})");
            }
            if (building == null)
            {
                var why = entry != null ? "건축물 관리코드 또는 소유구분 숫자 누락/형식 확인" : matches == null ? "엑셀에 건물번호 없음" : "동일 건물번호가 여러 행에 존재";
                plan.Notes.Add($"{label}: 확인 필요 — {why}. 공항코드만 입력하고 건축물번호는 기존 값 유지. ({source})");
            }
            Assign("공항코드", key.Value.Airport);
            if (building != null) Assign("건축물번호", building);

            void Assign(string name, string value)
            {
                // 동명 다른 자료형/읽기전용 필드를 무시하고 엉뚱한 필드에 쓰지 않는다.
                var candidates = fields.Where(r => r.Name == name).ToList();
                var fieldLabel = label + " / " + name;
                if (candidates.Count != 1) { errors.Add(fieldLabel + $": 매개변수 {candidates.Count}개 — 유일하게 연결되지 않습니다."); return; }
                var row = candidates[0];
                if (row.Storage != "String" || row.DataType != TextSpec || row.Editor != "텍스트")
                { errors.Add(fieldLabel + ": 지원하는 텍스트 매개변수가 아닙니다."); return; }
                var error = EditPlanner.Validate(row, value);
                if (error.Length > 0) { errors.Add(fieldLabel + ": " + error); return; }
                assignments.Add(row.Key, (row, value, $"룰셋: {file.Name} / {source} / {key.Value.Airport}-{key.Value.Number}" +
                    (name == "건축물번호" && missingReason.Length > 0 ? " / " + missingReason : "")));
            }
        }
        foreach (var edit in pending)
            if (assignments.TryGetValue(edit.Original.Key, out var a) && (edit.Mode != "입력" || edit.Value != a.Value))
                errors.Add(edit.Model + " / " + edit.Field + ": 이미 준비한 변경과 룰셋 값이 다릅니다. 기존 변경을 제외한 후 다시 불러오세요.");
        if (errors.Count > 0)
            throw new InvalidOperationException("공간파일 룰셋 전체를 보류했습니다. 기존 변경 예정 목록은 그대로입니다.\n" + string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? $"\n외 {errors.Count - 12}개 문제" : ""));
        foreach (var a in assignments.Values)
            foreach (var edit in EditPlanner.Stage([a.Row], a.Value))
            { edit.Reason = a.Reason; plan.Edits.Add(edit); }
        plan.Notes.Insert(0, $"SP·SA 파일 {plan.MatchedOwners:N0}개 · 프로젝트 정보 변경 {plan.Edits.Count:N0}개 · 동일값 {assignments.Count - plan.Edits.Count:N0}개 제외");
        return plan;
    }
}
