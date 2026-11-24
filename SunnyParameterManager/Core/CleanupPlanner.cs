using System.Globalization;
using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

public sealed class CleanupEvidence
{
    public string Version { get; set; } = "";
    public bool InPlace { get; set; }
    public bool CanRename { get; set; }
    public double? ThicknessMm { get; set; }
    public string ThicknessBasis { get; set; } = "";
    public string ThicknessIssue { get; set; } = "";
}

// 고정된 업무 정책이다. 외부 파일에서 코드/표현식을 실행하지 않는다.
public static class CleanupPlanner
{
    public const string NameKey = "sunny:type-name";
    public const string WorksetKey = "builtin:-1002053";
    public const string RoomKey = "builtin:-1001007";
    public const string MassCategory = "-2003400";
    public static readonly string[] Flags = ["건축", "구조", "IsExternal", "기계", "소방", "전기", "토목"];
    public static bool IsKac(string name) => name.StartsWith("KAC-A-", StringComparison.Ordinal) || name.StartsWith("KAC-S-", StringComparison.Ordinal);
    public static RuleSet Rules() => new() { Version = 4, Name = "KAC 유형·작업세트 정리 — 등록 모델 전체", Cleanup = true };
    public static string NormalizeName(string name, double? thickness, out string issue)
    {
        issue = "";
        // 괄호 안 설명은 문자 그대로 보존한다. 두께 규격이 아닌 H/W 등은 치환하지 않는다.
        var m = Regex.Match(name, @"\A(KAC-[AS]-[A-Z]{2,3}[0-9]{4}_[^_()\r\n]+?)[_-](?:THK|T)([0-9]+(?:\.[0-9]+)?)(\([^\r\n]*\))?\z", RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            issue = "자동 수정 가능한 단일 두께 규격이 아님 — 기존 이름 유지";
            return name;
        }
        if (thickness == null) issue = "총 두께 미확인 — 구분자/THK만 정리하고 기존 두께 수치 유지";
        var number = thickness is > 0 && double.IsFinite(thickness.Value) ? Math.Round(thickness.Value, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture) : m.Groups[2].Value;
        return m.Groups[1].Value + "-T" + number + m.Groups[3].Value;
    }
    public static RulePlan Plan(IEnumerable<ParameterRow> available, IEnumerable<ValueEdit> pending, IReadOnlyCollection<string>? preserveWorksets = null)
    {
        var groups = available.GroupBy(r => (r.ModelPath, r.OwnerId)).Select(g => g.ToList()).ToList();
        var plan = new RulePlan();
        int preservedWorksets = 0;
        var names = new List<(ParameterRow Row, string Value)>();
        var types = groups.Where(g => g[0].Scope == "유형").Select(g => g[0]).ToList();
        void Note(ParameterRow r, string field, string why) => plan.Notes.Add($"{r.ModelTitle} / {r.TypeName} / {r.Scope} / ID {r.ElementId} / {field}: {why}");
        void Add(ParameterRow r, string value, string reason, bool rename = false)
        {
            if (r.HasValue && r.Display == value) return;
            var error = rename ? "" : EditPlanner.Validate(r, value);
            if (error.Length > 0) { Note(r, r.Name, error); return; }
            plan.Edits.Add(new() { Original = r, Value = value, KacCleanup = true, ReferenceKey = r.Editor == "선택" ? r.Choices.Single(c => c.Name == value).Key : "", Reason = "KAC 정리 / " + reason });
        }
        foreach (var rows in groups)
        {
            var r = rows[0];
            bool kac = IsKac(r.TypeName), mass = r.CategoryKey == MassCategory;
            if (r.Scope is not ("유형" or "인스턴스") || !kac && !mass) continue;
            if (r.Cleanup == null) throw new InvalidOperationException("유형 정리 근거가 없습니다. 전용 룰셋을 먼저 불러온 뒤 RVT를 다시 조회하세요.");
            plan.MatchedOwners++;
            void Field(string label, Func<ParameterRow, bool> filter, string value)
            {
                var found = rows.Where(filter).ToList();
                if (found.Count != 1) { Note(r, label, found.Count == 0 ? "해당 범위에 적용 가능한 매개변수 없음 — 유지" : "동명 후보가 여러 개 — 유지"); return; }
                Add(found[0], value, label);
            }
            if (r.Scope == "인스턴스")
            {
                var workset = rows.Where(p => p.ParameterKey == WorksetKey).ToList();
                if (workset.Count == 1 && workset[0].HasValue && preserveWorksets?.Contains(workset[0].Display, StringComparer.Ordinal) == true)
                    preservedWorksets++;
                else Field("작업세트", p => p.ParameterKey == WorksetKey, mass ? "09_매스" : r.TypeName.StartsWith("KAC-A-") ? "01_건축" : "02_구조");
            }
            if (!kac) continue; // 매스 예외는 작업세트만 변경한다.
            if (r.Scope == "유형")
            {
                var n = rows.SingleOrDefault(p => p.ParameterKey == NameKey);
                if (n != null)
                {
                    var name = NormalizeName(r.TypeName, r.Cleanup.ThicknessMm, out var issue);
                    if (issue.Length > 0) Note(r, "유형명", issue + " / " + r.Cleanup.ThicknessIssue);
                    if (name != r.TypeName)
                    {
                        if (r.Cleanup.CanRename) names.Add((n, name));
                        else Note(r, "유형명", "Revit에서 이름 변경이 허용되지 않는 유형 — 유지");
                    }
                }
                else Note(r, "유형명", "유형 이름 판독 근거 없음");
            }
            foreach (var flag in Flags)
                Field(flag, p => p.Name == flag && p.IdentityDataParameter && !p.ParameterKey.StartsWith("builtin:") && p.Storage == "Integer" && p.Editor == "예/아니요",
                    flag == (r.TypeName.StartsWith("KAC-A-") ? "건축" : "구조") ? "예" : "아니요");
            Field("룸 경계", p => p.ParameterKey == RoomKey && p.Editor == "예/아니요", "아니요");
            if (r.Scope == "인스턴스" && r.Cleanup.InPlace)
            {
                var target = r.CategoryKey switch { "-2001330" => "IfcColumn", "-2000035" => "IfcRoof", "-2000032" => "IfcSlab", "-2000011" => "IfcWall", _ => "" };
                if (target.Length == 0) Note(r, "내부편집 IFC", "카테고리만으로 용도를 확정할 수 없어 기존값 유지");
                else Field("내부편집 IFC", p => p.ParameterKey == KacRulePlanner.IfcInstanceKey && p.Storage == "String", target);
            }
        }
        foreach (var (row, name) in names)
        {
            if (types.Any(t => t.ModelPath == row.ModelPath && t.CategoryKey == row.CategoryKey && t.OwnerId != row.OwnerId && string.Equals(t.TypeName, name, StringComparison.OrdinalIgnoreCase)) ||
                names.Count(n => n.Row.ModelPath == row.ModelPath && n.Row.CategoryKey == row.CategoryKey && string.Equals(n.Value, name, StringComparison.OrdinalIgnoreCase)) > 1)
                Note(row, "유형명", "동일 카테고리에 기존/변경 예정 이름 중복 — 유지: " + name);
            else Add(row, name, "구분자·THK·실제 총 두께 / " + row.Cleanup?.ThicknessBasis, true);
        }
        var previous = pending.ToList();
        if (previous.Any(e => !e.KacCleanup) && plan.Edits.Count > 0) throw new InvalidOperationException("유형 정리는 다른 수동/룰셋 변경과 별도로 적용하세요. 기존 변경은 보존했습니다.");
        foreach (var e in plan.Edits)
            if (previous.Any(p => p.Original.Key == e.Original.Key && p.Value != e.Value)) throw new InvalidOperationException("기존 변경과 유형 정리 결과가 충돌합니다. 기존 변경은 보존했습니다.");
        plan.Notes.Insert(0, $"등록 모델 전체: KAC 유형·요소 및 매스 {plan.MatchedOwners:N0}개 검토 / 변경 {plan.Edits.Count:N0}건 / 확인·유지 안내 {plan.Notes.Count:N0}건. 유형명 변경은 같은 유형의 모든 배치에 영향을 줍니다. 룸 경계 해제는 방·면적에 영향을 줄 수 있습니다. 없는 작업세트/매개변수는 생성하지 않습니다.");
        plan.Notes.Insert(1, "변경 항목별: " + string.Join(" / ", plan.Edits.GroupBy(e => e.Field).Select(g => g.Key + " " + g.Count().ToString("N0") + "건")));
        if (preserveWorksets?.Count > 0) plan.Notes.Insert(2, $"현재 00_외장 작업세트 유지: {preservedWorksets:N0}개 요소. 작업세트만 변경 대상에서 제외합니다.");
        return plan;
    }
}
