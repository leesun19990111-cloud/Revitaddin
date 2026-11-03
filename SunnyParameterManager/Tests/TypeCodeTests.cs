using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

internal static class TypeCodeTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string json = "{\"Version\":7,\"Name\":\"세 자리 유형 코드 보정\",\"Rules\":[],\"TypeCodeFix\":true}";
        var rules = RulePlanner.Parse(json);
        var query = QueryPlan.FromRules(rules);
        check(query.IncludeTypeCodeFix && query.Scopes.SequenceEqual(["유형"]), "유형 이름 전용 조회");
        reject(() => RulePlanner.Parse(json.Replace("\"Rules\":[]", "\"Rules\":[{}]")), "다른 변경 규칙 혼합 차단");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot { Title = "전체 조회" }], rules), "구 조회 근거 사용 차단");
        foreach (var (before, after) in new[] { ("KAC-A-WA000_벽-T200", "KAC-A-WA0000_벽-T200"), ("KAC-S-CL123-기둥", "KAC-S-CL1230-기둥"), ("FR999", "FR9990") })
            check(TypeCodePlanner.TryFix(before, out var actual) && actual == after, "세 자리 코드 끝에 0 추가: " + before);
        foreach (var name in new[] { "KAC-A-WA0000_벽", "KAC-A-WA00001_벽", "앞_KAC-A-WA000_벽", "KAC-M-WA000_벽", "KAC-A-WA000X_벽", "KAC-A-T200_벽" })
            check(!TypeCodePlanner.TryFix(name, out var actual) && actual == name, "대상 외 이름 유지: " + name);
        static (ParameterRow Owner, ParameterRow Name) Type(string id, string value, bool canRename = true)
        {
            var evidence = new CleanupEvidence { Version = "v1", CanRename = canRename };
            var owner = new ParameterRow { ModelPath = "A.rvt", ModelTitle = "A", OwnerId = id, TypeOwnerId = id, ElementId = id,
                Scope = "유형", Category = "벽", CategoryKey = "walls", Family = "기본벽", TypeName = value, Cleanup = evidence };
            var name = new ParameterRow { ModelPath = owner.ModelPath, ModelTitle = owner.ModelTitle, OwnerId = id, TypeOwnerId = id,
                ElementId = id, Scope = "유형", Category = "벽", CategoryKey = "walls", Family = "기본벽", TypeName = value,
                ParameterKey = CleanupPlanner.NameKey, Name = "유형명 정리", Raw = value, Display = value, HasValue = true,
                Cleanup = evidence, BlockReason = "전용" };
            return (owner, name);
        }
        var a = Type("1", "KAC-A-WA000_벽");
        var existing = Type("2", "KAC-A-WA0000_벽");
        var blocked = Type("3", "KAC-A-CL111_기둥", false);
        var good = Type("4", "KAC-S-FR123_바닥");
        var plan = TypeCodePlanner.Plan([a.Owner, a.Name, existing.Owner, existing.Name, blocked.Owner, blocked.Name, good.Owner, good.Name], []);
        check(plan.TypeCodeFindings.Count == 3 && plan.Edits.Count == 1 && plan.Edits[0].Value == "KAC-S-FR1230_바닥", "충돌·이름 변경 불가 제외");
        check(plan.TypeCodeFindings.Any(f => f.Status.Contains("변경 후 이름 존재")) && plan.TypeCodeFindings.Any(f => f.Status.Contains("이름 변경 불가")), "제외 사유 기록");
        reject(() => TypeCodePlanner.Plan([good.Owner, good.Name], [new ValueEdit { Original = good.Name, Value = "다른 이름" }]), "수동 편집 혼합 차단");
        var folder = Path.Combine(Path.GetTempPath(), "SunnyTypeCode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var rulePath = Path.Combine(folder, "유형코드.sunnyrules.json");
            File.WriteAllText(rulePath, json);
            var report = TypeCodeWorkbook.PathForRule(rulePath);
            TypeCodeWorkbook.Save(report, plan.TypeCodeFindings);
            using var zip = ZipFile.OpenRead(report);
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = XDocument.Parse(reader.ReadToEnd());
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var cells = xml.Descendants(ns + "t").Select(e => e.Value).ToArray();
            check(cells.Contains("현재 유형명") && cells.Contains("변경 후 유형명") && cells.Contains("KAC-S-FR1230_바닥") && xml.Descendants(ns + "autoFilter").Any(), "XLSX 이름 전후·필터 기록");
            zip.Dispose();
            TypeCodeWorkbook.Save(report, []);
            check(Directory.GetFiles(folder, "*.xlsx").SequenceEqual([report]), "보고서 한 파일만 갱신");
        }
        finally { Directory.Delete(folder, true); }
    }
}
