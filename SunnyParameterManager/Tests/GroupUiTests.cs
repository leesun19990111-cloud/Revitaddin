using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static class GroupUiTests
{
 public static void Run(Action<bool, string> check, Action<Window, string, int, int> render)
 {
  var w = new MainWindow();
  T C<T>(string n) where T : FrameworkElement => (T)w.FindName(n);
  var models = (ObservableCollection<ModelItem>)C<ListBox>("ModelsList").ItemsSource;
  ParameterRow Row(string path, string owner, bool blocked) => new()
  {
   ModelPath = path, ModelTitle = path, OwnerId = owner, ElementId = owner, Scope = "인스턴스", TypeName = "KAC-A-BM9900_트러스연결구", Family = "연결구", ParameterKey = "id:100", Name = "건축",
   Editor = "예/아니요", Storage = "Integer", DataType = "bool", HasValue = false, Raw = "0", Choices = [new("0", "아니요"), new("1", "예")], IdentityDataParameter = true, Cleanup = new() { Version = "검증" },
   BlockReason = blocked ? "그룹 제한 — 캐노피 트러스 / 그룹 편집에서 검토" : "",
   GroupEdit = blocked ? new() { Name = "캐노피 트러스", ElementId = "300", Identity = "그룹", Reason = "그룹 인스턴스 간 값이 정렬되는 매개변수입니다. 원본의 그룹 편집에서 검토하세요." } : null
  };
  var m = new ModelSnapshot { Path = "검증_A.rvt", Query = QueryPlan.FromRules(CleanupPlanner.Rules()), Rows = [Row("검증_A.rvt", "101", true), Row("검증_A.rvt", "102", false)] };
  var b = new ModelSnapshot { Path = "검증_B.rvt", Query = m.Query, Rows = [Row("검증_B.rvt", "201", true)] };
  models.Add(new() { Path = m.Path, Snapshot = m }); models.Add(new() { Path = b.Path, Snapshot = b });
  w.PrepareRules(CleanupPlanner.Rules());
  check(C<DataGrid>("EditsGrid").Items.Count == 1 && ((ValueEdit)C<DataGrid>("EditsGrid").Items[0]).Original.ElementId == "102", "화면 룰셋 검토는 그룹 제한 제외·일반 요소 변경 유지");
  var grid = C<DataGrid>("GroupRestrictionsGrid");
  check(grid.Items.Count == 2 && C<TabItem>("GroupRestrictionsTab").Header.ToString()!.Contains("2"), "전체 모델의 그룹 제한 탭·건수 표시");
  check(new[] { "소속 파일", "그룹명", "그룹 ID", "요소 ID", "매개변수", "제외 사유" }.All(h => grid.Columns.Any(c => c.Header.ToString() == h)), "그룹 제외 목록 식별·원인 열 제공");
  check(grid.IsReadOnly && grid.EnableRowVirtualization, "대량 제외 목록 가상화·읽기 전용");
  C<TabControl>("MainTabs").SelectedItem = C<TabItem>("GroupRestrictionsTab");
  render(w, "group-restrictions-1080", 1080, 700); render(w, "group-restrictions-1360", 1360, 860);
  C<TextBox>("GroupSearchBox").Text = "201"; check(grid.Items.Count == 1 && ((ParameterRow)grid.Items[0]).ModelPath == b.Path, "제외 목록 요소 ID 검색");
  C<TextBox>("GroupSearchBox").Text = "검증_A"; check(grid.Items.Count == 1, "제외 목록 소속 모델 검색");
  C<TextBox>("GroupSearchBox").Clear(); check(grid.Items.Count == 2, "검색 해제 전체 제외 목록 복원");
  check(C<TextBox>("RuleSummary").Text.Contains("101") && C<TextBox>("RuleSummary").Text.Contains("그룹 제한"), "변경이 필요했던 제한 항목은 룰셋 검토 기록에도 남음");
  ((Dictionary<string, ValueEdit>)typeof(MainWindow).GetField("_edits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!).Clear(); w.Close();
 }
}
