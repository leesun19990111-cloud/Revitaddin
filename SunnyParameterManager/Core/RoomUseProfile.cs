using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

// 사용자 작업절차의 실 종류 사전을 데이터로 보관한다. RVT 적용 시 외부 파일이나 스크립트를 실행하지 않는다.
public sealed class RoomUseProfile
{
    public string Source { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public List<RoomUseTerm> Terms { get; set; } = [];
    public List<RoomUseSemanticTerm> SemanticTerms { get; set; } = [];
    public List<string> AuditFields { get; set; } = [];
}

public sealed class RoomUseTerm
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Use { get; set; } = "";
    public List<string> Cases { get; set; } = [];
}

public sealed class RoomUseSemanticTerm
{
    public string Phrase { get; set; } = "";
    public string Use { get; set; } = "";
    public string Basis { get; set; } = "";
}

public static class RoomUsePlanner
{
    public const string RoomCategory = "-2000160";
    public static readonly string[] RequiredAuditFields = ["실명", "번호", "기존 실번호", "지상지하", "층"];
    private const string TextSpec = "autodesk.spec:spec.string-2.0.0";
    private static readonly string[] Choices = ["C", "E", "R", "U", "Z"];
    private static readonly string[] HumanRooms = ["감시실", "무균실", "무반향실", "장비실", "제어실"];
    private static readonly string[] PublicRooms = ["화장실", "휴게실", "대합실", "여객통로"];
    private static readonly string[] PublicFallback = ["홀", "복도", "로비", "계단", "대합실"];
    private static readonly string[] TransitPrefixes = ["출발", "도착", "체크인", "입국", "출국", "환승"];

    public static void Validate(RuleSet file)
    {
        var p = file.RoomUse;
        if (file.Version != 11 || string.IsNullOrWhiteSpace(file.Name) || file.SampleOnly || file.Rules?.Count != 0 ||
            file.Kac != null || file.Space != null || file.Spec != null || file.Cleanup || file.MaterialAudit || file.TypeCodeFix ||
            file.ProjectBasePointAudit || file.RebuildCentral || file.ProjectParameterTransfer || file.RoomReview || file.PreserveWorksets?.Count != 0 ||
            p == null || string.IsNullOrWhiteSpace(p.Source) || !Regex.IsMatch(p.SourceSha256 ?? "", "^[0-9A-Fa-f]{64}$") ||
            p.Terms == null || p.Terms.Count is < 1 or > 400 || p.SemanticTerms == null || p.SemanticTerms.Count > 100 ||
            p.AuditFields == null || !p.AuditFields.SequenceEqual(RequiredAuditFields))
            throw new InvalidOperationException("버전11 Room 용도 룰셋의 출처와 실 종류 사전을 확인하세요. 다른 규칙과 혼합할 수 없습니다.");
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in p.Terms)
            if (term == null || string.IsNullOrWhiteSpace(term.Name) || term.Name.Length > 100 ||
                !Regex.IsMatch(term.Code ?? "", "^[0-9]{5}$") || !codes.Add(term.Code!) ||
                !Choices.Contains(term.Use) || term.Cases == null || term.Cases.Count > 100 ||
                term.Cases.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 100))
                throw new InvalidOperationException("Room 용도 사전의 세분류 코드·이름·용도·사례를 확인하세요.");
        var phrases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in p.SemanticTerms)
            if (term == null || string.IsNullOrWhiteSpace(term.Phrase) || term.Phrase.Length is < 2 or > 30 ||
                !phrases.Add(Clean(term.Phrase)) || !Choices.Contains(term.Use) ||
                string.IsNullOrWhiteSpace(term.Basis) || term.Basis.Length > 120)
                throw new InvalidOperationException("Room 의미 단서의 문구·용도·판정 근거를 확인하세요.");
    }

    private static string Clean(string value)
    {
        var text = Regex.Replace(value ?? "", @"[\s\.]+", "").ToUpperInvariant();
        for (var i = 0; i < 4; i++)
        {
            var before = text;
            text = Regex.Replace(text, @"(?:[-#]?\d+|_(?:남|여)|\((?:남|여|남,여|장애인\(남\)|장애인\(여\)|가족|예비|도착|출발|국제선|국내선)\))$", "");
            if (text == before) break;
        }
        return text;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        var clean = Clean(name);
        var inner = Regex.Match(clean, @"^[^()]+\((.+)\)$");
        if (inner.Success && inner.Groups[1].Value.Length > 0) yield return Clean(inner.Groups[1].Value);
        yield return clean;
    }

    private static bool Invalid(string text) => text.Length == 0 || text.Contains('?') ||
        text.Count(c => c == '(') != text.Count(c => c == ')') ||
        text.Contains("공실") || text.Contains("확인필요") || text.Contains("편의시설") ||
        (text.Contains("및") || text.Contains('/')) && !Regex.IsMatch(text, @"^[A-Z]+(?:/[A-Z]+)+$");

    private static string? Acronym(string text) => text switch
    {
        "ELEV" or "ESC" => "E",
        "AD" or "PD" or "OA" or "EA" or "EPS" or "PS" or "UPS" or "TPS" => "U",
        "DA" => "Z",
        _ => null
    };

    private static bool Suffix(string text, string term)
    {
        if (term.Length < 2 || !text.EndsWith(term, StringComparison.Ordinal)) return false;
        var prefix = text[..^term.Length];
        // CPS를 PS로, 영문 단어의 끝부분을 별도 약어로 판정하지 않는다.
        return prefix.Length == 0 || !(term.All(c => c is >= 'A' and <= 'Z') && prefix[^1] is >= 'A' and <= 'Z');
    }

    private static (string Use, string Basis)? DictionaryMatch(string text, RoomUseProfile profile)
    {
        if (Invalid(text)) return null;
        // 세분류명과 사례가 같은 단어를 서로 다르게 분류하면 세분류명을 우선한다.
        var names = profile.Terms.Where(t => Clean(t.Name) == text).ToList();
        if (names.Count > 0 && names.Select(t => t.Use).Distinct().Count() == 1)
            return (names[0].Use, $"세분류명 완전 일치: {names[0].Name} ({names[0].Code})");
        var terms = profile.Terms.SelectMany(t => new[] { t.Name }.Concat(t.Cases)
            .Select(s => (Text: Clean(s), Term: t))).Where(x => x.Text.Length > 0).ToList();
        var exact = terms.Where(x => x.Text == text).ToList();
        if (exact.Count > 0 && exact.Select(x => x.Term.Use).Distinct().Count() == 1)
            return (exact[0].Term.Use, $"사전 완전 일치: {exact[0].Term.Name} ({exact[0].Term.Code})");
        var tail = terms.Where(x => Suffix(text, x.Text) &&
                !(TransitPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)) && PublicFallback.Any(p => x.Text.EndsWith(p, StringComparison.Ordinal))))
            .OrderByDescending(x => x.Text.Length).ToList();
        if (tail.Count == 0) return null;
        var longest = tail.Where(x => x.Text.Length == tail[0].Text.Length).ToList();
        return longest.Select(x => x.Term.Use).Distinct().Count() == 1
            ? (longest[0].Term.Use, $"사전 끝말 일치: {longest[0].Term.Name} ({longest[0].Term.Code})") : null;
    }

    public static (string Use, string Basis) Classify(string roomName, RoomUseProfile profile)
    {
        roomName ??= "";
        var name = Clean(roomName);
        var original = Regex.Replace(roomName ?? "", @"\s+", "").ToUpperInvariant();
        if (original == "ND") return ("R", "실명 ND");
        if (original == "OPEN") return ("C", "실명 OPEN");
        foreach (var candidate in Candidates(roomName!))
        {
            if (HumanRooms.Any(s => candidate.EndsWith(s, StringComparison.Ordinal))) return ("R", "상주·작업 실 예외: " + candidate);
            if (candidate.Contains("PIT", StringComparison.Ordinal)) return ("U", "PIT 우선 판정");
            if (PublicRooms.Any(s => candidate.EndsWith(s, StringComparison.Ordinal))) return ("C", "공용구역 끝말");
            var dictionary = DictionaryMatch(candidate, profile);
            if (dictionary != null) return dictionary.Value;
            var acronym = Acronym(candidate);
            if (acronym != null) return (acronym, "단독 약어: " + candidate);
        }
        if (Regex.IsMatch(name, @"^[A-Z]+(?:/[A-Z]+)*$")) return ("U", "판별되지 않은 영문 약어");
        if (PublicFallback.Any(s => name.Contains(s, StringComparison.Ordinal))) return ("C", "공용구역 포함어");
        var semantic = profile.SemanticTerms.Where(t => name.Contains(Clean(t.Phrase), StringComparison.Ordinal)).ToList();
        if (semantic.Count > 0 && semantic.Select(t => t.Use).Distinct().Count() == 1)
            return (semantic[0].Use, "공간 의미 단서: " + string.Join("·", semantic.Select(t => t.Phrase).Distinct()) + " / " + semantic[0].Basis);
        return ("R", "그 외 기본값");
    }

    public static string UnusableNameReason(ParameterRow? source)
    {
        if (source == null) return "실명 매개변수 없음";
        if (!source.HasValue || string.IsNullOrWhiteSpace(source.Raw)) return "실명 빈값";
        if (string.Equals(source.Raw.Trim(), "ND", StringComparison.OrdinalIgnoreCase)) return "실명 ND";
        return "";
    }

    public static RulePlan Plan(RuleSet file, IEnumerable<ParameterRow> available, IEnumerable<ValueEdit> pending)
    {
        Validate(file);
        var pendingList = pending.ToList();
        if (pendingList.Any(e => e.Original.CategoryKey != RoomCategory || e.Original.Name != "용도" || e.Original.Scope != "인스턴스"))
            throw new InvalidOperationException("Room 용도 룰셋은 다른 변경 예정과 함께 적용하지 않습니다. 기존 변경을 먼저 적용·제외하거나 작업 파일로 보관하세요.");
        var plan = new RulePlan();
        var errors = new List<string>();
        var assignments = new Dictionary<string, (ParameterRow Row, string Value, string Reason)>();
        var skippedNames = 0;
        var owners = available.Where(r => r.Scope == "인스턴스" && r.CategoryKey == RoomCategory)
            .GroupBy(r => (r.ModelPath, r.OwnerId)).ToList();
        foreach (var owner in owners)
        {
            var rows = owner.ToList();
            var first = rows[0];
            var label = $"{first.ModelTitle} / Room {first.ElementId}";
            var nameRows = rows.Where(r => r.Name == "실명").ToList();
            var useRows = rows.Where(r => r.Name == "용도").ToList();
            if (nameRows.Count > 1)
            { errors.Add($"{label}: 실명 매개변수가 {nameRows.Count}개라 판정할 수 없습니다."); continue; }
            var source = nameRows.SingleOrDefault();
            if (source != null && (source.Storage != "String" || source.DataType != TextSpec))
            { errors.Add(label + ": 실명은 텍스트 매개변수여야 합니다."); continue; }
            var unavailable = UnusableNameReason(source);
            if (unavailable.Length > 0)
            {
                skippedNames++;
                plan.Notes.Add($"{label} · {unavailable} — 용도 변경 제외 · 공간정보 점검 엑셀에서 확인");
                continue;
            }
            if (useRows.Count != 1)
            { errors.Add($"{label}: 용도 매개변수가 {useRows.Count}개라 입력할 수 없습니다."); continue; }
            var target = useRows[0];
            if (target.Storage != "String" || target.DataType != TextSpec)
            { errors.Add(label + ": 용도는 텍스트 매개변수여야 합니다."); continue; }
            var name = source!.Raw.Trim();
            var (value, basis) = Classify(name, file.RoomUse!);
            var validation = EditPlanner.Validate(target, value);
            if (validation.Length > 0) { errors.Add(label + ": 용도 변경 불가 — " + validation); continue; }
            target.ElementName = name.Length == 0 ? "(실명 없음)" : name;
            var reason = $"실명 '{target.ElementName}' / {basis}";
            assignments.Add(target.Key, (target, value, reason));
            plan.Notes.Add($"{label} · 실명 {target.ElementName} · 기존 {target.ValueLabel} → 제안 {value} · {basis}");
            plan.MatchedOwners++;
        }
        foreach (var edit in pendingList)
            if (!assignments.TryGetValue(edit.Original.Key, out var a))
                errors.Add(edit.Model + " / 용도: 이번 룰셋에서 제외한 Room의 이전 변경 예정이 남아 있습니다. 해당 변경을 제외하고 다시 검토하세요.");
            else if (edit.Mode != "입력" || edit.Value != a.Value)
                errors.Add(edit.Model + " / 용도: 기존 변경 예정과 룰셋 값이 다릅니다. 기존 변경을 제외하고 다시 검토하세요.");
        if (errors.Count > 0)
            throw new InvalidOperationException("Room 용도 룰셋 전체를 보류했습니다. 기존 변경은 유지합니다.\n" + string.Join("\n", errors.Take(12)) +
                (errors.Count > 12 ? $"\n외 {errors.Count - 12}개 문제" : ""));
        foreach (var a in assignments.Values)
            foreach (var edit in EditPlanner.Stage([a.Row], a.Value))
            { edit.Reason = "룰셋: " + file.Name + " / " + a.Reason; plan.Edits.Add(edit); }
        plan.Notes.Insert(0, $"용도 판정 Room {plan.MatchedOwners:N0}개 · 실명 없음/ND 제외 {skippedNames:N0}개 · 변경 {plan.Edits.Count:N0}개 · 기존과 같음 {assignments.Count - plan.Edits.Count:N0}개. 제외 Room은 공간정보 점검 엑셀에서 확인하세요.");
        return plan;
    }
}
