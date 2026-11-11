using System.Text.Json;
using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;

internal static class RoomUseTests
{
    private const string Text = "autodesk.spec:spec.string-2.0.0";
    private static RuleSet Rules() => new()
    {
        Version = 11, Name = "Room 용도", RoomUse = new()
        {
            Source = "작업절차", SourceSha256 = new string('a', 64),
            SemanticTerms =
            [
                new() { Phrase = "입출하대", Use = "R", Basis = "하역장" },
                new() { Phrase = "캐노피", Use = "Z", Basis = "외부공간" },
                new() { Phrase = "관제탑", Use = "R", Basis = "근무 공간" },
                new() { Phrase = "초소", Use = "R", Basis = "근무 공간" }
            ],
            Terms =
            [
                new() { Code = "33227", Name = "창고", Use = "R" },
                new() { Code = "33234", Name = "화장실", Use = "C" },
                new() { Code = "33401", Name = "감시실", Use = "R" },
                new() { Code = "33405", Name = "기계실", Use = "U" },
                new() { Code = "33409", Name = "방풍실", Use = "U" },
                new() { Code = "34101", Name = "현관", Use = "C", Cases = ["방풍실"] },
                new() { Code = "34201", Name = "홀", Use = "C" },
                new() { Code = "34403", Name = "엘리베이터샤프트", Use = "E" },
                new() { Code = "38389", Name = "캐노피", Use = "Z" }
            ]
        }
    };

    private static ParameterRow Row(string owner, string field, string value) => new()
    {
        ModelPath = "model.rvt", ModelTitle = "model", OwnerId = owner, ElementId = owner,
        Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory, Category = "룸",
        ParameterKey = "guid:" + field, Name = field, DataType = Text, Storage = "String", Editor = "텍스트",
        HasValue = value.Length > 0, Raw = value, Display = value
    };

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var rules = Rules();
        check(RulePlanner.Parse(JsonSerializer.Serialize(rules)).Version == 11, "Room 용도 룰셋 파싱");
        var query = QueryPlan.FromRules(rules);
        check(query.IncludeRoomUse && query.Scopes.SequenceEqual(["인스턴스"]) &&
            query.Names.SequenceEqual(["실명", "용도"]) && query.RoomIdentityVersion == 0,
            "Room 용도 전용 실명·용도 조회");
        var legacy = Rules();
        legacy.RoomUse!.AuditFields = ["실명", "번호", "기존 실번호", "지상지하", "층"];
        check(RulePlanner.Parse(JsonSerializer.Serialize(legacy)).Version == 11,
            "이전 용도 룰셋도 읽되 공간정보 보고서는 만들지 않음");
        check(new QueryPlan { Scopes = ["인스턴스"], Names = ["실명", "용도"], IncludeRoomUse = true }.Covers(query),
            "용도 변경은 실명·용도만 있으면 충분");
        foreach (var (name, expected) in new[]
        {
            ("ND", "Z"), (" nd ", "Z"), ("", ""), ("  ", ""),
            ("OPEN", "C"), ("중앙감시실", "R"), ("ELEV.PIT", "U"),
            ("법무부(송환자 화장실)", "C"), ("창고-2", "R"), ("ELEV홀", "C"),
            ("ELEV기계실", "U"), ("CPS", "U"), ("AL/CPS", "U"),
            ("캐노피", "Z"), ("관세청(창고)", "R"), ("방풍실", "U"), ("대합실", "C"),
            ("입출하대", "R"), ("관제탑", "R"), ("초소", "R"), ("캐노피정면", "Z"),
            ("관제탑기계실", "U"), ("초소화장실", "C"), ("기게실", "R"), ("ELEV.", "E")
        }) check(RoomUsePlanner.Classify(name, rules.RoomUse!).Use == expected, "Room 실명 판정: " + name);
        check(RoomUsePlanner.Classify("입출하대", rules.RoomUse!).Basis.Contains("하역장") &&
            RoomUsePlanner.Classify("초소", rules.RoomUse!).Basis.Contains("근무 공간") &&
            !RoomUsePlanner.Classify("기게실", rules.RoomUse!).Basis.Contains("유사"), "공간 의미 근거 사용·오탈자 추정 없음");
        var rows = new[] { Row("1", "실명", "ELEV기계실"), Row("1", "용도", "R"),
            Row("2", "실명", "법무부(화장실)"), Row("2", "용도", "C") };
        var plan = RoomUsePlanner.Plan(rules, rows, []);
        check(plan.MatchedOwners == 2 && plan.Edits.Count == 1 && plan.Edits[0].Value == "U" &&
            plan.Edits[0].Type.Contains("ELEV기계실") && plan.Edits[0].Reason.Contains("실명"), "Room 변경 미리보기와 동일값 제외");
        reject(() => RoomUsePlanner.Plan(rules, rows.Take(3), []), "용도 없는 Room 전체 보류");
        reject(() => RoomUsePlanner.Plan(rules, rows, [new ValueEdit { Original = rows[1], Value = "E" }]), "기존 변경 충돌 보류");
        var incomplete = new[]
        {
            Row("7", "실명", ""), Row("7", "용도", "C"),
            Row("8", "실명", " ND "), Row("8", "용도", "E"),
            Row("9", "용도", "U"),
            Row("10", "실명", "기계실"), Row("10", "용도", "R"),
            Row("11", "실명", " "), Row("11", "용도", ""),
            Row("12", "실명", "nd"), Row("12", "용도", "Z"),
            Row("13", "실명", " "), Row("13", "용도", "R")
        };
        var partial = RoomUsePlanner.Plan(rules, incomplete, []);
        check(partial.MatchedOwners == 6 &&
            partial.Edits.Select(e => (e.Original.OwnerId, e.Value)).SequenceEqual([("7", ""), ("8", "Z"), ("10", "U"), ("13", "")]) &&
            partial.Notes.Count(n => n.Contains("용도 변경 제외")) == 1,
            "실명 공란·공백은 기존 용도 지우기, ND는 Z, 이미 맞는 용도와 실명 매개변수 누락은 편집하지 않음");
        reject(() => RoomUsePlanner.Plan(rules, incomplete, [new ValueEdit { Original = incomplete[1], Value = "R" }]),
            "실명이 공란인 Room의 이전 R 변경 예정은 보류");
        var other = new RuleSet { Version = 1, Name = "설명 입력", Rules =
        [
            new() { Name = "공통 설명", Match = new() { Scope = "인스턴스", Category = "룸", Type = "실" },
                Set = [new() { Name = "설명", Storage = "String", DataType = Text, Value = "입력" }] }
        ] };
        var combinedRows = rows.Take(2).Concat([Row("1", "설명", "기존")]).ToList();
        foreach (var row in combinedRows) row.TypeName = "실";
        check(RuleBatch.Query([rules, other]).IncludeOtherElements &&
            !query.Covers(RuleBatch.Query([rules, other])) &&
            RuleBatch.Plan([rules, other], combinedRows, [], false).Edits.Count == 2,
            "용도와 일반 값 입력 룰셋을 한 조회·변경 목록으로 준비");
        other.Rules[0].Set[0].Name = "용도";
        other.Rules[0].Set[0].Value = "R";
        reject(() => RuleBatch.Plan([other, rules], combinedRows, [], false), "현재 값과 같은 룰셋 제안과 다른 룰셋의 충돌도 차단");
        var auditRows = new List<ParameterRow>
        {
            Row("3", "실명", "ND"), Row("3", "용도", "R"), Row("3", "번호", ""),
            Row("3", "기존 실번호", "A-1"), Row("3", "지상지하", "ND"), Row("3", "층", "015"),
            Row("4", "실명", "기계실"), Row("4", "용도", "U"), Row("4", "번호", "101"),
            Row("4", "기존 실번호", "101"), Row("4", "지상지하", "지상"), Row("4", "층", "01")
        };
        var snapshot = new ModelSnapshot
        {
            Title = "검토모델", Path = "model.rvt", Rows = auditRows,
            Owners = [new() { OwnerId = "3", Name = "", ElementName = "Revit 룸 이름", ElementId = "3", Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory }]
        };
        var findings = RoomSpaceAudit.Inspect([snapshot]).Where(f => RoomUsePlanner.RequiredAuditFields.Contains(f.Field)).ToList();
        check(findings.Count == 4 && findings.Any(f => f.Field == "실명" && f.Issue == "ND") &&
            findings.Any(f => f.Field == "번호" && f.Issue == "빈값") &&
            findings.Any(f => f.Field == "지상지하" && f.Issue == "ND") &&
            findings.Any(f => f.Field == "층" && f.CurrentValue == "015") &&
            findings.All(f => f.ElementId == "3" && f.RevitName == "Revit 룸 이름" && f.Number == ""),
            "Room 공간정보 누락·ND·층 세 자리 및 이름·번호 식별값 보존");
        check(!findings.Any(f => f.Field == "기존 실번호"), "실제 매개변수 기존 실번호는 누락으로 오인하지 않기");
        var missingNameFindings = RoomSpaceAudit.Inspect([new ModelSnapshot
        {
            Title = "누락", Path = "missing.rvt",
            Owners = [new() { OwnerId = "11", ElementId = "11", ElementName = "이름", Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory }],
            Rows = [Row("11", "용도", "R")]
        }]);
        check(missingNameFindings.Any(f => f.Field == "실명" && f.Issue == "매개변수 없음 — 용도 변경 제외"),
            "실명 매개변수 없는 Room도 엑셀 점검 대상");
        foreach (var invalidFloor in new[] { "1", "2", "3", "015", "A1" })
        {
            auditRows.Single(r => r.OwnerId == "3" && r.Name == "층").Raw = auditRows.Single(r => r.OwnerId == "3" && r.Name == "층").Display = invalidFloor;
            check(RoomSpaceAudit.Inspect([snapshot]).Any(f => f.Field == "층" && f.Issue.Contains("두 자리")), "층 형식 오류: " + invalidFloor);
        }
        foreach (var validFloor in new[] { "01", "02", "03", "15" })
        {
            auditRows.Single(r => r.OwnerId == "3" && r.Name == "층").Raw = auditRows.Single(r => r.OwnerId == "3" && r.Name == "층").Display = validFloor;
            check(!RoomSpaceAudit.Inspect([snapshot]).Any(f => f.Field == "층"), "층 두 자리 유지: " + validFloor);
        }
        rules.RoomUse!.Terms[0].Code = "잘못";
        reject(() => RulePlanner.Parse(JsonSerializer.Serialize(rules)), "잘못된 Room 사전 거부");
    }
}
