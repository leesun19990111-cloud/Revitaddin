using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

internal static class BusyUiTests
{
 public static void Run(Action<bool, string> check, Action<Action, string> reject, Action<Window, string, int, int> render)
 {
  var w = new MainWindow();
  T Control<T>(string name) where T : FrameworkElement => (T)w.FindName(name);
  void Call(string method) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, [w, new RoutedEventArgs()]);
  var rules = CleanupPlanner.Rules();
  w.SelectRules(rules);
  check(Control<Border>("WelcomeRulesPanel").Visibility == Visibility.Visible && Control<TextBlock>("WelcomeRulesName").Text == rules.Name, "조회 전 중앙 안내에 선택 룰셋 이름 표시");
  check(Control<Button>("RulesButton").Content.ToString() == "룰셋 바꾸기" && Control<TextBlock>("QuerySummary").Text.Contains("선택 완료"), "버튼·고정 머리글에 선택 완료 피드백");
  render(w, "rules-selected-welcome-1080", 1080, 700);
  reject(() => w.SelectRules(new RuleSet { Version = 99 }), "잘못된 룰셋 선택 거부");
  check(Control<TextBlock>("WelcomeRulesName").Text == rules.Name, "선택 실패 시 기존 선택 표시 유지");
  Call("UseFullQuery");
  check(Control<Border>("WelcomeRulesPanel").Visibility == Visibility.Collapsed && !Control<TextBlock>("QuerySummary").Text.Contains("선택 완료"), "룰셋 해제 시 완료 표시 해제");
  w.SelectRules(rules); w.LoadSample();
  check(Control<Border>("WelcomeRulesPanel").Visibility == Visibility.Collapsed, "샘플 전환 시 이전 룰셋 표시 초기화");
  var list = Control<ListBox>("ModelsList");
  var models = (ObservableCollection<ModelItem>)list.ItemsSource;
  for (int i = 3; i <= 60; i++) models.Add(new() { Path = $"검증_모델_{i:00}.rvt", Status = i == 3 ? "조회 중…" : "조회 전" });
  w.SelectRules(rules);
  render(w, "files-before-query-1080", 1080, 700);
  var scroll = Descendants<ScrollViewer>(list).First();
  list.SelectedIndex = 0;
  var grid = Control<DataGrid>("ValuesGrid"); var beforeRows = grid.ItemsSource;
  var run = typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!;
  var task = (Task)run.Invoke(w, [new Func<Task>(() =>
  {
   check(list.IsEnabled && scroll.IsEnabled && Control<Grid>("WorkspacePanel").IsEnabled, "실제 실행 잠금 중 파일 목록·스크롤 활성");
   check(new[] { "TopActions", "FileActions", "MainTabs", "WelcomePanel", "ShowAllModelsButton", "ReviewButton", "ApplyButton" }.All(n => !Control<FrameworkElement>(n).IsEnabled), "조회 중 파일 변경·룰셋·편집·저장 잠금 유지");
   reject(() => w.SelectRules(rules), "조회 중 룰셋 교체 방어 유지");
   scroll.ScrollToTop(); list.UpdateLayout();
   scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
   list.UpdateLayout();
   check(scroll.VerticalOffset > 0, "조회 중 실제 WPF 휠 이벤트로 목록 이동");
   scroll.ScrollToEnd(); list.UpdateLayout();
   check(scroll.VerticalOffset > 20 && Descendants<ScrollBar>(list).Any(b => b.Orientation == Orientation.Vertical && b.IsEnabled), "조회 중 파일 끝까지 스크롤·스크롤바 활성");
   list.SelectedIndex = 1;
   check(ReferenceEquals(beforeRows, grid.ItemsSource), "조회 중 목록 탐색은 편집 행을 재생성하지 않음");
   render(w, "files-during-query-1080", 1080, 700);
   return Task.CompletedTask;
  })])!;
  task.GetAwaiter().GetResult();
  check(Control<TabControl>("MainTabs").IsEnabled && Control<StackPanel>("FileActions").IsEnabled && Control<StackPanel>("TopActions").IsEnabled, "작업 종료 후 조작 복원");
  check(grid.Items.Cast<ParameterRow>().All(r => r.ModelPath == models[1].Path) && grid.Items.Count > 0, "작업 종료 후 선택 파일과 표시 행 일치");
  check(Control<TextBlock>("QuerySummary").Text.Contains(rules.Name), "조회 후에도 선택 룰셋 이름 유지");
  w.Close();
 }
 private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
 {
  for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
  {
   var child = VisualTreeHelper.GetChild(parent, i);
   if (child is T match) yield return match;
   foreach (var nested in Descendants<T>(child)) yield return nested;
  }
 }
}
