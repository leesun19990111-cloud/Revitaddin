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
        reject(() => RulePlanner.Plan(rules, [], [], false), "유형속성검토는 저장 가능한 변경을 만들지 않음");
        reject(() => RulePlanner.Parse(definition.Replace("\"Rules\":[]", "\"Rules\":[],\"Cleanup\":true")), "쓰기 모드와 검토 룰셋 혼합 차단");

        var model = new ModelSnapshot
        {
            Path = @"C:\local\개인 로컬.rvt", CentralPath = @"\\server\projects\MWX-15여객터미널-SA.rvt", Title = "여객터미널",
            Query = query, TypePropertyReview = new() { Airport = new("MWX"), Building = new("1D001-1") }
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
        model.TypePropertyReview.Types.Add(new TypePropertyEntry { Name = "KAC-A-WA1110", ElementId = "13" });
        var audit = new TypePropertyReview(rules.TypePropertyReview!);
        var rows = audit.Inspect(model).ToList();
        check(rows.Count == 2 && rows[0].Cells[1].Value == "미배치" && rows[0].Cells[1].Issue == "", "미배치 유형도 표시하고 작업세트 판정 생략");
        check(rows[0].Cells[8].Issue.Contains("조달청 기준값 없음"), "원본 조달청 매핑 공란은 붉은 셀에 사유 표시");
        check(rows[1].Cells.Select((c, i) => (c.Issue, i)).Where(c => c.Issue.Length > 0).Select(c => c.i).SequenceEqual(new[] { 1, 5, 6, 9 }),
            "배치 작업세트·IsExternal 아니요·층 형식 오류·혼재 재료 공란만 강조");
        check(rows[1].Cells[2].Value == "MWX" && rows[1].Cells[3].Value == "1D001-1", "로컬명이 다르더라도 중앙파일명 기준 공항·건축물번호");
        column.InstanceValues["다음으로 IFC로 내보내기"] = [new("IfcWall")];
        check(audit.Inspect(model).Last().Cells[0].Issue.Contains("IfcColumn"), "유형 IFC와 다른 배치 요소 IFC 검출");
        column.InstanceValues.Remove("다음으로 IFC로 내보내기");
        var noTypes = new ModelSnapshot { Path = @"C:\GMP-99시범-SA.rvt", Title = "시범", TypePropertyReview = new() { Airport = new("GMP") } };
        check(audit.Inspect(noTypes).Single().Cells[2].Issue == "" && audit.Inspect(noTypes).Single().Cells[3].Issue.Contains("없음"),
            "GMP 공항코드 판정과 원본에 없는 건물 확인, 유형 없는 모델도 표시");

        var work = Path.Combine(Path.GetTempPath(), "SunnyTypeReview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var rulePath = Path.Combine(work, "유형속성검토.sunnyrules.json");
            File.WriteAllText(rulePath, definition);
            var (file, issues) = TypePropertyReviewWorkbook.Save(rulePath, [model, noTypes], rules.TypePropertyReview!);
            var first = File.ReadAllBytes(file);
            var (repeat, _) = TypePropertyReviewWorkbook.Save(rulePath, [model, noTypes], rules.TypePropertyReview!);
            check(Path.GetFileNameWithoutExtension(repeat).EndsWith("_2") && first.SequenceEqual(File.ReadAllBytes(file)) && issues > 4,
                "엑셀 다시 저장 시 새 이름 생성 및 기존 보고서 보존");
            using var zip = ZipFile.OpenRead(file);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var bookStream = zip.GetEntry("xl/workbook.xml")!.Open();
            check(XDocument.Load(bookStream).Descendants(ns + "sheet").Count() == 2, "파일별 워크시트 2개");
            using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var cells = XDocument.Load(sheet).Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!);
            check((string?)cells["H3"].Attribute("s") == "5" && (string?)cells["L3"].Attribute("s") == "5" &&
                (string?)cells["K3"].Attribute("s") != "5" && cells["Q3"].Value.Contains("재료명"), "문제 셀만 빨간 스타일로 표시하고 원인을 기록");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
