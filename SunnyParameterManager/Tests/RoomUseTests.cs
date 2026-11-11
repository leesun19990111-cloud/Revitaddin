using System.Text.Json;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
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
            AuditFields = ["실명", "번호", "기존 실번호", "지상지하", "층"],
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
            query.Names.SequenceEqual(["실명", "용도", "번호", "기존 실번호", "지상지하", "층"]), "Room 전용 용도·점검 필드 조회");
        check(!new QueryPlan { Scopes = ["인스턴스"], Names = ["실명", "용도"] }.Covers(query), "구 조회는 Room 범위 확인 전 재조회");
        check(!new QueryPlan { Scopes = ["인스턴스"], Names = ["실명", "용도", "번호", "기존실번호", "지상지하", "층"] }.Covers(query),
            "띄어쓰기 없는 기존 조회는 실제 기존 실번호 값 재조회");
        check(!new QueryPlan { Scopes = ["인스턴스"], Names = query.Names.ToList(), IncludeRoomUse = true }.Covers(query),
            "0.5.7 Room 조회는 Revit 이름이 없어 재조회");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot { Title = "이전 전체 조회" }], rules),
            "0.5.7 전체 조회 캐시도 Revit Room 이름 재조회");
        foreach (var (name, expected) in new[]
        {
            ("ND", "R"), ("OPEN", "C"), ("중앙감시실", "R"), ("ELEV.PIT", "U"),
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
            Row("10", "실명", "기계실"), Row("10", "용도", "R")
        };
        var partial = RoomUsePlanner.Plan(rules, incomplete, []);
        check(partial.MatchedOwners == 1 && partial.Edits.Count == 1 && partial.Edits[0].Original.OwnerId == "10" &&
            partial.Edits[0].Value == "U" && partial.Notes.Count(n => n.Contains("용도 변경 제외")) == 3,
            "실명 빈값·ND·매개변수 없음은 건너뛰고 정상 Room만 용도 변경");
        check(incomplete.Where(r => r.Name == "용도" && r.OwnerId is "7" or "8" or "9").Select(r => r.Raw)
            .SequenceEqual(["C", "E", "U"]), "실명 없는 Room의 기존 용도 유지");
        reject(() => RoomUsePlanner.Plan(rules, incomplete, [new ValueEdit { Original = incomplete[1], Value = "R" }]),
            "실명 없는 Room의 이전 용도 변경 예정은 적용 차단");
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
        var findings = RoomSpaceAudit.Inspect([snapshot]);
        check(findings.Count == 4 && findings.Any(f => f.Field == "실명" && f.Issue == "ND — 용도 변경 제외") &&
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
        var workbook = Path.Combine(Path.GetTempPath(), "room-audit-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var second = new ModelSnapshot
            {
                Title = "같은 파일명", Path = "other/model.rvt",
                Owners =
                [
                    new() { OwnerId = "5", ElementId = "5", ElementName = "ND", Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory },
                    new() { OwnerId = "6", ElementId = "6", ElementName = "", Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory }
                ],
                Rows =
                [
                    Row("5", "실명", "ND"), Row("5", "번호", "102"), Row("5", "기존 실번호", "102"), Row("5", "지상지하", "지상"), Row("5", "층", "02"),
                    Row("6", "실명", ""), Row("6", "번호", "103"), Row("6", "기존 실번호", "103"), Row("6", "지상지하", "지상"), Row("6", "층", "03")
                ]
            };
            var empty = new ModelSnapshot { Title = "빈 모델", Path = "empty.rvt" };
            RoomSpaceWorkbook.Save(workbook, [snapshot, second, empty], findings.Concat(RoomSpaceAudit.Inspect([second])));
            using var zip = ZipFile.OpenRead(workbook);
            var workbookXml = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var names = workbookXml.Descendants(ns + "sheet").Select(s => (string?)s.Attribute("name")).ToList();
            check(names.SequenceEqual(["model", "model (2)", "empty"]) &&
                zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)) == 3,
                "모델별 시트 및 동일 파일명 시트 구별");
            var firstSheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var secondSheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());
            var emptySheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet3.xml")!.Open());
            static string Cell(XDocument sheet, XNamespace ns, string address) =>
                sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == address).Value;
            check(Cell(firstSheet, ns, "C1") == "이름" && Cell(firstSheet, ns, "E1") == "번호" &&
                Cell(firstSheet, ns, "C2") == "Revit 룸 이름" && Cell(firstSheet, ns, "D2") == "" &&
                Cell(firstSheet, ns, "E2") == "" && Cell(firstSheet, ns, "I2") == "ND — 용도 변경 제외" &&
                Cell(firstSheet, ns, "H5") == "015",
                "이름 우선·번호 열·정상 이름의 요소 ID 숨김");
            check(Cell(secondSheet, ns, "C2") == "ND" && Cell(secondSheet, ns, "D2") == "5" &&
                Cell(secondSheet, ns, "E2") == "102" && Cell(secondSheet, ns, "C3") == "" &&
                Cell(secondSheet, ns, "D3") == "6" && Cell(secondSheet, ns, "E3") == "103" &&
                emptySheet.Descendants(ns + "row").Count() == 1,
                "ND·빈 이름의 요소 ID 보조 표시와 문제 없는 모델 시트 유지");
        }
        finally { if (File.Exists(workbook)) File.Delete(workbook); }
        rules.RoomUse!.Terms[0].Code = "잘못";
        reject(() => RulePlanner.Parse(JsonSerializer.Serialize(rules)), "잘못된 Room 사전 거부");
    }
}
