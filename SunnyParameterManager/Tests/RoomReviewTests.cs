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
            query.Names.Contains("기존 실번호") && query.Names.Contains("작성기준") && query.RoomIdentityVersion == 3,
            "룸 검토는 최종 20열의 실제 매개변수만 조회");
        var previous = new QueryPlan { Scopes = ["인스턴스"], Names = ["실명", "용도", "번호", "기존 실번호", "지상지하", "층"], IncludeRoomUse = true, RoomIdentityVersion = 1 };
        check(!previous.Covers(query), "기존 용도 룰셋 조회는 룸 검토용 20열에 재사용하지 않음");

        var first = new ModelSnapshot
        {
            Path = "C:/one/A.rvt", Title = "첫 모델", RoomIdentityVersion = 3,
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
        var second = new ModelSnapshot { Path = "C:/two/A.rvt", Title = "둘째 모델", RoomIdentityVersion = 3 };
        var path = Path.Combine(Path.GetTempPath(), "room-review-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            RoomReviewWorkbook.Save(path, [first, second], RoomSpaceAudit.Inspect([first, second]));
            using var zip = ZipFile.OpenRead(path);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
            var sheets = workbook.Descendants(ns + "sheet").Select(s => (string?)s.Attribute("name")).ToList();
            var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());
            var empty = XDocument.Load(zip.GetEntry("xl/worksheets/sheet3.xml")!.Open());
            var styles = XDocument.Load(zip.GetEntry("xl/styles.xml")!.Open());
            static string Cell(XDocument xml, XNamespace ns, string key) =>
                xml.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == key).Value;
            bool Red(string key) => (string?)sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == key).Attribute("s") is "1" or "5";
            check(sheets.SequenceEqual([RoomReviewWorkbook.UnplacedSheetName, "A", "A (2)"]) &&
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
            check((string?)sheet.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "B3").Attribute("s") == "5" &&
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
        CheckRequiredCells(check);
        CheckUnplacedRooms(check, reject);
    }

    private static void CheckRequiredCells(Action<bool, string> check)
    {
        var model = new ModelSnapshot { Path = "C:/one/A.rvt", Title = "셀 강조 검증", RoomIdentityVersion = 3 };
        var cases = new List<(string Id, string Field)>();
        foreach (var field in RoomReviewRules.RequiredFields)
        foreach (var state in new[] { "빈값", "공백", "미설정", "매개변수 없음" })
        {
            var id = field + "-" + state;
            cases.Add((id, field));
            model.Owners.Add(new() { OwnerId = id, ElementId = id, ElementName = id,
                Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory });
            foreach (var parameter in RoomReviewRules.Fields)
            {
                if (parameter == field && state == "매개변수 없음") continue;
                var row = Row(id, parameter, parameter switch
                {
                    "용도" => "R", "지상지하" => "F", "층" => "01", "번호" => "001",
                    "실번호" => "RF01001", "기존 실번호" => "ND", _ => "입력"
                });
                if (parameter == field)
                {
                    row.Raw = row.Display = state == "공백" ? " \t " : "";
                    row.HasValue = state != "미설정";
                }
                model.Rows.Add(row);
            }
        }
        var path = Path.Combine(Path.GetTempPath(), "room-required-cells-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            RoomReviewWorkbook.Save(path, [model], RoomSpaceAudit.Inspect([model]));
            using var zip = ZipFile.OpenRead(path);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());
            var rows = sheet.Descendants(ns + "row").Skip(1).Select(r => r.Elements(ns + "c").ToList())
                .ToDictionary(cells => cells[8].Value);
            foreach (var (id, field) in cases)
            {
                int column = Array.IndexOf(RoomReviewWorkbook.Headers, field);
                var cells = rows[id];
                check(cells.Count == 20 && cells.Where(c => (string?)c.Attribute("s") == "5").Count() == 1 &&
                    (string?)cells[column].Attribute("s") == "5" &&
                    cells.Where((_, i) => i != column).All(c => (string?)c.Attribute("s") != "5"),
                    "필수 누락 셀만 강한 강조: " + id);
            }
            var styles = XDocument.Load(zip.GetEntry("xl/styles.xml")!.Open());
            var strong = styles.Root!.Element(ns + "cellXfs")!.Elements(ns + "xf").ElementAt(5);
            check((string?)strong.Attribute("fillId") == "5" && (string?)strong.Attribute("borderId") == "1" &&
                (string?)strong.Attribute("fontId") == "3" &&
                styles.Descendants(ns + "fgColor").Any(c => (string?)c.Attribute("rgb") == "FFFFA8B0"),
                "강조 셀의 진한 붉은 배경·글꼴·테두리 저장");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void CheckUnplacedRooms(Action<bool, string> check, Action<Action, string> reject)
    {
        var first = new ModelSnapshot { Path = "C:/one/미배치 룸 점검.rvt", Title = "미배치 확인", RoomIdentityVersion = 3 };
        var second = new ModelSnapshot { Path = "C:/two/B.rvt", Title = "정상 모델", RoomIdentityVersion = 3 };
        foreach (var (id, unplaced) in new[] { ("11", true), ("12", true), ("13", false) })
        {
            first.Owners.Add(new() { OwnerId = id, ElementId = id, ElementName = "룸 " + id,
                Scope = "인스턴스", CategoryKey = RoomUsePlanner.RoomCategory, RoomUnplaced = unplaced });
            var number = Row(id, "번호", id);
            number.RoomUnplaced = unplaced;
            first.Rows.Add(number);
            first.Rows.Add(Row(id, "실명", "실명 " + id));
        }
        check(RoomReviewRules.UnplacedRooms(first).Count == 2, "동일 룸의 여러 조회행은 미배치 개수에 한 번만 반영");
        var path = Path.Combine(Path.GetTempPath(), "room-unplaced-" + Guid.NewGuid().ToString("N") + ".xlsx");
        reject(() => RoomReviewWorkbook.Save(path, [new() { Title = "이전 조회", RoomIdentityVersion = 1 }], []),
            "배치 상태를 확인할 수 없는 이전 룸 조회로 미배치 0개 추정 금지");
        try
        {
            RoomReviewWorkbook.Save(path, [first, second], RoomSpaceAudit.Inspect([first, second]));
            using var zip = ZipFile.OpenRead(path);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var summary = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var data = XDocument.Load(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());
            var names = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open()).Descendants(ns + "sheet")
                .Select(s => (string?)s.Attribute("name")).ToList();
            var cells = summary.Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!, c => c.Value);
            check(names.SequenceEqual(["미배치 룸 점검", "미배치 룸 점검 (2)", "B"]) &&
                cells["A2"].Contains("총 2개") && cells["A2"].Contains(RoomReviewRules.UnplacedNotice) &&
                cells["B5"] == "2" && cells["C5"] == RoomReviewRules.UnplacedNotice &&
                cells["B6"] == "0" && cells["C6"] == "미배치 룸 없음",
                "첫 시트에 전체·모델별 개수와 삭제 요망 표시 및 모델 시트명 충돌 회피");
            check(cells["B9"] == "11" && cells["B10"] == "12" && cells["D9"] == "룸 11" &&
                cells["E10"] == "실명 12" && data.Descendants(ns + "row").Count() == 4 &&
                data.Descendants(ns + "row").Skip(1).All(r => r.Elements(ns + "c").Count() == 20),
                "미배치 요소 ID·번호·이름을 찾을 수 있고 모델 표에는 정상 룸까지 20열로 출력");
            using var emptyZip = ZipFile.OpenRead(RoomReviewWorkbook.Save(path, [second], []));
            var empty = XDocument.Load(emptyZip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            check(empty.Root!.Value.Contains("룸 요소가 없습니다") && !empty.Root.Value.Contains(RoomReviewRules.UnplacedNotice) &&
                File.Exists(path), "미배치 0개도 표시하고 이전 엑셀은 보존");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var next = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_2.xlsx");
            if (File.Exists(next)) File.Delete(next);
        }
    }
}
