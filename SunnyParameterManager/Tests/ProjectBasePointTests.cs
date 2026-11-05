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
        QueryPlan.RequireCoverage([new ModelSnapshot { Title = "점검", Query = query, ProjectBasePointAudit = new() }], rule);
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
                    { SiteName = "공유대지 A", NorthSouthMeters = -12.345, EastWestMeters = 7.5,
                      ElevationMeters = 1.25, TrueNorthDegrees = 23.5, Status = "확인" } },
                new ModelSnapshot { Path = "C:/models/PUS-02.rvt", ProjectBasePointAudit = new()
                    { SiteName = "", Status = "조회 실패: 기준점 없음" } }
            };
            ProjectBasePointWorkbook.Save(report, rows);
            using (var zip = ZipFile.OpenRead(report))
            using (var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()))
            {
                var xml = XDocument.Parse(reader.ReadToEnd());
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                check(xml.Descendants(ns + "row").Count() == 3 && xml.Descendants(ns + "autoFilter").Any(), "모델별 행·필터 유지");
                check(xml.Descendants(ns + "c").Any(c => (string?)c.Attribute("r") == "D2" &&
                    double.TryParse((string?)c.Element(ns + "v"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && Math.Abs(value + 12.345) < 1e-9) &&
                    xml.Descendants(ns + "c").Any(c => (string?)c.Attribute("r") == "G2" && (string?)c.Element(ns + "v") == "23.5"), "미터·각도 숫자 셀");
                check(xml.Descendants(ns + "t").Any(t => (string?)t == "조회 실패: 기준점 없음"), "실패 모델도 한 행으로 기록");
            }
            ProjectBasePointWorkbook.Save(report, rows);
            check(Directory.GetFiles(folder, "*.xlsx").SequenceEqual([report]), "반복 저장은 같은 엑셀 한 파일만 갱신");
        }
        finally { Directory.Delete(folder, true); }
    }
}
