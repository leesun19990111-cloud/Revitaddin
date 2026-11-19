using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

// 개인 사전은 룰셋 데이터에서만 읽는다. 룸마다 사전을 다시 펼치지 않고 한 검토에서 재사용한다.
public sealed class RoomProcedureClassifier
{
    private readonly Dictionary<string, List<RoomUseTerm>> _names;
    private readonly Dictionary<string, List<RoomUseTerm>> _cases;
    private readonly Dictionary<string, RoomUseSemanticTerm> _aliases;
    private readonly Dictionary<string, RoomUseTerm> _byCode;
    private readonly Dictionary<string, string[]> _codeCandidates;
    private readonly List<(string Text, List<RoomUseTerm> Terms)> _tails;
    private static readonly string[] PublicWords = ["홀", "복도", "로비", "계단", "대합실", "통로", "LOBBY", "HALL", "CORRIDOR"];
    private static readonly string[] TransitPrefixes = ["출발", "도착", "체크인", "입국", "출국", "환승"];

    public RoomProcedureClassifier(RoomUseProfile profile)
    {
        Dictionary<string, List<RoomUseTerm>> Index(Func<RoomUseTerm, IEnumerable<string>> words) =>
            profile.Terms.SelectMany(t => words(t).Select(w => (Text: Normalize(w), Term: t)))
                .Where(x => x.Text.Length > 0).GroupBy(x => x.Text)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Term).DistinctBy(t => t.Code).ToList());
        _names = Index(t => t.NameMatches ?? [t.Name]);
        _cases = Index(t => t.Cases);
        _aliases = profile.UseAliases.ToDictionary(t => Normalize(t.Phrase));
        _byCode = profile.Terms.ToDictionary(t => t.Code);
        _codeCandidates = profile.EstimateCodeCandidates.ToDictionary(x => Normalize(x.Key), x => x.Value);
        _tails = _names.Keys.Concat(_cases.Keys).Distinct().Select(word =>
            (Text: word, Terms: _names.GetValueOrDefault(word, []).Concat(_cases.GetValueOrDefault(word, []))
                .DistinctBy(t => t.Code).ToList())).OrderByDescending(x => x.Text.Length).ToList();
    }

    public static string Normalize(string value)
    {
        var text = Regex.Replace(value ?? "", @"\s+", "").ToUpperInvariant();
        while (true)
        {
            var next = Regex.Replace(text, @"(?:[-#]?\d+|_(?:남|여)|\((?:남|여|남,여|장애인\(남\)|장애인\(여\)|가족|예비|도착|출발|국제선|국내선)\))$", "");
            if (next == text) return text;
            text = next;
        }
    }

    private static bool Invalid(string name)
    {
        if (name.Length == 0 || name.Contains('?')) return true;
        int depth = 0;
        foreach (var ch in name)
        {
            if (ch == '(') depth++;
            if (ch == ')' && --depth < 0) return true;
        }
        return depth != 0;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        var text = Normalize(name);
        var inner = Regex.Match(text, @"^[^()]+\((.+)\)$");
        if (inner.Success)
        {
            var candidate = Normalize(inner.Groups[1].Value);
            if (candidate.Length > 0) yield return candidate;
        }
        yield return text;
    }

    private static bool Compound(string text) =>
        (text.Contains("및") || text.Contains('/')) && !Regex.IsMatch(text, @"^[A-Z]+(?:/[A-Z]+)+$");

    private static bool Ambiguous(string text) => text.Contains("공실") || text.Contains("확인필요") || text.Contains("편의시설");

    private static (string Value, string Basis)? Choose(List<RoomUseTerm>? matches, bool code, string basis)
    {
        if (matches == null || matches.Count == 0) return null;
        var candidates = matches;
        if (code && matches.Select(t => t.Code).Distinct().Count() > 1)
        {
            var common = matches.Where(t => t.Code.StartsWith("33", StringComparison.Ordinal)).ToList();
            if (common.Count > 0) candidates = common;
        }
        var values = candidates.Select(t => code ? t.Code : t.Use).Distinct().ToList();
        return values.Count == 1 ? (values[0], basis + ": " + candidates[0].Name + " (" + candidates[0].Code + ")") : null;
    }

    private (string Value, string Basis)? Exact(string text, bool code) =>
        Choose(_names.GetValueOrDefault(text), code, "세분류명 완전 일치") ??
        Choose(_cases.GetValueOrDefault(text), code, "사례 완전 일치");

    private static string? Acronym(string text, bool code) => text.Replace(".", "") switch
    {
        "ELEV" => code ? "34403" : "E",
        "ESC" => code ? "34404" : "E",
        "AD" or "PD" or "OA" or "EA" => code ? "33430" : "U",
        "EPS" => code ? "33432" : "U",
        "PS" => code ? "33436" : "U",
        "UPS" => code ? "33435" : "U",
        "TPS" => code ? "33434" : "U",
        "DA" => code ? "38301" : "U",
        "NAF" or "NAF실" or "NAF(소화가스)" => code ? null : "U",
        _ => null
    };

    private (string Value, string Basis)? Tail(string text, bool code)
    {
        var matches = _tails.Where(t => t.Text.Length > 0 && text.EndsWith(t.Text, StringComparison.Ordinal) &&
            (text.Length == t.Text.Length || !t.Text.All(c => c is >= 'A' and <= 'Z') || text[^(t.Text.Length + 1)] is not (>= 'A' and <= 'Z'))).ToList();
        if (matches.Count == 0) return null;
        var longest = matches.TakeWhile(t => t.Text.Length == matches[0].Text.Length)
            .SelectMany(t => t.Terms).DistinctBy(t => t.Code).ToList();
        return Choose(longest, code, "사전 끝말 일치");
    }

    private (string Value, string Basis)? Match(string text, bool code)
    {
        if (text.Contains("PIT", StringComparison.Ordinal)) return (code ? "33117" : "U", "PIT 우선 판정");
        if (Compound(text))
            return !code && Regex.IsMatch(text, @"(?<![A-Z])(?:EPS|TPS|UPS|PS|AD|PD)(?![A-Z])")
                ? ("U", "복합 실명에 설비 약어 포함") : null;
        if (Ambiguous(text)) return null;
        if (code && (text is "OPEN" or "ND" or "하론가스실" or "건강관리실" ||
            text.EndsWith("로비", StringComparison.Ordinal) || text.StartsWith("LOBBY", StringComparison.Ordinal))) return null;
        var exact = Exact(text, code);
        if (exact != null) return exact;
        if (!code && _aliases.TryGetValue(text, out var alias)) return (alias.Use, alias.Basis);
        var acronym = Acronym(text, code);
        if (acronym != null) return (acronym, "단독 약어: " + text);
        if (code && (text.EndsWith("통로", StringComparison.Ordinal) ||
            TransitPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)) &&
            new[] { "홀", "복도", "로비" }.Any(s => text.EndsWith(s, StringComparison.Ordinal)))) return null;
        return Tail(text, code);
    }

    public (string Use, string Basis) Use(string name)
    {
        var decision = UseDecision(name);
        return (decision.Use, decision.Basis);
    }

    internal (string Use, string Basis, bool Estimated) UseDecision(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ("", "실명 공란 — 기존 용도 유지", false);
        var text = Normalize(name);
        if (text == "ND") return ("Z", "실명 ND", false);
        if (text == "OPEN") return ("C", "실명 OPEN", false);
        if (!Invalid(text))
            foreach (var candidate in Candidates(name))
            {
                var match = Match(candidate, false);
                if (match is { } found) return (found.Value, found.Basis, false);
            }
        if (PublicWords.Any(w => text.Contains(w, StringComparison.Ordinal))) return ("C", "공용구역 포함어", true);
        if (Regex.IsMatch(text, @"^[A-Z]{1,4}(?:/[A-Z]{1,4})*$")) return ("U", "4자 이하 영문 약어", true);
        return ("R", "그 외 기본값", true);
    }

    internal (RoomUseTerm? Term, string Basis, bool Specific) EstimateCode(string name, string use)
    {
        var text = Normalize(name);
        if (text == "ND" || !text.Any(char.IsLetter)) return (null, "공간을 특정할 실명 없음", false);
        foreach (var candidate in Candidates(name))
        {
            if (!_codeCandidates.TryGetValue(candidate, out var codes)) continue;
            var options = codes.Select(code => _byCode[code]).ToList();
            // 용도와 일치하는 코드, 시설물 공통 코드, 사전 후보 순서로 선택한다.
            var chosen = options.OrderByDescending(t => t.Use == use)
                .ThenByDescending(t => t.Code.StartsWith("33", StringComparison.Ordinal)).First();
            return (chosen, "원문 충돌 후보 " + string.Join(" / ", options.Select(t => $"{t.Code} {t.Name}")), true);
        }
        var acronyms = Regex.Matches(text, @"(?<![A-Z])(?:EPS|TPS|UPS|PS|AD|PD|OA|EA|NAF)(?![A-Z])")
            .Select(m => m.Value == "NAF" ? "33414" : Acronym(m.Value, true)!).Distinct().ToList();
        if (acronyms.Count > 0 && _byCode.TryGetValue(acronyms.Count == 1 ? acronyms[0] : "33499", out var utility))
            return (utility, "실명에 설비 약어 포함", true);
        // 괄호·물음표·복합어 때문에 확정하지 못한 경우에도 뚜렷한 공간명 단서를 검토한다.
        var hints = _names.Where(n => n.Key.Length >= 2 && n.Key.Any(c => c > 127) && text.Contains(n.Key, StringComparison.Ordinal))
            .OrderByDescending(n => n.Key.Length).ToList();
        if (hints.Count > 0)
        {
            var longest = hints.TakeWhile(n => n.Key.Length == hints[0].Key.Length).ToList();
            var options = longest.SelectMany(n => n.Value).DistinctBy(t => t.Code).ToList();
            if (options.Count == 1) return (options[0], "실명에 포함된 세분류명 " + longest[0].Key, true);
        }
        var fallback = use switch { "C" => "33199", "U" => "33499", "R" => "33699", _ => "" };
        return (_byCode.GetValueOrDefault(fallback), $"구체적인 분류 단서가 없어 용도 {use}를 기준으로 기타 공간 선택", false);
    }

    public (string Code, string Basis) Code(string name)
    {
        var text = Normalize(name);
        if (Invalid(text)) return ("", "실명 공란·미확인 또는 괄호 오류 — 기존 공간분류코드 유지");
        foreach (var candidate in Candidates(name))
        {
            var match = Match(candidate, true);
            if (match != null) return match.Value;
        }
        return ("", "공간분류코드 미확정 — 기존 값 유지");
    }
}
