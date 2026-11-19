using System.Text.Json;
using System.IO;
using System.IO.Compression;
using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;

internal static class RoomProcedureTests
{
    private const string Text = "autodesk.spec:spec.string-2.0.0";

    private static RoomUseTerm Term(string code, string name, string use, string[]? names = null, string[]? cases = null) =>
        new() { Code = code, Name = name, Use = use, NameMatches = (names ?? [name]).ToList(), Cases = cases?.ToList() ?? [] };

    public static RuleSet Rules() => new()
    {
        Version = 11, Name = "용도구분 + 공간분류코드",
        RoomUse = new()
        {
            ProcedureVersion = 2, SpaceClassification = true,
            Source = "용도 작업절차", SourceSha256 = new string('A', 64),
            SpaceSource = "공간분류 작업절차", SpaceSourceSha256 = new string('B', 64),
            Terms =
            [
                Term("33227", "창고", "R", cases: ["물품창고"]),
                Term("33234", "화장실", "C"),
                Term("33235", "휴게실", "C"),
                Term("33401", "감시실", "R"),
                Term("33405", "기계실", "U"),
                Term("33409", "방풍실", "U", [], ["탑승교방풍실"]),
                Term("34101", "현관", "C", ["현관", "방풍실"]),
                Term("34201", "홀", "C"),
                Term("34301", "복도", "C", cases: ["도착복도"]),
                Term("34401", "계단실", "C", cases: ["계단"]),
                Term("34403", "엘리베이터샤프트", "E"),
                Term("34404", "에스컬레이터", "E"),
                Term("38301", "드라이 에어리어", "U", ["드라이에어리어"], ["DA", "D.A"]),
                Term("38389", "캐노피", "Z"),
                Term("38501", "필로티", "Z")
            ],
            UseAliases =
            [
                new() { Phrase = "하론가스실", Use = "U", Basis = "공통 매칭어" },
                new() { Phrase = "전실", Use = "C", Basis = "공통 매칭어" }
            ]
        }
    };

    private static ParameterRow Row(string owner, string field, string value) => new()
    {
        ModelPath = "rooms.rvt", ModelTitle = "룸", OwnerId = owner, ElementId = owner, Scope = "인스턴스",
        Category = "룸", CategoryKey = RoomUsePlanner.RoomCategory, TypeName = "실",
        Name = field, ParameterKey = "guid:" + field, DataType = Text, Storage = "String",
        Editor = "텍스트", HasValue = value.Length > 0, Raw = value, Display = value
    };

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var rule = RulePlanner.Parse(JsonSerializer.Serialize(Rules()));
        var classifier = new RoomProcedureClassifier(rule.RoomUse!);
        foreach (var (name, use, code) in new[]
        {
            ("", "", ""), ("ND", "Z", ""), ("OPEN", "C", ""),
            ("창고-2", "R", "33227"), ("법무부(송환자 화장실)", "C", "33234"),
            ("관세청(창고)", "R", "33227"), ("화장실(장애인(남))", "C", "33234"),
            ("방풍실", "C", "34101"), ("탑승교방풍실", "U", "33409"),
            ("DA", "U", "38301"), ("NAF", "U", ""), ("하론가스실", "U", ""),
            ("EPS 및 창고", "U", ""), ("PIT감시실", "U", "33117"),
            ("AL/CPS", "U", ""), ("CPS", "U", ""), ("LONGWORD", "R", ""),
            ("로비", "C", ""), ("체크인로비", "C", ""), ("LOBBYOPEN", "C", ""),
            ("도착복도", "C", "34301"), ("입국복도", "C", ""), ("직원통로", "C", ""),
            ("ESC", "E", "34404"), ("ELEV", "E", "34403"),
            ("홀OPEN", "C", ""), ("ELEV홀", "C", "34201"), ("HALL", "C", ""), ("CORRIDOR", "C", ""),
            ("필로티", "Z", "38501"), ("장비고?", "R", ""), ("케이원", "R", ""),
            (")창고(", "R", ""), ("기계실)", "R", ""), ("상부물품창고", "R", "33227")
        })
            check(classifier.Use(name).Use == use && classifier.Code(name).Code == code,
                "새 작업절차 용도·공간분류 판정: " + name);
        var query = QueryPlan.FromRules(rule);
        check(query.Names.SequenceEqual(["실명", "용도", "공간분류코드"]), "복합 룰셋의 세 필드 선택 조회");
        reject(() => QueryPlan.RequireCoverage([new() { Query = new() { Scopes = ["인스턴스"], Names = ["실명", "용도"], IncludeRoomUse = true } }], rule),
            "공간분류코드 없는 이전 조회 재사용 차단");
        var rows = new List<ParameterRow>();
        foreach (var (id, name, use, code) in new[]
        {
            ("1", "방풍실", "U", "33409"), ("2", "ND", "R", "기존"), ("3", "", "R", "기존"),
            ("4", "케이원", "R", "기존"), ("5", "창고", "R", "33227")
        })
            rows.AddRange([Row(id, "실명", name), Row(id, "용도", use), Row(id, "공간분류코드", code)]);
        var plan = RoomUsePlanner.Plan(rule, rows, []);
        check(plan.Edits.Count == 3 && plan.Edits.All(e => e.Original.OwnerId is "1" or "2") &&
            plan.Edits.Single(e => e.Original.OwnerId == "1" && e.Field == "공간분류코드").Value == "34101" &&
            plan.Edits.All(e => e.RoomUseSource?.Name == "실명"),
            "두 필드 변경·근거 보관 및 실명 공란·코드 미확정의 기존값 유지");
        check(RoomUsePlanner.Plan(rule, rows, plan.Edits).Edits.Count == 3, "복합 룰셋 재검토");
        reject(() => RoomUsePlanner.Plan(rule, rows, [new() { Original = rows[2], Value = "99999" }]), "공간분류코드 기존 변경 충돌");
        reject(() => RoomUsePlanner.Plan(rule, rows.Where(r => r.OwnerId != "1" || r.Name != "공간분류코드"), []), "필수 공간분류 매개변수 누락 진단");
        var bulk = Rules();
        bulk.RoomUse!.Terms[0].Cases = Enumerable.Range(0, 191).Select(n => "실례" + n).ToList();
        check(RulePlanner.Parse(JsonSerializer.Serialize(bulk)).RoomUse!.Terms[0].Cases.Count == 191, "191개 사례가 있는 새 사전 지원");
        var other = new RuleSet { Version = 1, Name = "코드 입력", Rules =
        [
            new() { Name = "다른 코드", Match = new() { Scope = "인스턴스", Category = "룸", Type = "실" },
                Set = [new() { Name = "공간분류코드", DataType = Text, Storage = "String", Value = "99999" }] }
        ] };
        reject(() => RuleBatch.Plan([rule, other], rows, [], false), "묶음 룰셋도 공간분류코드 충돌 검사");
        foreach (var edit in plan.Edits)
        { edit.Original.Raw = edit.Original.Display = edit.Value; edit.Original.HasValue = true; }
        check(RoomUsePlanner.Plan(rule, rows, []).Edits.Count == 0, "복합 룰셋 재실행 동일값 제외");
        CheckEstimates(check, reject);
    }

    private static void CheckEstimates(Action<bool, string> check, Action<Action, string> reject)
    {
        var rule = Rules();
        var profile = rule.RoomUse!;
        profile.EstimateUncertain = true;
        profile.Terms.AddRange([Term("34199", "기타 출입구 및 전실", "C", ["기타출입구및전실"]),
            Term("33432", "EPS실", "U"), Term("33699", "기타 업무공간", "R")]);
        profile.EstimateCodeCandidates["전실"] = ["34101", "34199"];
        var rows = new List<ParameterRow>();
        foreach (var (id, name, use, code) in new[]
        {
            ("1", "전실", "C", ""), ("2", "EPS(전기)", "R", "ND"), ("3", "전경대", "R", ""),
            ("4", "ND", "R", ""), ("5", "", "R", "ND"), ("6", "창고", "U", "ND"),
            ("7", "전실", "C", "34101")
        })
            rows.AddRange([Row(id, "실명", name), Row(id, "용도", use), Row(id, "공간분류코드", code)]);
        var plan = RoomUsePlanner.Plan(rule, rows, []);
        check(plan.RoomEstimates.Count == 6 && plan.RoomEstimates.Any(r => r is
                { ElementId: "7", Proposed: "34101", Status: "기존값과 동일" }) &&
            plan.Edits.Where(e => e.RoomEstimate != null).Count() == 4 &&
            plan.Edits.Any(e => e.RoomEstimate is { RealName: "EPS(전기)", Field: "용도", Proposed: "U" }) &&
            plan.Edits.Any(e => e.RoomEstimate is { RealName: "EPS(전기)", Field: "공간분류코드", Before: "ND", Proposed: "33432" }) &&
            plan.Edits.Any(e => e.RoomEstimate is { RealName: "전경대", Proposed: "33699" }) &&
            plan.Edits.Any(e => e.Original.OwnerId == "4" && e.Field == "용도" && e.Value == "Z" && e.RoomEstimate == null) &&
            plan.Edits.All(e => e.Original.OwnerId != "5" || e.RoomEstimate == null),
            "충돌·설비 약어·기타 공간 임시값을 적용 경로와 엑셀에 전달하고 ND의 Z 판정을 보존");
        var folder = Path.Combine(Path.GetTempPath(), "SunnyRoomEstimate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var estimates = plan.Edits.Where(e => e.RoomEstimate != null)
                .Select(e => e.RoomEstimate! with { Status = "임시값 적용 완료" }).ToList();
            var applied = RoomEstimateWorkbook.Save(folder, estimates);
            var original = File.ReadAllBytes(applied);
            var second = RoomEstimateWorkbook.Save(folder, estimates.Select(e => e with { Status = "미반영 · 적용 실패" }));
            var third = RoomEstimateWorkbook.Save(folder, estimates);
            using var zip = ZipFile.OpenRead(applied);
            using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = sheet.ReadToEnd();
            check(xml.Contains("원문 충돌 후보") && xml.Contains("임시값 적용 완료") && xml.Contains("EPS(전기)") &&
                Path.GetFileName(applied) == "용도_공간분류_임시값_적용결과.xlsx" &&
                Path.GetFileName(second) == "용도_공간분류_임시값_적용결과_2.xlsx" &&
                Path.GetFileName(third) == "용도_공간분류_임시값_적용결과_3.xlsx" &&
                Directory.GetFiles(folder, "*.xlsx").Length == 3 && original.SequenceEqual(File.ReadAllBytes(applied)),
                "회차별 임시값 적용 결과 한 파일과 기존 파일 내용 보존");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        profile.EstimateCodeCandidates["오류사례"] = ["99999"];
        reject(() => RulePlanner.Validate(rule), "사전에 없는 코드는 임시값 후보로 등록할 수 없음");
    }
}
