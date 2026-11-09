using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static class ProjectParameterTransferTests
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var query = ProjectParameterTransfer.DefinitionQuery();
        var source = Model("source", query,
            [Def("비고", "", "OST_Walls"), Def("제작정보", "11111111-1111-1111-1111-111111111111", "OST_Walls")]);
        var target = Model("target", query, [Def("비고", "", "OST_Sheets")]);
        var window = new MainWindow();
        try
        {
            window.SelectRules(RulePlanner.Parse("{\"Version\":10,\"Name\":\"프로젝트 매개변수 표준 전송\",\"ProjectParameterTransfer\":true,\"Rules\":[]}"));
            var models = (ObservableCollection<ModelItem>)typeof(MainWindow).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            models.Add(new ModelItem { Path = "source.rvt", Snapshot = source });
            models.Add(new ModelItem { Path = "target.rvt", Snapshot = target });
            var combo = (ComboBox)window.FindName("SourceCombo");
            var grid = (DataGrid)window.FindName("ProjectTransferGrid");
            var button = (Button)window.FindName("ApplyButton");
            combo.SelectedIndex = 0;
            Review(window);
            var row = grid.Items.Cast<ProjectTransferReviewRow>().Single();
            check(grid.Visibility == Visibility.Visible && row.Name == "프로젝트 매개변수" && row.Status == "전송 대상" && button.IsEnabled,
                "정의별 충돌 판단 없이 대상 파일을 기본 표준 전송 대상으로 표시");

            target.Definitions!.Definitions.Add(Def("제작정보", "22222222-2222-2222-2222-222222222222", "OST_Sheets"));
            Review(window);
            check(grid.Items.Cast<ProjectTransferReviewRow>().Count() == 1 && button.IsEnabled,
                "동명 매개변수의 GUID·카테고리 차이를 자체 차단하지 않음");
            combo.SelectedIndex = 1;
            check(grid.Visibility == Visibility.Collapsed && !button.IsEnabled,
                "기준 파일 변경 시 이전 전송 검토 무효화");
        }
        finally { window.Close(); }
    }

    private static void Review(MainWindow window) =>
        typeof(MainWindow).GetMethod("ReviewLoadedRules", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window.FindName("ReviewRulesButton"), new RoutedEventArgs()]);

    private static ParameterDefinitionInfo Def(string name, string guid, string category) => new()
    {
        Name = name, Guid = guid, Key = guid.Length > 0 ? "guid:" + guid : "id:101", ElementId = "101",
        ElementUniqueId = "uid-101", Revision = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        DataType = "autodesk.spec:string.text-2.0.0", Group = "autodesk.parameter.group:text-1.0.0",
        Binding = "인스턴스", Categories = [category]
    };
    private static ModelSnapshot Model(string path, QueryPlan query, List<ParameterDefinitionInfo> definitions) => new()
    {
        Path = path, CanonicalPath = path, Identity = path, Title = path, Query = query,
        Definitions = new DefinitionSnapshot { Definitions = definitions, AllCategories = ["OST_Walls", "OST_Sheets"] }
    };
}
