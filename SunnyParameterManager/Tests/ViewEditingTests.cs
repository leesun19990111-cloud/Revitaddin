using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

internal static class ViewEditingTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject, Action<Window, string, int, int> render)
    {
        ParameterRow Row(string owner, string name, string scope = "뷰") => new()
        {
            ModelPath = "뷰검사.rvt", ModelTitle = "뷰 검사 모델", OwnerId = owner, ElementId = owner,
            Scope = scope, Category = scope, CategoryKey = scope, Family = "뷰", TypeName = "공통 유형",
            TypeOwnerId = "view-type", ElementName = name, SheetNumber = scope == "시트" ? "A-101" : "",
            Name = "뷰 구분-1", ParameterKey = "id:42", Storage = "String", DataType = "text",
            Editor = "텍스트", HasValue = true, Raw = "기존 구분", Display = "기존 구분"
        };
        var first = Row("1", "지상 1층 평면도"); var second = Row("2", "지상 2층 평면도");
        var sheet = Row("3", "평면도 도면", "시트");
        check(first.TypeKey == second.TypeKey && first.Key != second.Key, "같은 뷰 유형도 실제 소유 뷰별 독립 식별");
        check(EditPlanner.Stage([first], "건축").Single().Original.OwnerId == "1" && second.Raw == "기존 구분", "선택 뷰만 직접 입력 예정");
        check(EditPlanner.Stage([first, second], "건축").Count == 2, "같은 유형의 서로 다른 뷰 둘 다 선택 가능");
        check(EditPlanner.Stage([sheet], "건축").Single().Type.Contains("A-101 · 평면도 도면"), "변경 검토에 시트 번호와 실제 이름 표시");
        var restored = JsonSerializer.Deserialize<ParameterRow>(JsonSerializer.Serialize(sheet))!;
        check(restored.TargetName == sheet.TargetName && restored.Key == sheet.Key, "작업 파일 왕복 시 뷰·시트 이름과 식별 유지");
        var source = new ModelSnapshot { Path = "기준.rvt", Rows = [first] };
        reject(() => EditPlanner.CopyTypeValues(source, [second], true), "뷰 이름 기반 모델 간 자동 복사 차단");
        reject(() => EditPlanner.CopyTypeValues(source, [sheet], true), "시트 이름 기반 모델 간 자동 복사 차단");
        restored.BlockReason = "뷰 템플릿 제어";
        reject(() => EditPlanner.Stage([restored], "새값"), "잠긴 시트 직접 입력 차단");
        reject(() => EditPlanner.CheckFresh(sheet, restored), "조회 후 템플릿 제어가 생기면 적용 차단");
        var w = new MainWindow(); w.LoadSample();
        var models = ((ListBox)w.FindName("ModelsList")).Items.Cast<ModelItem>().ToList();
        var rows = new List<ParameterRow> { first, second, sheet };
        var compactName = Row("4", "단면 A"); compactName.Name = "뷰구분-1"; compactName.ParameterKey = "id:43"; rows.Add(compactName);
        foreach (var name in new[] { "동측 입면도", "계단 단면도", "천장평면도", "드래프팅 상세", "범례", "마감 일람표", "전체 3D 뷰" }) rows.Add(Row((rows.Count + 1).ToString(), name));
        models[0].Snapshot!.Rows = rows; models[1].Snapshot!.Rows = [];
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(MainWindow).GetMethod("RefreshFields", flags)!.Invoke(w, null);
        var scope = (ComboBox)w.FindName("ScopeCombo"); var field = (ComboBox)w.FindName("FieldCombo");
        var search = (TextBox)w.FindName("FieldSearchBox"); var grid = (DataGrid)w.FindName("ValuesGrid");
        check(scope.Items.Cast<ComboBoxItem>().Any(i => (string)i.Content == "뷰") && scope.Items.Cast<ComboBoxItem>().Any(i => (string)i.Content == "시트"), "UI 대상 뷰·시트 제공");
        void Scope(string name) => scope.SelectedItem = scope.Items.Cast<ComboBoxItem>().Single(i => (string)i.Content == name);
        Scope("뷰"); field.SelectedItem = "뷰 구분-1";
        check(grid.Items.Count == 9 && grid.Items.Cast<ParameterRow>().All(r => r.Scope == "뷰" && r.Name == "뷰 구분-1"), "평면·입면 등 뷰 범위와 실제 필드로 필터");
        check(grid.Columns.OfType<DataGridTextColumn>().Any(c => (c.Binding as Binding)?.Path.Path == "TargetName"), "UI 이름 열 실제 대상명 바인딩");
        grid.SelectedItems.Add(first); ((TextBox)w.FindName("ValueBox")).Text = "건축";
        search.Text = "뷰구분-1";
        check(field.Items.Contains("뷰 구분-1") && field.Items.Contains("뷰구분-1"), "공백 무시 검색은 두 실제 이름 모두 찾기");
        check((string)field.SelectedItem == "뷰 구분-1" && grid.SelectedItems.Contains(first) && ((TextBox)w.FindName("ValueBox")).Text == "건축", "공백 무시 검색도 선택·입력값 보존");
        typeof(MainWindow).GetMethod("StageValues", flags)!.Invoke(w, [w, new RoutedEventArgs()]);
        var edits = (DataGrid)w.FindName("EditsGrid");
        check(edits.Items.Count == 1 && ((ValueEdit)edits.Items[0]).Original.OwnerId == first.OwnerId && ((ValueEdit)edits.Items[0]).Type.Contains(first.ElementName), "UI에서 선택한 실제 뷰만 변경 목록에 추가");
        field.SelectedItem = "뷰구분-1";
        check(grid.Items.Count == 1 && ((ParameterRow)grid.Items[0]).ParameterKey == "id:43", "공백 다른 실제 매개변수는 병합하지 않기");
        field.SelectedItem = "뷰 구분-1";
        ((TextBox)w.FindName("SearchBox")).Text = "지상 2층";
        check(grid.Items.Count == 1 && grid.Items[0] == second, "유형명 아닌 실제 뷰 이름 검색");
        ((TextBox)w.FindName("SearchBox")).Clear();
        foreach (var size in new[] { (1320, 820), (1080, 700) }) render(w, "views-" + size.Item1, size.Item1, size.Item2);
        Scope("시트");
        check(grid.Items.Count == 1 && ((ParameterRow)grid.Items[0]).TargetName == "A-101 · 평면도 도면", "UI 시트 범위와 번호·이름 표시");
        ((TextBox)w.FindName("SearchBox")).Text = "A-101";
        check(grid.Items.Count == 1, "시트 번호로 대상 검색");
        ((TextBox)w.FindName("SearchBox")).Clear(); Scope("전체 범위");
        check(grid.Items.Count == 10 && grid.Items.Cast<ParameterRow>().Any(r => r.Scope == "시트"), "전체 범위에 뷰·시트 함께 포함");
        check(edits.Items.Count == 1, "범위·이름 필터는 준비한 변경 보존");
    }
}
