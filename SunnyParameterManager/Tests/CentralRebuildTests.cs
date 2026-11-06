using SunnyParameterManager.Core;
using System.IO;

internal static class CentralRebuildTests
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var json = "{\"Version\":9,\"Name\":\"중앙 재생성\",\"Rules\":[],\"RebuildCentral\":true}";
        var rules = RulePlanner.Parse(json);
        var query = QueryPlan.FromRules(rules);
        check(query.IncludeCentralRebuild && query.Scopes.SequenceEqual(["프로젝트 정보"]), "중앙 재생성은 파일 상태만 조회");
        reject(() => RulePlanner.Parse(json.Replace("\"Version\":9", "\"Version\":8")), "버전이 다른 중앙 재생성 거부");
        reject(() => RulePlanner.Parse(json.Replace("\"Rules\":[]", "\"Rules\":[{}]")), "값 변경과 중앙 재생성 혼합 거부");
        var model = new ModelSnapshot { Path = "C:/x.rvt", CanonicalPath = "C:/x.rvt", CentralPath = "C:/x.rvt",
            Kind = "중앙", Title = "x", Query = query, Identity = CentralRebuildPlanner.Identity(Guid.NewGuid(), "C:/x.rvt"),
            CentralRebuild = new() { SourceKind = "중앙", Length = 100, Sha256 = new string('A', 64) } };
        CentralRebuildPlanner.Validate(model);
        QueryPlan.RequireCoverage([model], rules);
        check(true, "중앙 증거와 선택 조회가 맞으면 검토 가능");
        foreach (var kind in new[] { "작업공유 사본" })
        {
            model.Kind = kind;
            model.CentralRebuild!.SourceKind = kind;
            CentralRebuildPlanner.Validate(model);
            QueryPlan.RequireCoverage([model], rules);
            check(true, kind + "도 새 중앙 검토 가능");
        }
        model.Kind = "중앙";
        model.CentralRebuild!.SourceKind = "중앙";
        var same = new CentralRebuildEvidence { SourceKind = "중앙", Length = 100, Sha256 = new string('a', 64), LastWriteUtc = model.CentralRebuild.LastWriteUtc };
        CentralRebuildPlanner.RequireUnchanged(model.CentralRebuild, same);
        same.SourceKind = "일반";
        reject(() => CentralRebuildPlanner.RequireUnchanged(model.CentralRebuild!, same), "파일 종류 변경 거부");
        same.SourceKind = "중앙";
        same.Length++;
        reject(() => CentralRebuildPlanner.RequireUnchanged(model.CentralRebuild!, same), "조회 후 중앙파일 변경 거부");
        var folder = Path.Combine(Path.GetTempPath(), "sunny-central-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var original = Path.Combine(folder, "central.rvt");
            var backup = Path.Combine(folder, "backup.rvt");
            File.WriteAllText(original, "original central bytes");
            var captured = CentralRebuildPlanner.Capture(original, "작업공유 사본");
            File.Copy(original, backup);
            CentralRebuildPlanner.RequireBackupContents(captured, CentralRebuildPlanner.Capture(backup, "작업공유 사본"));
            check(captured.Length > 0 && captured.Sha256.Length == 64, "원본·복구 사본 내용 지문 확인");
            File.AppendAllText(original, "changed");
            reject(() => CentralRebuildPlanner.RequireUnchanged(captured, CentralRebuildPlanner.Capture(original, "작업공유 사본")), "다른 저장 이후 원본 교체 차단");
        }
        finally { Directory.Delete(folder, true); }
    }
}
