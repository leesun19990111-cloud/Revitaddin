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
        var ordinary = Def("뷰 구분-1", "", "id:101", "101", "uid-101", "OST_Views");
        var shared = Def("제작정보", "11111111-1111-1111-1111-111111111111", "guid:11111111-1111-1111-1111-111111111111", "102", "uid-102", "OST_Walls");
        var source = Model("source", query, [ordinary, shared]);
        var target = Model("target", query, []);
        var planned = ProjectParameterTransfer.Plan(source, target);
        check(planned.Count == 2 && planned.All(d => d.Status == "추가 예정"), "일반·공유 프로젝트 매개변수 전체 추가 계획");
        target.Definitions!.Definitions.Add(Def("뷰 구분-1", "", "id:501", "501", "target-501", "OST_Views"));
        planned = ProjectParameterTransfer.Plan(source, target);
        check(planned.Single(d => d.Source.Name == "뷰 구분-1").Status == "이미 있음", "일반 매개변수 동일 정의는 중복 생성하지 않음");
        target.Definitions.Definitions[0].Binding = "유형";
        planned = ProjectParameterTransfer.Plan(source, target);
        check(planned.Single(d => d.Source.Name == "뷰 구분-1").Status == "정의 교체 예정", "일반 매개변수의 바인딩 차이는 기준 정의로 교체");
        target.Definitions.Definitions[0].Binding = "인스턴스";
        target.Definitions.Definitions[0].Categories = ["OST_Sheets"];
        check(ProjectParameterTransfer.Plan(source, target).Single(d => d.Source.Name == "뷰 구분-1").Status == "정의 교체 예정",
            "일반 매개변수의 카테고리 차이는 기준 정의로 교체");
        target.Definitions.Definitions[0].Guid = "22222222-2222-2222-2222-222222222222";
        check(ProjectParameterTransfer.Plan(source, target).Single(d => d.Source.Name == "뷰 구분-1").Status == "정의 교체 예정",
            "동명 GUID 차이는 기준 정의로 교체");
        target.Definitions.Definitions[0].Guid = "";
        target.Definitions.Definitions[0].DataType = "autodesk.spec:spec.int-2.0.0";
        check(ProjectParameterTransfer.Plan(source, target).Single(d => d.Source.Name == "뷰 구분-1").Status == "정의 교체 예정",
            "동명 자료형 차이는 기준 정의로 교체");
        target.Definitions.Definitions[0].DataType = ordinary.DataType;
        target.Definitions.AllCategories.Remove("OST_Views");
        check(ProjectParameterTransfer.Plan(source, target).Single(d => d.Source.Name == "뷰 구분-1").Status == "충돌",
            "대상에 없는 카테고리는 임의로 버리지 않음");
        target.Definitions.AllCategories.Add("OST_Views");
        var sharedTarget = Model("shared-target", query,
            [Def("제작정보", shared.Guid, shared.Key, "901", "shared-target-901", "OST_Sheets")]);
        check(ProjectParameterTransfer.Plan(source, sharedTarget).Single(d => d.Source.Name == "제작정보").Status == "설정 변경 예정",
            "공유 매개변수의 카테고리 차이는 연결 설정 변경");
        var groupTarget = Model("group-target", query,
            [Def("뷰 구분-1", "", "id:902", "902", "group-target-902", "OST_Views")]);
        groupTarget.Definitions!.Definitions[0].Group = "autodesk.parameter.group:identityData-1.0.0";
        check(ProjectParameterTransfer.Plan(source, groupTarget).Single(d => d.Source.Name == "뷰 구분-1").Status == "설정 변경 예정",
            "일반 매개변수의 그룹만 다르면 제자리 변경");
        source.Definitions!.Definitions.Add(Def("뷰 구분-1", "", "id:103", "103", "uid-103", "OST_Views"));
        check(ProjectParameterTransfer.Plan(source, Model("third", query, [])).Any(d => d.Status == "충돌"), "원본 동명 중복 차단");
        source.Definitions.Definitions.RemoveAt(2);
        var copy = Model("copy", query, []);
        copy.Identity = source.Identity;
        reject(() => ProjectParameterTransfer.Plan(source, copy), "동일 모델에 자기 전송 차단");
        var fresh = Def("뷰 구분-1", "", "id:101", "101", "uid-101", "OST_Views");
        check(ProjectParameterTransfer.SameSnapshot(new DefinitionSnapshot { Definitions = [ordinary] }, new DefinitionSnapshot { Definitions = [fresh] }), "정의 조회 근거 일치");
        fresh.Categories = ["OST_Sheets"];
        check(!ProjectParameterTransfer.SameSnapshot(new DefinitionSnapshot { Definitions = [ordinary] }, new DefinitionSnapshot { Definitions = [fresh] }), "조회 뒤 카테고리 변경 차단");

        var window = new MainWindow();
        try
        {
            window.SelectRules(RulePlanner.Parse("{\"Version\":10,\"Name\":\"프로젝트 매개변수 표준 전송\",\"ProjectParameterTransfer\":true,\"Rules\":[]}"));
            var models = (ObservableCollection<ModelItem>)typeof(MainWindow).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            models.Add(new ModelItem { Path = "source.rvt", Snapshot = source });
            models.Add(new ModelItem { Path = "target.rvt", Snapshot = Model("target", query, []) });
            var combo = (ComboBox)window.FindName("SourceCombo");
            var grid = (DataGrid)window.FindName("ProjectTransferGrid");
            var button = (Button)window.FindName("ApplyButton");
            combo.SelectedIndex = 0;
            typeof(MainWindow).GetMethod("ReviewLoadedRules", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window.FindName("ReviewRulesButton"), new RoutedEventArgs()]);
            check(grid.Visibility == Visibility.Visible && grid.Items.Count == 2,
                "기존 기준 파일 선택 후 같은 창에서 모든 정의 검토");
            check(button.IsEnabled, "전송 검토 후 기존 저장 버튼 활성화");
            models[1].Snapshot = Model("target", query,
                [Def("뷰 구분-1", "", "id:503", "503", "target-503", "OST_Sheets"),
                 Def("제작정보", shared.Guid, shared.Key, "504", "target-504", "OST_Walls")]);
            typeof(MainWindow).GetMethod("ReviewLoadedRules", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window.FindName("ReviewRulesButton"), new RoutedEventArgs()]);
            check(grid.Items.Cast<ProjectTransferReviewRow>().Any(r => r.Status == "정의 교체 예정") && button.IsEnabled,
                "추가 없이 정의 교체만 있어도 전송 버튼 활성화");
            combo.SelectedIndex = 1;
            check(grid.Visibility == Visibility.Collapsed && !button.IsEnabled,
                "기준 파일 변경 시 이전 전송 승인 무효화");
        }
        finally { window.Close(); }
    }

    private static ParameterDefinitionInfo Def(string name, string guid, string key, string id, string uid, string category) => new()
    {
        Name = name, Guid = guid, Key = key, ElementId = id, ElementUniqueId = uid, Revision = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        DataType = "autodesk.spec:string.text-2.0.0", Group = "autodesk.parameter.group:text-1.0.0", Binding = "인스턴스", Categories = [category]
    };
    private static ModelSnapshot Model(string path, QueryPlan query, List<ParameterDefinitionInfo> defs) => new()
    {
        Path = path, CanonicalPath = path, Identity = path, Title = path, Query = query,
        Definitions = new DefinitionSnapshot { Definitions = defs, AllCategories = ["OST_Views", "OST_Walls", "OST_Sheets"] }
    };
}
