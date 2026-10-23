using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

internal static class MaterialAuditTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var json = "{\"Version\":6,\"Name\":\"KAC 재료 점검\",\"Rules\":[],\"MaterialAudit\":true}";
        var rule = RulePlanner.Parse(json);
        var query = QueryPlan.FromRules(rule);
        check(query.IncludeMaterialAudit && query.Scopes.Contains("인스턴스") && query.Scopes.Contains("유형"), "재료 점검은 유형·인스턴스 읽기 전용 선택 조회");
        reject(() => RulePlanner.Parse(json.Replace("\"Rules\":[]", "\"Rules\":[{}]")), "점검과 변경 규칙 혼합 거부");
        reject(() => RulePlanner.Parse(json.Replace("\"Version\":6", "\"Version\":2")), "구버전 재료 점검 플래그 거부");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot { Title = "전체 조회", Query = null }], rule), "일반 조회로 재료 점검 근거 대체 불가");
        QueryPlan.RequireCoverage([new ModelSnapshot { Title = "점검", Query = query, MaterialAuditItems = [] }], rule);
        check(true, "점검 결과 0건도 유효");
        var path = Path.Combine(Path.GetTempPath(), "SunnyMaterialAuditTest-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            MaterialAuditWorkbook.Save(path, [new MaterialAuditItem { Model = "CJU-모델.rvt", Category = "벽", Family = "기본벽", TypeName = "KAC-A-WA1000",
                TypeId = "42", Issue = "KAC- 이외 재료", Scope = "유형 매개변수", Slot = "구조 재료", Material = "일반<재료>&", ElementCount = 3, ExampleElementId = "70" }]);
            using var zip = ZipFile.OpenRead(path);
            check(zip.Entries.Count == 6, "점검 XLSX 표준 구성");
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = XDocument.Parse(reader.ReadToEnd());
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var cells = xml.Descendants(ns + "c").Select(c => (string?)c.Element(ns + "is")?.Element(ns + "t") ?? "").ToArray();
            check(cells.Contains("모델 파일") && cells.Contains("인스턴스 재료 매개변수") == false && cells.Contains("일반<재료>&"), "점검 XLSX 머리글·값·XML 이스케이프");
            check(xml.Descendants(ns + "c").Any(c => (string?)c.Attribute("r") == "J2" && (string?)c.Element(ns + "v") == "3") &&
                xml.Descendants(ns + "autoFilter").Any(), "점검 XLSX 숫자 수량·필터");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
