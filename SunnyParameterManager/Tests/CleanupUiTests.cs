using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

internal static class CleanupUiTests
{
 public static void Run(Action<bool, string> check, Action<Action, string> reject, Action<Window, string, int, int> render)
 {
  var rules = CleanupPlanner.Rules(); var q = QueryPlan.FromRules(rules);
  var actualFile = Environment.GetEnvironmentVariable("SUNNY_CLEANUP_RULE_TEST_FILE");
  if (!string.IsNullOrWhiteSpace(actualFile))
  {
   var actual = RulePlanner.Parse(System.IO.File.ReadAllText(actualFile));
   check(actual.Cleanup && actual.Version == 4 && actual.PreserveWorksets.SequenceEqual(["00_외장"]) && QueryPlan.FromRules(actual).IncludeCleanup, "전달한 외장 유지 유형·작업세트 정리 룰셋 호환");
  }
  check(q.IncludeCleanup && !q.IncludeOuterMaterial && q.Keys.Contains(CleanupPlanner.WorksetKey), "정리 룰셋 최소 필드·두께 근거 조회 계획");
  var w = new MainWindow(); w.SelectRules(rules);
  var models = (ObservableCollection<ModelItem>)typeof(MainWindow).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
  var row = new ParameterRow { ModelPath = "A.rvt", ModelTitle = "검증 건축", OwnerId = "type-1", TypeOwnerId = "type-1", ElementId = "42", Scope = "유형", TypeName = "KAC-A-CE7410_CW02_THK21", Family = "복합 천장", CategoryKey = "ceiling", ParameterKey = CleanupPlanner.NameKey, Name = "유형명 정리", Raw = "KAC-A-CE7410_CW02_THK21", Display = "KAC-A-CE7410_CW02_THK21", HasValue = true, BlockReason = "정리 전용", Cleanup = new() { CanRename = true, ThicknessMm = 76.5, ThicknessBasis = "복합 구조 전체 두께", Version = "v1" } };
  var m = new ModelSnapshot { Path = "A.rvt", Title = "검증 건축", Query = q, Rows = [row] };
  models.Add(new() { Path = m.Path, Snapshot = m });
  w.PrepareRules(rules);
  var grid = (DataGrid)w.FindName("EditsGrid");
  check(grid.Items.Count == 1 && ((ValueEdit)grid.Items[0]).Value == "KAC-A-CE7410_CW02-T76.5", "실제 창에 유형명 전후 미리보기 표시");
  check(((TextBox)w.FindName("RuleSummary")).Text.Contains("룸 경계") && ((TextBox)w.FindName("RuleSummary")).Text.Contains("변경 항목별"), "범위·항목별 개수·영향 안내");
  w.PrepareRules(rules); check(grid.Items.Count == 1, "정리 룰셋 재검토 중복 누적 없음");
  m.Notices.Add("다른 사용자 소유로 감지된 대상 1개가 있습니다. 소유 사용자: 검증 사용자 / 요소 ID: 42");
  w.PrepareRules(rules);
  check(((TextBox)w.FindName("RuleSummary")).Text.Contains("검증 사용자") && ((TextBox)w.FindName("RuleSummary")).Text.Contains("저장 직전에 다시 확인"), "모든 룰셋 검토에 조회 당시 소유권 주의 표시");
  check(row.Raw == "KAC-A-CE7410_CW02_THK21", "미리보기는 원래 조회 이름 유지");
  render(w, "cleanup-review-1080", 1080, 700); render(w, "cleanup-review-1320", 1320, 820);
  var restored = JsonSerializer.Deserialize<ModelSnapshot>(JsonSerializer.Serialize(m))!;
  check(restored.Query!.IncludeCleanup && restored.Rows[0].Cleanup!.ThicknessMm == 76.5 && !restored.Rows[0].Editable, "작업 보관 정리 근거·전용행 유지");
  reject(() => CleanupPlanner.Plan(m.Rows, [new() { Original = row, Value = "직접 입력" }]), "다른 편집과 혼합 보류");
  var old = JsonSerializer.Deserialize<ModelSnapshot>(JsonSerializer.Serialize(m))!; old.Query!.IncludeCleanup = false;
  reject(() => QueryPlan.RequireCoverage([old], rules), "같은 이름 필드만 조회해도 두께 근거 없으면 재조회");
  var r2 = JsonSerializer.Deserialize<ParameterRow>(JsonSerializer.Serialize(row))!; r2.ModelPath = "B.rvt"; r2.ModelTitle = "검증 구조";
  check(CleanupPlanner.Plan([row, r2], []).Edits.Count == 2, "같은 UniqueId·이름도 모델별 독립 정리");
  ((Dictionary<string, ValueEdit>)typeof(MainWindow).GetField("_edits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!).Clear();
  w.Close();
 }
}
