using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

internal static class ProjectBasePointTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string json = "{\"Version\":8,\"Name\":\"프로젝트 기준점 공유대지 점검\",\"Rules\":[],\"ProjectBasePointAudit\":true}";
        var rule = RulePlanner.Parse(json);
        var query = QueryPlan.FromRules(rule);
        check(query.IncludeProjectBasePointAudit && query.Scopes.SequenceEqual(["프로젝트 정보"]), "기준점 점검은 프로젝트 범위만 읽기");
        reject(() => RulePlanner.Parse(json.Replace("\"Rules\":[]", "\"Rules\":[{}]")), "기준점 점검과 변경 규칙 혼합 거부");
        reject(() => RulePlanner.Parse(json.Replace("\"Version\":8", "\"Version\":7")), "구버전 기준점 점검 플래그 거부");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot { Title = "전체 조회" }], rule), "일반 조회로 기준점 점검 대체 불가");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot { Title = "구버전 점검", Query = query, ProjectBasePointAudit = new() { FormatVersion = 2 } }], rule), "측량 기준점 없는 이전 조회는 재조회");
        QueryPlan.RequireCoverage([new ModelSnapshot { Title = "점검", Query = query, ProjectBasePointAudit = new() { FormatVersion = 3 } }], rule);
        var folder = Path.Combine(Path.GetTempPath(), "SunnyBasePointTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var rulePath = Path.Combine(folder, "기준점.sunnyrules.json");
            File.WriteAllText(rulePath, json);
            var report = ProjectBasePointWorkbook.PathForRule(rulePath);
            var rows = new[]
            {
                new ModelSnapshot { Path = "C:/models/PUS-01.rvt", ProjectBasePointAudit = new()
                    { FormatVersion = 3, SiteName = "공유대지 A", NorthSouthMillimeters = -12345, EastWestMillimeters = 7500,
                      ElevationMillimeters = 1250, TrueNorthDegrees = 346, Status = "확인", SurveyNorthSouthMillimeters = 123456,
                      SurveyEastWestMillimeters = -654321, SurveyElevationMillimeters = 987, SurveyStatus = "확인" } },
                new ModelSnapshot { Path = "C:/models/PUS-02.rvt", ProjectBasePointAudit = new()
                    { FormatVersion = 3, SiteName = "", Status = "조회 실패: 기준점 없음", SurveyStatus = "측량 기준점 없음" } }
            };
            ProjectBasePointWorkbook.Save(report, rows);
            using (var zip = ZipFile.OpenRead(report))
            using (var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()))
            {
                var xml = XDocument.Parse(reader.ReadToEnd());
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                check(xml.Descendants(ns + "row").Count() == 3 && xml.Descendants(ns + "autoFilter").Any(), "모델별 행·필터 유지");
                check(xml.Descendants(ns + "c").Any(c => (string?)c.Attribute("r") == "D2" && (string?)c.Attribute("s") == "1" && (string?)c.Element(ns + "v") == "-12345") &&
                    xml.Descendants(ns + "c").Any(c => (string?)c.Attribute("r") == "G2" && (string?)c.Attribute("s") == "2" && (string?)c.Element(ns + "v") == "346"), "정수 mm·소수 둘째 자리 각도 숫자 셀");
                check(xml.Descendants(ns + "t").Any(t => (string?)t == "조회 실패: 기준점 없음"), "실패 모델도 한 행으로 기록");
            }
            using (var styled = ZipFile.OpenRead(report))
            {
                check(styled.GetEntry("xl/styles.xml") != null, "mm 정수·각도 두 자리 표시 형식 포함");
                var workbook = XDocument.Load(styled.GetEntry("xl/workbook.xml")!.Open());
                XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                check(workbook.Descendants(main + "sheet").Select(s => (string?)s.Attribute("name"))
                    .SequenceEqual(["기준점 점검", "측량 기준점"]), "기존 시트와 측량 기준점 시트 두 개");
                using var surveyReader = new StreamReader(styled.GetEntry("xl/worksheets/sheet2.xml")!.Open());
                var survey = XDocument.Parse(surveyReader.ReadToEnd());
                check(survey.Descendants(main + "c").Any(c => (string?)c.Attribute("r") == "D2" && (string?)c.Attribute("s") == "1" && (string?)c.Element(main + "v") == "123456") &&
                    survey.Descendants(main + "c").Any(c => (string?)c.Attribute("r") == "E2" && (string?)c.Element(main + "v") == "-654321") &&
                    survey.Descendants(main + "c").Any(c => (string?)c.Attribute("r") == "F2" && (string?)c.Element(main + "v") == "987") &&
                    survey.Descendants(main + "autoFilter").Any(f => (string?)f.Attribute("ref") == "A1:G3"),
                    "측량 좌표 3개를 정수 mm 숫자 셀로 기록");
                check(survey.Descendants(main + "t").Any(t => (string?)t == "측량 기준점 없음"),
                    "측량 기준점 부재를 해당 시트에 표시");
            }
            ProjectBasePointWorkbook.Save(report, rows);
            check(Directory.GetFiles(folder, "*.xlsx").SequenceEqual([report]), "반복 저장은 같은 엑셀 한 파일만 갱신");
        }
        finally { Directory.Delete(folder, true); }
    }
}
