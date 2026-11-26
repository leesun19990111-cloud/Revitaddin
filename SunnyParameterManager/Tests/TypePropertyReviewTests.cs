using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;

internal static class TypePropertyReviewTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string definition = """
            {"Version":14,"Name":"유형속성검토","Rules":[],"TypePropertyReview":{
            "Classification":[{"Code":"CL1210","Kbims":"E22","Procurement":"AEA110"},{"Code":"WA1110","Kbims":"E2","Procurement":""}],
            "Ifc":[{"Prefix":"CL","Value":"IfcColumn"},{"Prefix":"WA","Value":"IfcWall"}],
            "Buildings":[{"Airport":"MWX","Name":"여객터미널 (국제/국내)","Number":"15","ManagementCode":"MWX1D001","Ownership":"1","SourceRow":208},
            {"Airport":"PUS","Name":"망루초소","Number":"19","ManagementCode":"PUS1B001","Ownership":"","SourceRow":36}],
            "Sources":["대역"]}}
            """;
        var rules = RulePlanner.Parse(definition);
        var query = QueryPlan.FromRules(rules);
        check(query.IncludeTypePropertyReview && query.Names.Count == 10, "유형속성검토 10항목 전용 조회");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot()], rules), "전용 조회값 없는 캐시 재조회 요구");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot { Query = query, TypePropertyReview = new() }], rules),
            "유형명 범위가 좁았던 이전 조회 결과를 새 검토에 사용하지 않음");
        reject(() => RulePlanner.Plan(rules, [], [], false), "유형속성검토는 저장 가능한 변경을 만들지 않음");
        reject(() => RulePlanner.Parse(definition.Replace("\"Rules\":[]", "\"Rules\":[],\"Cleanup\":true")), "쓰기 모드와 검토 룰셋 혼합 차단");

        var model = new ModelSnapshot
        {
            Path = @"C:\local\개인 로컬.rvt", CentralPath = @"\\server\projects\MWX-15여객터미널-SA.rvt", Title = "여객터미널",
            Query = query, TypePropertyReview = new() { FormatVersion = TypePropertyReviewSnapshot.CurrentFormatVersion, Airport = new("MWX"), Building = new("1D001-1") }
        };
        var column = new TypePropertyEntry
        {
            Name = "KAC-S-CL1210", ElementId = "12", InstanceCount = 2,
            TypeValues = new()
            {
                ["다음으로 IFC로 내보내기"] = new("IfcColumn"),
                ["제작정보"] = new("KAC-v1.0"), ["KBIMS-부위코드"] = new("E22"), ["조달청표준공사코드"] = new("AEA110")
            },
            InstanceValues = new()
            {
                ["작업세트"] = [new("02_구조"), new("01_건축")],
                ["층"] = [new("PIT층"), new("지하2층"), new("GL+6000_지상2층")],
                ["IsExternal"] = [new("아니요")], ["재료명"] = [new("ND"), new("")]
            }
        };
        model.TypePropertyReview.Types.Add(column);
        model.TypePropertyReview.Types.Add(new TypePropertyEntry
        {
            Name = "KAC-A-WA1110", ElementId = "13", InstanceCount = 1,
            InstanceValues = new() { ["작업세트"] = [new("01_건축")] }
        });
        var unplaced = new TypePropertyEntry { Name = "KAC-A-WA1111", ElementId = "14" };
        model.TypePropertyReview.Types.Add(unplaced);
        model.TypePropertyReview.Types.Add(new TypePropertyEntry
        {
            Name = "기본 벽", ElementId = "15", InstanceCount = 1,
            InstanceValues = new() { ["작업세트"] = [new("01_건축")] }
        });
        var audit = new TypePropertyReview(rules.TypePropertyReview!);
        var rows = audit.Inspect(model).ToList();
        check(rows.Select(row => row.Type.ElementId).SequenceEqual(new[] { "13", "12", "15" }) && rows[0].Cells[1].Issue == "", "미배치 유형을 제외하고 배치 유형만 점검");
        check(rows[2].NameIssue.Length != 0 && rows[2].Cells[1].Value == "01_건축" &&
            rows[2].Cells.All(cell => cell.Issue.Length == 0), "기본 유형명은 이름만 강조하고 실제 작업세트 표시");
        QueryPlan.RequireCoverage([model], rules);
        (string Name, bool Valid)[] formats = [("KAC-A-WA1100_벽체", true), ("KAC-S-CL1210-기둥", true),
            ("KAC-A-WA1100", true), ("KAC-A-WA110 벽", false), ("KAC-S-CL12101", false), ("KAC-A-WA1100A", false),
            ("kac-a-wa1100", false), ("KAC-M-WA1100", false)];
        var examples = new ModelSnapshot
        {
            Path = model.Path, CentralPath = model.CentralPath,
            TypePropertyReview = new()
            {
                FormatVersion = TypePropertyReviewSnapshot.CurrentFormatVersion,
                Types = formats.Select(example => new TypePropertyEntry { Name = example.Name, InstanceCount = 1 }).ToList()
            }
        };
        var inspectedFormats = audit.Inspect(examples).ToDictionary(row => row.Type.Name);
        check(formats.All(example => (inspectedFormats[example.Name].NameIssue.Length == 0) == example.Valid),
            "KAC-A/S·대문자 2자·숫자 4자리와 코드 경계의 이름 형식 점검");
        check(rows[0].Cells[8].Issue.Contains("조달청 기준값 없음"), "원본 조달청 매핑 공란은 붉은 셀에 사유 표시");
        check(rows[1].Cells.Select((c, i) => (c.Issue, i)).Where(c => c.Issue.Length > 0).Select(c => c.i).SequenceEqual(new[] { 1, 6, 9 }),
            "배치 작업세트·층 형식 오류·혼재 재료 공란만 강조");
        check(rows[0].Cells[5] == new TypePropertyCell("") && rows[1].Cells[5] == new TypePropertyCell("아니요"), "빈 IsExternal과 아니요는 값 표시만");
        column.InstanceValues["IsExternal"] = [new("예"), new("아니요"), new(Ambiguous: true)];
        var external = audit.Inspect(model).Single(row => row.Type == column).Cells[5];
        check(external.Value.Contains("예") && external.Value.Contains("아니요") && external.Issue == "", "여러 IsExternal 값과 동명 필드도 강조하지 않음");
        check(rows[1].Cells[2].Value == "MWX" && rows[1].Cells[3].Value == "1D001-1", "로컬명이 다르더라도 중앙파일명 기준 공항·건축물번호");
        column.InstanceValues["다음으로 IFC로 내보내기"] = [new("IfcWall")];
        check(audit.Inspect(model).Single(row => row.Type == column).Cells[0].Issue.Contains("IfcColumn"), "유형 IFC와 다른 배치 요소 IFC 검출");
        column.InstanceValues.Remove("다음으로 IFC로 내보내기");
        var gmpModel = new ModelSnapshot { Path = @"C:\GMP-99시범-SA.rvt", Title = "시범", TypePropertyReview = new() { FormatVersion = TypePropertyReviewSnapshot.CurrentFormatVersion, Airport = new("GMP") } };
        gmpModel.TypePropertyReview.Types.Add(new TypePropertyEntry { Name = "KAC-A-WA1110", InstanceCount = 1 });
        check(audit.Inspect(gmpModel).Single().Cells[2].Issue == "" && audit.Inspect(gmpModel).Single().Cells[3].Issue.Contains("없음"),
            "배치 유형이 있는 모델은 기존 GMP 공항코드 판정과 원본에 없는 건물 검토를 유지");

        var noIssues = new ModelSnapshot
        {
            Path = @"C:\local\미작성 매개변수 검토.rvt",
            Title = "여객터미널",
            TypePropertyReview = new() { FormatVersion = TypePropertyReviewSnapshot.CurrentFormatVersion, Types = [unplaced] }
        };
        ModelSnapshot[] models = [model, gmpModel, noIssues];
        var work = Path.Combine(Path.GetTempPath(), "SunnyTypeReview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var rulePath = Path.Combine(work, "유형속성검토.sunnyrules.json");
            File.WriteAllText(rulePath, definition);
            var (file, issues) = TypePropertyReviewWorkbook.Save(rulePath, models, rules.TypePropertyReview!);
            var first = File.ReadAllBytes(file);
            var (repeat, _) = TypePropertyReviewWorkbook.Save(rulePath, models, rules.TypePropertyReview!);
            check(Path.GetFileNameWithoutExtension(repeat).EndsWith("_2") && first.SequenceEqual(File.ReadAllBytes(file)) && issues > 4,
                "엑셀 다시 저장 시 새 이름 생성 및 기존 보고서 보존");
            using var zip = ZipFile.OpenRead(file);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var bookStream = zip.GetEntry("xl/workbook.xml")!.Open();
            var names = XDocument.Load(bookStream).Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")!).ToList();
            check(names.Count == 4 && names[0] == TypePropertyReviewWorkbook.SummarySheetName &&
                names[^1] == TypePropertyReviewWorkbook.SummarySheetName + " (2)", "첫 시트 요약 및 동일한 이름의 모델 시트 구분");

            Dictionary<string, XElement> ReadCells(int index)
            {
                using var sheet = zip.GetEntry($"xl/worksheets/sheet{index}.xml")!.Open();
                return XDocument.Load(sheet).Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
            }

            var summary = ReadCells(1);
            check(summary["B1"].Value == "유형명", "요약 첫 항목은 유형명 오류 수");
            int SummaryCount(char column, int row)
            {
                var cell = summary[$"{column}{row}"];
                if ((string?)cell.Attribute("t") != "n") throw new Exception("유형속성검토 집계가 숫자 셀이 아닙니다.");
                return (int)cell.Element(ns + "v")!;
            }

            string[] checkedFields = ["유형명", .. TypePropertyReview.Fields];
            var totals = new int[checkedFields.Length];
            for (var m = 0; m < models.Length; m++)
            {
                var detailed = ReadCells(m + 2);
                var modelTotal = 0;
                for (var f = 0; f < totals.Length; f++)
                {
                    var detailColumn = f == 0 ? 'D' : (char)('F' + f);
                    int count = detailed.Count(c => c.Key[0] == detailColumn && (string?)c.Value.Attribute("s") == "5");
                    check(SummaryCount((char)('B' + f), m + 2) == count, $"모델 {m + 1}의 {checkedFields[f]} 집계가 빨간 셀 수와 일치");
                    totals[f] += count;
                    modelTotal += count;
                }
                check(SummaryCount('M', m + 2) == modelTotal, $"모델 {m + 1} 집계 합계");
            }
            check(totals.Select((total, f) => total == SummaryCount((char)('B' + f), models.Length + 2)).All(matches => matches)
                && SummaryCount('M', models.Length + 2) == issues, "매개변수별 총합과 보고서 문제 셀 합계 일치");
            check(summary["A2"].Value != summary["A4"].Value && SummaryCount('M', 4) == 0, "동명 모델 구분 및 미배치 유형만 있는 모델의 0건 표시");
            check(ReadCells(4).Keys.All(reference => reference.EndsWith('1') && reference.Length == 2), "미배치 유형만 있는 상세 시트는 머리글만 표시");
            check(SummaryCount('H', 2) == 0 && SummaryCount('H', models.Length + 2) == 0, "IsExternal의 모델별 집계와 전체 합계는 0");
            var cells = ReadCells(2);
            check((string?)cells["D4"].Attribute("s") == "5" && (string?)cells["D3"].Attribute("s") != "5" &&
                cells["Q4"].Value.Contains("유형명 ·") && SummaryCount('B', 2) == 1 && SummaryCount('B', 5) == 1,
                "기본 이름의 상세 셀과 점검 사유 강조 및 요약에서 한 건 집계");
            check(cells.Values.All(cell => cell.Value != unplaced.Name) && !cells.ContainsKey("D5"), "미배치 유형 행과 해당 강조 셀 모두 제외");
            check((string?)cells["H3"].Attribute("s") == "5" && (string?)cells["M3"].Attribute("s") == "5" && (string?)cells["L3"].Attribute("s") != "5" &&
                (string?)cells["K3"].Attribute("s") != "5" && cells["Q3"].Value.Contains("재료명"), "문제 셀만 빨간 스타일로 표시하고 원인을 기록");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
