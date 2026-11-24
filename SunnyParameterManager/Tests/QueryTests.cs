using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Text.Json;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows.Controls;

internal static class QueryTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        RuleSet Basic(string field = "업체명") => new() { Version = 1, Name = "선택 조회 검사", Rules = [new() { Name = "업체", Match = new() { Scope = "유형", Type = "KAC-", TypeMatch = "시작" }, Set = [new() { Name = field, DataType = "text", Storage = "String", Value = "ND" }] }] };
        var basic = Basic(); var q = QueryPlan.FromRules(basic);
        check(q.Names.SequenceEqual(new[] { "업체명" }) && q.Scopes.SequenceEqual(new[] { "유형" }) && !q.IncludeOuterMaterial, "일반 룰셋 목표 항목만 조회 계획");
        basic.Rules[0].Set[0].ParameterKey = "builtin:-123";
        q = QueryPlan.FromRules(basic);
        check(q.Includes("다른 표시명", "builtin:-123") && q.Includes("업체명", "id:5") && !q.Includes("길이", "id:6"), "이름·식별자 둘 다 보존하고 다른 필드 제외");
        var space = new RuleSet { Version = 3, Name = "공간", Space = new() { Source = "표", SourceSha256 = new('a', 64), Buildings = [new() { Airport = "MWX", Number = "15", Name = "시험", ManagementCode = "MWX1D99", Ownership = "1", SourceRow = 1 }] } };
        var sq = QueryPlan.FromRules(space);
        check(sq.Names.Count == 2 && sq.Scopes.SequenceEqual(new[] { "프로젝트 정보" }) && !sq.IncludeOuterMaterial, "공항·건물 룰셋은 프로젝트 정보 두 항목만");
        space.Space!.MapAssetNumber = true;
        var aq = QueryPlan.FromRules(space);
        check(aq.Names.Contains("자산번호") && !sq.Covers(aq) && aq.Covers(sq), "자산번호 옵션 변경 시 미조회 식별");
        space.Space.MapAssetNumber = false;
        var kac = new RuleSet { Version = 2, Name = "KAC", Kac = new() { ManufacturerInfo = "KAC-v1.0", Company = "ND", MaterialPolicy = "외측-상단-천장하단-단일재료", UnresolvedPolicy = "기존값유지-확인필요", Classification = [new() { Code = "CL1210" }], Ifc = [new() { Prefix = "CL", Value = "IfcColumn", Source = "표" }] } };
        var kq = QueryPlan.FromRules(kac);
        check(kq.Names.Count == 6 && kq.Keys.Count == 2 && kq.IncludeOuterMaterial && kq.Scopes.Count == 2, "KAC 여섯 항목·실제 IFC 키·반대 범위·외측 재료 근거");
        kac.Kac!.NormalizeFloorName = true;
        var floor = QueryPlan.FromRules(kac);
        check(floor.Names.Contains("층") && !kq.Covers(floor) && floor.Covers(kq), "층 옵션 변경 시 미조회 식별");
        var partial = new ModelSnapshot { Title = "선택 모델", Query = sq };
        reject(() => QueryPlan.RequireCoverage([partial], basic), "공간 조회로 유형 룰셋 평가 차단");
        QueryPlan.RequireCoverage([new ModelSnapshot()], kac);
        check(true, "이전 전체 조회 스냅샷 호환");
        var roundtrip = JsonSerializer.Deserialize<ModelSnapshot>(JsonSerializer.Serialize(partial))!;
        check(roundtrip.Query!.Covers(sq) && !roundtrip.Query.Covers(kq), "작업 보관 후 조회 범위 유지");
        reject(() => new QueryPlan().Validate(), "빈 선택 계획은 전체 조회로 해석하지 않음");
        reject(() => new QueryPlan { Version = 2, Scopes = ["유형"], Names = ["업체명"] }.Validate(), "알 수 없는 선택 조회 버전 차단");
        var owner = new ParameterRow { ModelPath = "A", OwnerId = "1", TypeName = "KAC-시험", Scope = "유형", BlockReason = "대상 식별" };
        reject(() => RulePlanner.Plan(Basic(), [owner], [], false), "필드 없는 소유자도 누락 진단에서 유지");
        check(!owner.Editable, "식별 메타데이터는 편집 불가");
        var w = new MainWindow();
        w.SelectRules(Basic());
        check(((TextBlock)w.FindName("QuerySummary")).Text.Contains("업체명"), "조회 전 룰셋 이름·필드 안내");
        check(!((Button)w.FindName("ReviewRulesButton")).IsEnabled, "RVT 미조회에서는 준비한 룰셋 적용 차단");
        var models = (ObservableCollection<ModelItem>)typeof(MainWindow).GetField("_models", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        models.Add(new() { Path = "A", Snapshot = partial });
        reject(() => w.PrepareRules(Basic()), "실제 화면의 미조회 필드 차단");
        var edits = (Dictionary<string, ValueEdit>)typeof(MainWindow).GetField("_edits", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w)!;
        edits["test"] = new() { Value = "수동" };
        w.SelectRules(space);
        check(edits["test"].Value == "수동", "룰셋 교체 시 기존 변경 예정 보존");
        edits.Clear(); models.Clear();
        w.Close();
        basic = Basic(); basic.SampleOnly = true;
        var clean = new MainWindow(); reject(() => clean.SelectRules(basic), "연습용 룰셋 실제 조회 모드 거부"); clean.Close();
    }
}
