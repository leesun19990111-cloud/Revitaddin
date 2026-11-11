using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;

internal static class RoomReviewTests
{
    private static ParameterRow Row(string id, string field, string value) => new()
    {
        ModelPath = "C:/one/A.rvt", ModelTitle = "첫 모델", OwnerId = id, ElementId = id,
        Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory, Name = field,
        HasValue = value.Length > 0, Raw = value, Display = value
    };

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var rule = RulePlanner.Parse("{\"Version\":12,\"Name\":\"룸 검토\",\"Rules\":[],\"RoomReview\":true}");
        check(rule.RoomReview && rule.RoomUse == null && RoomReviewRules.Fields.Length == 18,
            "룸 검토와 용도 입력 룰셋 분리");
        reject(() => RulePlanner.Parse("{\"Version\":12,\"Name\":\"룸 검토\",\"Rules\":[],\"RoomReview\":true,\"MaterialAudit\":true}"),
            "룸 검토에 다른 변경 규칙 혼합 차단");
        reject(() => RulePlanner.Plan(rule, [], [], false), "룸 검토는 변경 계획 없음");
        var query = QueryPlan.FromRules(rule);
        check(query.IncludeRoomUse && query.Scopes.SequenceEqual(["인스턴스"]) &&
            query.Names.SequenceEqual(RoomReviewRules.Fields) && !query.Names.Contains("계약번호") &&
            query.Names.Contains("기존 실번호") && query.Names.Contains("작성기준"),
            "룸 검토는 최종 20열의 실제 매개변수만 조회");
        var previous = new QueryPlan { Scopes = ["인스턴스"], Names = ["실명", "용도", "번호", "기존 실번호", "지상지하", "층"], IncludeRoomUse = true, RoomIdentityVersion = 1 };
        check(!previous.Covers(query), "기존 용도 룰셋 조회는 룸 검토용 20열에 재사용하지 않음");

        var first = new ModelSnapshot
        {
            Path = "C:/one/A.rvt", Title = "첫 모델",
            Owners =
            [
                new() { ModelPath = "C:/one/A.rvt", OwnerId = "2", ElementId = "2", ElementName = "ND", Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory },
                new() { ModelPath = "C:/one/A.rvt", OwnerId = "1", ElementId = "1", ElementName = "기계실1", Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory }
            ],
            Rows =
            [
                Row("1", "용도", "U"), Row("1", "지상지하", "지상"), Row("1", "층", "01"),
                Row("1", "설명", "기계실"), Row("1", "번호", "101"), Row("1", "실번호", "001"),
                Row("1", "기존 실번호", "A-1"), Row("1", "실명", "기계실"), Row("1", "자산번호", "자산-1"),
                Row("1", "LOD", "300"), Row("1", "공간분류코드", "33405"), Row("1", "구역", "A"),
                Row("1", "실ID", "ID-1"), Row("1", "실내재료마감-걸레받이", "B1"),
                Row("1", "실내재료마감-바닥", "F1"), Row("1", "실내재료마감-벽", "W1"),
                Row("1", "실내재료마감-천장", "C1"), Row("1", "작성기준", "2026"),
                Row("2", "지상지하", "지상"), Row("2", "층", "02"), Row("2", "번호", "102"),
                Row("2", "기존 실번호", "ND"), Row("2", "실명", "ND")
            ]
        };
        var second = new ModelSnapshot { Path = "C:/two/A.rvt", Title = "둘째 모델" };
        var path = Path.Combine(Path.GetTempPath(), "room-review-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            RoomReviewWorkbook.Save(path, [first, second], RoomSpaceAudit.Inspect([first, second]));
            using var zip = ZipFile.OpenRead(path);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
            var sheets = workbook.Descendants(ns + "sheet").Select(s => (string?)s.Attribute("name")).ToList();
            var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var empty = XDocument.Load(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());
            var styles = XDocument.Load(zip.GetEntry("xl/styles.xml")!.Open());
            static string Cell(XDocument xml, XNamespace ns, string key) =>
                xml.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == key).Value;
            bool Red(string key) => (string?)sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == key).Attribute("s") == "1";
            check(sheets.SequenceEqual(["A", "A (2)"]) &&
                RoomReviewWorkbook.Headers.SequenceEqual(["모델명", "용도", "지상지하", "층", "설명", "번호", "실번호", "기존실번호", "이름", "실명", "자산번호", "LOD", "공간분류코드", "구역", "실ID", "실내재료마감-걸레받이", "실내재료마감-바닥", "실내재료마감-벽", "실내재료마감-천장", "작성기준"]) &&
                Cell(sheet, ns, "A1") == "모델명" && Cell(sheet, ns, "T1") == "작성기준" &&
                Cell(sheet, ns, "A2") == "첫 모델" && Cell(sheet, ns, "A3") == "" &&
                Cell(sheet, ns, "B2") == "U" && Cell(sheet, ns, "C2") == "지상" &&
                Cell(sheet, ns, "C3") == "지상" && Cell(sheet, ns, "E2") == "기계실" &&
                Cell(sheet, ns, "H2") == "A-1" && Cell(sheet, ns, "I2") == "기계실1" &&
                Cell(sheet, ns, "J3") == "ND" && Cell(sheet, ns, "T2") == "2026" &&
                sheet.Descendants(ns + "row").Count() == 3 && empty.Descendants(ns + "row").Count() == 1 &&
                (string?)sheet.Descendants(ns + "autoFilter").Single().Attribute("ref") == "A1:T3",
                "모델별 시트·20열 순서·ROOM별 한 행·모델명만 반복 생략");
            check((string?)sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "B3").Attribute("s") == "1" &&
                (string?)sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "J3").Attribute("s") == "1" &&
                styles.Descendants(ns + "font").SelectMany(f => f.Elements(ns + "sz")).Any(s => (string?)s.Attribute("val") == "9") &&
                styles.Descendants(ns + "font").SelectMany(f => f.Elements(ns + "sz")).Any(s => (string?)s.Attribute("val") == "11") &&
                styles.Descendants(ns + "fgColor").Any(c => (string?)c.Attribute("rgb") == "FF256B4A"),
                "빈 용도·ND 실명 강조와 초록색 제목·작은 본문 글꼴");
            check(!Red("A3") && !Red("H3") && !Red("I3") && Red("E3") && Red("G2") && !Red("F2"),
                "의도적 모델명 생략과 기존 실번호·이름 ND는 허용하고 공란 및 실번호 불일치는 강조");
            var number = first.Rows.Single(r => r.OwnerId == "1" && r.Name == "번호");
            var roomCode = first.Rows.Single(r => r.OwnerId == "1" && r.Name == "실번호");
            var level = first.Rows.Single(r => r.OwnerId == "1" && r.Name == "지상지하");
            level.Display = "F";
            roomCode.Display = "UF01101";
            check(!RoomSpaceAudit.Inspect([first]).Any(f => f.ElementId == "1"),
                "용도·지상지하·층·번호를 순서대로 합친 실번호는 통과");
            number.Display = "01";
            check(RoomSpaceAudit.Inspect([first]).Any(f => f.ElementId == "1" && f.Field == "번호"),
                "번호는 숫자 세 자리 필수");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
