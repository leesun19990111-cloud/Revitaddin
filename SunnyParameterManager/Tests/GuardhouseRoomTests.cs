using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using SunnyParameterManager.Desktop;
using SunnyParameterManager.Core;

internal static class GuardhouseRoomTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string json = """{"Version":13,"Name":"초소 룸 정리","Rules":[],"GuardhouseRooms":true}""";
        var rule = RulePlanner.Parse(json);
        var query = QueryPlan.FromRules(rule);
        check(query.IncludeRoomUse && !query.IncludeOtherElements && query.RoomIdentityVersion == 2 &&
            query.Keys.Contains(GuardhouseRoomRules.LevelKey), "초소 룰셋은 Room 및 기본 레벨만 선택 조회");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot { RoomIdentityVersion = 1 }], rule), "이전 Room 조회는 초소 룰셋에 재사용하지 않음");
        reject(() => RulePlanner.Parse(json.Replace("\"Rules\":[]", "\"Rules\":[],\"RoomReview\":true")), "초소 룰셋과 읽기 전용 룸 검토 혼합 거부");

        var rows = Room("1", "", "처마", "지하 2층")
            .Concat(Room("2", "ND", "기존 이름", "GL+6300_지상2층"))
            .Concat(Room("3", "초소", "초소", "1F"))
            .Concat(Room("4", "캐노피", "처마", "2F"))
            .Concat(Room("5", "초소", "초소", "지상3층"))
            .Concat(Room("6", "창고", "창고", "1F"))
            .Concat(Room("7", "", "외부 창고", "1F", "일반동-SP"))
            .Concat(Room("8", "", "초소", "지상 1층 / 1F"))
            .Concat(Room("9", "ND", "초소", "1F / 2F"))
            .Concat(Room("10", "대기실", "초소", "2F"))
            .Concat(Room("11", "", "창고", "2F"))
            .ToList();
        var plan = RulePlanner.Plan(rule, rows, [], false);
        check(plan.GuardhouseRoomFindings.Select(x => (x.ElementId, x.Fields)).SequenceEqual(
            [("2", "이름"), ("6", "이름, 실명"), ("10", "실명"), ("11", "이름")]),
            "기존 이름·실명 중 하나라도 목록 밖이면 룸당 한 행, 공란·비대상 모델 제외");
        var folder = Path.Combine(Path.GetTempPath(), "SunnyGuardhouseTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var rulePath = Path.Combine(folder, "초소_룸_정리.sunnyrules.json");
            File.WriteAllText(rulePath, json);
            var report = GuardhouseRoomWorkbook.PathForRule(rulePath);
            GuardhouseRoomWorkbook.Save(report, plan.GuardhouseRoomFindings);
            using var zip = ZipFile.OpenRead(report);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var sheet = XDocument.Load(stream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var exported = sheet.Descendants(ns + "row").Select(row =>
                row.Descendants(ns + "t").Select(cell => cell.Value).ToArray()).ToList();
            check(Path.GetDirectoryName(report) == folder && exported.Count == 5 &&
                exported[0].SequenceEqual(["모델 파일", "룸 요소 ID", "기존 번호", "기존 이름", "기존 실명", "확인 항목"]) &&
                exported.Any(row => row.SequenceEqual(["정문초소-SP.rvt", "2", "200", "기존 이름", "ND", "이름"])) &&
                exported.Any(row => row.SequenceEqual(["정문초소-SP.rvt", "11", "200", "창고", "", "이름"])) &&
                (string?)sheet.Descendants(ns + "autoFilter").Single().Attribute("ref") == "A1:F5",
                "검토 XLSX에 기존값·확인 항목·정확한 필터 범위 저장");
        }
        finally { Directory.Delete(folder, true); }
        var byRoom = plan.Edits.ToLookup(e => e.Original.OwnerId);
        string? Value(string room, string field) => byRoom[room].SingleOrDefault(e => (field switch { "이름" => e.Original.ParameterKey == GuardhouseRoomRules.NameKey, "번호" => e.Original.ParameterKey == GuardhouseRoomRules.NumberKey, _ => e.Field == field }))?.Value;
        check(Value("1", "실명") == "캐노피" && Value("1", "이름") == "캐노피" &&
            Value("1", "층") == "01" && Value("1", "지상지하") == "F", "실명 공란과 처마 치환이 한 번의 최종값으로 합쳐짐");
        check(Value("2", "층") == "02" && Value("2", "지상지하") == "F" &&
            Value("3", "층") == "01" && Value("8", "실명") == "초소" && Value("8", "층") == "01",
            "ND·초소의 한글·영문 레벨 및 이름으로 보완한 실명을 처리");
        check(Value("4", "이름") == "캐노피" && Value("4", "층") == "01" && Value("4", "실명") == null, "캐노피는 레벨과 무관하게 01");
        check(Value("5", "층") == null && Value("9", "층") == null && Value("5", "지상지하") == "F" &&
            plan.Notes.Any(n => n.Contains("지상3층")) && plan.Notes.Any(n => n.Contains("1F / 2F")), "미지원·상충 레벨은 기존 층 유지 및 사유 표시");
        check(byRoom["6"].Count() == 1 && !byRoom["7"].Any() &&
            new[] { "1", "2", "3", "4", "5", "6", "8", "9" }.All(r => Value(r, "번호") == "001") &&
            plan.Edits.Select(e => e.Original.Key).Distinct().Count() == plan.Edits.Count, "초소 모델 룸 번호만 001로 설정하고 중복 변경 없음");
        var floor = byRoom["2"].Single(e => e.Field == "층");
        check(floor.GuardhouseRoomSources!.Any(r => r.ParameterKey == GuardhouseRoomRules.LevelKey), "층 수정에 레벨 조회 근거 포함");
        reject(() => RulePlanner.Plan(rule, rows, [new ValueEdit { Original = floor.Original, Value = "03" }], false), "이전 층 변경 예정과 충돌하면 검토 보류");
        foreach (var edit in plan.Edits)
        {
            edit.Original.Raw = edit.Value;
            edit.Original.Display = edit.Value;
            edit.Original.HasValue = true;
        }
        check(RulePlanner.Plan(rule, rows, [], false).Edits.Count == 0, "같은 룰셋 재실행 시 추가 변경 없음");
    }

    private static ParameterRow[] Room(string id, string realName, string name, string level, string model = "정문초소-SP")
    {
        ParameterRow Row(string field, string key, string value) => new()
        {
            ModelPath = $"C:/models/{model}.rvt", ModelTitle = model, OwnerId = id, ElementId = id,
            Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory, Name = field, ParameterKey = key,
            Storage = "String", Editor = "텍스트", HasValue = value.Length != 0, Raw = value, Display = value
        };
        return [Row("실명", "id:21", realName), Row("Name", GuardhouseRoomRules.NameKey, name),
            Row("Number", GuardhouseRoomRules.NumberKey, "200"), Row("Level", GuardhouseRoomRules.LevelKey, level),
            Row("지상지하", "id:22", "B"), Row("층", "id:23", "03")];
    }
}