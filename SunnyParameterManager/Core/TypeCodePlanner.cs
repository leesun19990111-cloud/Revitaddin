using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

public sealed class TypeCodeFinding
{
    public string Model { get; set; } = "";
    public string Category { get; set; } = "";
    public string Family { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string CurrentName { get; set; } = "";
    public string ProposedName { get; set; } = "";
    public string Status { get; set; } = "";
}

// 이름 맨 앞의 독립 코드만 보정한다. 예: KAC-A-WA000_벽 → KAC-A-WA0000_벽.
public static class TypeCodePlanner
{
    private static readonly Regex Code = new(@"\A(?:(?:KAC-[AS]-))?[A-Z]{2,3}[0-9]{3}(?=\z|[_-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryFix(string name, out string fixedName)
    {
        name ??= "";
        var match = Code.Match(name);
        fixedName = match.Success ? name.Insert(match.Length, "0") : name;
        return match.Success;
    }

    public static RulePlan Plan(IEnumerable<ParameterRow> available, IEnumerable<ValueEdit> pending)
    {
        var rows = available.ToList();
        var types = rows.Where(r => r.Scope == "유형" && r.ParameterKey.Length == 0)
            .DistinctBy(r => (r.ModelPath, r.OwnerId)).ToList();
        var names = rows.Where(r => r.Scope == "유형" && r.ParameterKey == CleanupPlanner.NameKey)
            .ToDictionary(r => (r.ModelPath, r.OwnerId));
        var oldEdits = pending.ToList();
        if (oldEdits.Count > 0 && oldEdits.Any(e => !e.KacCleanup))
            throw new InvalidOperationException("유형 코드 보정은 기존 수동/다른 룰셋 변경과 분리하세요. 기존 변경은 보존했습니다.");
        var plan = new RulePlan();
        foreach (var type in types)
        {
            if (!TryFix(type.TypeName, out var proposed)) continue;
            plan.MatchedOwners++;
            var finding = new TypeCodeFinding
            {
                Model = type.ModelTitle, Category = type.Category, Family = type.Family,
                ElementId = type.ElementId, CurrentName = type.TypeName, ProposedName = proposed
            };
            plan.TypeCodeFindings.Add(finding);
            if (!names.TryGetValue((type.ModelPath, type.OwnerId), out var row) || type.Cleanup == null)
                finding.Status = "확인 필요 · 유형 이름/변경 근거 없음";
            else if (!type.Cleanup.CanRename)
                finding.Status = "제외 · Revit에서 이름 변경 불가";
            else if (types.Any(t => t.ModelPath == type.ModelPath && t.OwnerId != type.OwnerId &&
                         t.CategoryKey == type.CategoryKey && t.Family == type.Family &&
                         string.Equals(t.TypeName, proposed, StringComparison.OrdinalIgnoreCase)))
                finding.Status = "제외 · 같은 패밀리에 변경 후 이름 존재";
            else
            {
                finding.Status = "변경 예정";
                plan.Edits.Add(new ValueEdit { Original = row, Value = proposed, KacCleanup = true,
                    Reason = "세 자리 유형 코드 끝에 0 추가" });
            }
        }
        foreach (var edit in oldEdits)
            if (!plan.Edits.Any(e => e.Original.Key == edit.Original.Key && e.Value == edit.Value))
                throw new InvalidOperationException("기존 유형 변경 목록과 현재 코드 보정 결과가 다릅니다. 기존 변경은 보존했습니다.");
        plan.Notes.Add($"등록 모델 전체 유형에서 세 자리 코드 {plan.MatchedOwners:N0}건 발견 · 변경 예정 {plan.Edits.Count:N0}건 · 제외 {plan.TypeCodeFindings.Count - plan.Edits.Count:N0}건.");
        plan.Notes.Add("유형 이름의 코드 뒤에 0 하나만 추가합니다. 이미 네 자리인 코드, 이름 중간의 숫자, 다른 접두어는 유지합니다.");
        return plan;
    }
}
