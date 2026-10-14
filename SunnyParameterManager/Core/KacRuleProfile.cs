using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

// 엑셀을 분석해 확정한 표만 받는다. 룰셋에서 경로·코드·식은 실행하지 않는다.
public sealed class KacRuleProfile
{
    public string Scope { get; set; } = "유형";
    public string ManufacturerInfo { get; set; } = "";
    public string Company { get; set; } = "";
    public string MaterialPolicy { get; set; } = "";
    public string UnresolvedPolicy { get; set; } = "";
    public List<KacClassification> Classification { get; set; } = [];
    public List<KacIfcMapping> Ifc { get; set; } = [];
    public List<string> Sources { get; set; } = [];
    // 명시한 항목만 실제 소속 유형 기준으로 배치 요소에도 입력한다. 기본은 기존 유형 전용.
    public List<string> InstanceFields { get; set; } = [];
}
public sealed class KacClassification
{
    public string Code { get; set; } = "";
    public string Kbims { get; set; } = "";
    public string Procurement { get; set; } = "";
    public string KbimsSource { get; set; } = "";
    public string ProcurementSource { get; set; } = "";
}
public sealed class KacIfcMapping
{
    public string Prefix { get; set; } = "";
    public string Value { get; set; } = "";
    public string Source { get; set; } = "";
}

public static class KacRulePlanner
{
    // 설치된 Revit 2026 BuiltInParameter의 실제 값. 표시 언어·번역명에 의존하지 않는다.
    public const string IfcTypeKey = "builtin:-1019015";
    public const string IfcInstanceKey = "builtin:-1019014";
    private static readonly string[] Fields = ["다음으로 IFC로 내보내기", "제작정보", "KBIMS-부위코드", "조달청표준공사코드", "재료명", "업체명"];
    private static bool Format(string value, string pattern) => value != null && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string TypeCode(string name)
    {
        // 코드 다음의 다섯 번째 숫자까지 잘못 포함하는 부분일치를 금지한다.
        var match = Regex.Match(name ?? "", @"\AKAC-[AS]-([A-Z]{2}[0-9]{4})(?=\z|[_\-\s])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Groups[1].Value : "";
    }
    public static void Validate(RuleSet file)
    {
        var p = file.Kac;
        if (string.IsNullOrWhiteSpace(file.Name) || file.Rules == null || file.Rules.Count != 0 || p == null || p.Scope != "유형" ||
            p.MaterialPolicy != "외측-상단-천장하단-단일재료" || p.UnresolvedPolicy != "기존값유지-확인필요" ||
            string.IsNullOrWhiteSpace(p.ManufacturerInfo) || string.IsNullOrWhiteSpace(p.Company) || p.Sources == null ||
            p.Classification == null || p.Classification.Count is < 1 or > 5000 || p.Ifc == null || p.Ifc.Count is < 1 or > 200 ||
            p.InstanceFields == null || p.InstanceFields.Distinct().Count() != p.InstanceFields.Count ||
            p.InstanceFields.Any(n => n is not ("재료명" or "업체명" or "다음으로 IFC로 내보내기")))
            throw new InvalidOperationException("버전2 KAC 룰셋의 유형 범위·재료/미확인 정책·고정값·매핑표를 확인하세요. 일반 규칙과 혼합할 수 없습니다.");
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in p.Classification)
            if (row == null || !Format(row.Code, @"\A[A-Z]{2}[0-9]{4}\z") || !codes.Add(row.Code) || row.Kbims == null || row.Procurement == null ||
                row.KbimsSource == null || row.ProcurementSource == null || row.Kbims.Length > 100 || row.Procurement.Length > 100)
                throw new InvalidOperationException("분류 코드는 중복 없이 영문2자+숫자4자리여야 합니다. 중복 원본은 상위 행의 값을 확정해 넣으세요.");
        codes.Clear();
        foreach (var row in p.Ifc)
            if (row == null || !Format(row.Prefix, @"\A[A-Z]{2}[0-9]{0,4}\z") || !codes.Add(row.Prefix) ||
                !Format(row.Value, @"\AIfc[A-Za-z0-9]+\z") || string.IsNullOrWhiteSpace(row.Source))
                throw new InvalidOperationException("IFC 코드 접두어는 중복될 수 없으며, 하나의 Ifc 값과 출처가 필요합니다.");
    }

    public static string MaterialName(MaterialEvidence evidence) => evidence.Name.StartsWith("KAC-", StringComparison.Ordinal) ? evidence.Name[4..] : evidence.Name;

    public static void CheckMaterial(ValueEdit edit, MaterialEvidence? actual)
    {
        var expected = edit.MaterialSource;
        if (expected == null) return;
        if (edit.Mode != "입력" || expected.Fingerprint.Length == 0 || actual == null || actual.Reason.Length > 0 ||
            expected.Fingerprint != actual.Fingerprint || expected.Name != actual.Name || expected.Basis != actual.Basis ||
            MaterialName(actual) != edit.Value)
            throw new InvalidOperationException(edit.Type + ": 조회 후 외측 재료 또는 층 구성이 달라졌습니다. 다시 조회하고 룰셋을 불러오세요.");
    }

    public static RulePlan Plan(RuleSet file, IEnumerable<ParameterRow> available, IEnumerable<ValueEdit> pending, bool sample)
    {
        var profile = file.Kac!;
        var plan = new RulePlan();
        var classifications = profile.Classification.ToDictionary(x => x.Code);
        var ifcs = profile.Ifc.OrderByDescending(x => x.Prefix.Length).ToList();
        var pendingByKey = pending.ToDictionary(e => e.Original.Key);
        var notes = new List<string>();
        var all = available.ToList();
        var types = all.Where(r => r.Scope == "유형").GroupBy(r => (r.ModelPath.ToUpperInvariant(), r.OwnerId))
            .ToDictionary(g => g.Key, g => g.First());
        var changed = Fields.ToDictionary(n => n, _ => 0);
        var same = Fields.ToDictionary(n => n, _ => 0);
        var skipped = Fields.ToDictionary(n => n, _ => 0);
        int identical = 0, outside = 0, instances = 0;
        foreach (var owner in all.Where(r => r.Scope == "유형" || r.Scope == "인스턴스" && profile.InstanceFields.Count > 0)
                     .GroupBy(r => (r.ModelPath.ToUpperInvariant(), r.OwnerId)))
        {
            var rows = owner.ToList(); var first = rows[0];
            var instance = first.Scope == "인스턴스";
            var code = TypeCode(first.TypeName);
            if (code.Length == 0) { outside++; continue; }
            var source = first;
            var label = $"{first.ModelTitle} / {first.TypeName} / {first.Scope} {first.ElementId}";
            if (instance)
            {
                if (string.IsNullOrEmpty(first.TypeOwnerId) || rows.Any(r => r.TypeOwnerId != first.TypeOwnerId) ||
                    !types.TryGetValue((first.ModelPath.ToUpperInvariant(), first.TypeOwnerId), out source) ||
                    source.TypeName != first.TypeName || source.Family != first.Family || source.CategoryKey != first.CategoryKey)
                {
                    notes.Add($"{label}: 실제 소속 유형 식별 정보 없음/불일치 — 모델 다시 조회 필요. 이름만으로 연결하지 않습니다");
                    foreach (var name in profile.InstanceFields) skipped[name]++;
                    continue;
                }
                instances++;
            }
            else plan.MatchedOwners++;
            void Assign(string name, string? value, string basis, MaterialEvidence? material = null)
            {
                if (instance && !profile.InstanceFields.Contains(name)) return;
                void Skip(string why) { skipped[name]++; notes.Add($"{label} / {name}: 확인 필요 — {why}. 기존값 유지"); }
                // 이 항목이 인스턴스에만 바인딩된 유형에는 존재하지 않는 유형 필드를 요구하지 않는다.
                if (!instance && profile.InstanceFields.Contains(name) && name != Fields[0] && rows.All(r => r.Name != name)) return;
                if (string.IsNullOrWhiteSpace(value)) { Skip(basis); return; }
                // 이름이 같은 다른 저장형식도 모호한 대상으로 취급한다. 임의 첫 매개변수 선택 금지.
                var candidates = name == Fields[0]
                    ? rows.Where(r => r.ParameterKey == (instance ? IfcInstanceKey : IfcTypeKey)).ToList()
                    : rows.Where(r => r.Name == name).ToList();
                // 이전 룰셋의 명시적 사용자 정의 텍스트 필드도 보존하되 다른 기본 IFC 필드와 혼동하지 않는다.
                if (name == Fields[0] && candidates.Count == 0)
                    candidates = rows.Where(r => r.Name == name && !r.ParameterKey.StartsWith("builtin:", StringComparison.Ordinal)).ToList();
                if (candidates.Count != 1) { Skip($"{first.Scope} 매개변수 {candidates.Count}개"); return; }
                var row = candidates[0];
                if (row.Storage != "String" || row.Editor != "텍스트" ||
                    !(row.DataType == "autodesk.spec:spec.string-2.0.0" || row.DataType == "autodesk.spec.aec:multilineText-2.0.0" || sample && row.DataType == "text") || !row.Editable)
                { Skip(!row.Editable ? row.Status : "텍스트 자료형이 아님"); return; }
                if (pendingByKey.TryGetValue(row.Key, out var old) && (old.Mode != "입력" || old.Value != value))
                    throw new InvalidOperationException($"{label} / {name}: 기존 변경과 룰셋 값이 달라 전체 보류했습니다. 기존 변경을 제외한 뒤 다시 불러오세요.");
                var staged = EditPlanner.Stage([row], value);
                if (staged.Count == 0) { identical++; same[name]++; }
                foreach (var edit in staged)
                {
                    edit.MaterialSource = material;
                    edit.Reason = $"룰셋: {file.Name} / {code} / {basis}";
                    plan.Edits.Add(edit);
                    changed[name]++;
                }
            }
            var ifc = ifcs.FirstOrDefault(x => code.StartsWith(x.Prefix, StringComparison.Ordinal));
            Assign("다음으로 IFC로 내보내기", ifc?.Value, ifc?.Source ?? "IFC 매핑 없음");
            Assign("제작정보", profile.ManufacturerInfo, "고정값");
            var classification = classifications.GetValueOrDefault(code);
            Assign("KBIMS-부위코드", classification?.Kbims, classification?.KbimsSource is { Length: > 0 } ks ? ks : "해당 코드의 KBIMS 값 없음");
            Assign("조달청표준공사코드", classification?.Procurement, classification?.ProcurementSource is { Length: > 0 } ps ? ps : "해당 코드의 조달청 값 없음");
            var material = source.OuterMaterial;
            var materialOk = material != null && material.Reason.Length == 0 && material.Fingerprint.Length > 0;
            Assign("재료명", materialOk ? MaterialName(material!) : null,
                materialOk ? material!.Basis + " / " + material.Name : material?.Reason is { Length: > 0 } mr ? mr : "외측 재료 정보 없음 — 모델 다시 조회 필요", materialOk ? material : null);
            Assign("업체명", profile.Company, "고정값");
        }
        plan.Notes.Add($"KAC-A-/KAC-S- 유형 {plan.MatchedOwners:N0}개 · 배치 요소 {instances:N0}개 · 변경 {plan.Edits.Count:N0}개 · 동일값 {identical:N0}개 · 확인 필요 {notes.Count:N0}개 · 코드 형식 제외 {outside:N0}개");
        foreach (var name in Fields) plan.Notes.Add($"{name}: 변경 예정 {changed[name]:N0}개 · 이미 동일 {same[name]:N0}개 · 확인 필요 {skipped[name]:N0}개");
        plan.Notes.Add(profile.InstanceFields.Count == 0 ? "유형 매개변수만 변경합니다." : "배치 요소 추가 대상: " + string.Join(", ", profile.InstanceFields) + ". 실제 유형 식별자로 연결합니다.");
        plan.Notes.Add("프로젝트 정보·실제 재료/층 구성·매개변수 바인딩·IFC 내보내기 여부/미리 정의된 유형은 변경하지 않습니다. 확인 필요 항목은 기존값을 유지합니다.");
        plan.Notes.AddRange(notes);
        return plan;
    }
}
