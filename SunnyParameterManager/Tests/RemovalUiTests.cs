using System.IO;
using System.Reflection;
using System.Windows.Controls;
using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;

internal static class RemovalUiTests
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var rules = new RuleSet { Version = 5, Name = "정확한 이름 안전 삭제", Spec = new() { Source = "시험", SourceSha256 = new string('a', 64), RemoveOnly = true, RemovalSafetyVersion = 1,
            RemovalTargets = [new() { Name = "건축물 번호", KeepName = "건축물번호", Categories = ["OST_ProjectInformation"] }, new() { Name = "구조", Categories = ["OST_ProjectInformation"] }] } };
        var w = new MainWindow(); w.LoadSample();
        var normal = RulePlanner.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "연습용.sunnyrules.json")));
        w.SelectRules(normal);
        var grid = (DataGrid)w.FindName("ValuesGrid"); grid.SelectedItems.Add(grid.Items[0]);
        ((TextBox)w.FindName("ValueBox")).Text = "기존 작업 보존";
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetMethod("StageValues", flags)!.Invoke(w, [null, new System.Windows.RoutedEventArgs()]);
        var edits = (DataGrid)w.FindName("EditsGrid"); var caption = ((TextBlock)w.FindName("QuerySummary")).Text;
        reject(() => w.SelectRules(rules), "삭제 선택은 이전 일반 변경과 혼합 거부");
        check(edits.Items.Count == 1 && ((TextBlock)w.FindName("QuerySummary")).Text == caption, "삭제 선택 거부 후 이전 변경·선택 룰셋 표시 보존");
        reject(() => w.PrepareRules(rules), "직접 삭제 검토도 이전 변경 혼합 거부");
        check(edits.Items.Count == 1 && ((ValueEdit)edits.Items[0]).Value == "기존 작업 보존", "검토 거부 후 기존 변경 불변");
        w.LoadSample();
        foreach (var model in ((ListBox)w.FindName("ModelsList")).Items.Cast<ModelItem>())
        {
            model.Snapshot!.Definitions = new() { AllCategories = ["OST_ProjectInformation"], Definitions = new[] { "건축물번호", "건축물 번호", "구조" }.Select((name, i) => new ParameterDefinitionInfo
            { Name = name, Key = "id:" + (10 + i), ElementId = (10 + i).ToString(), ElementUniqueId = "uid-" + i, Revision = Guid.NewGuid().ToString("D"), Binding = "인스턴스", DataType = "autodesk.spec:spec.string-2.0.0", Group = SharedParameterAddition.OtherGroup,
                Categories = ["OST_ProjectInformation"], ValueCount = 0, ValueFingerprint = DefinitionRemovalChecks.Fingerprint(new Dictionary<string, string>()) }).ToList() };
        }
        w.SelectRules(rules); w.PrepareRules(rules);
        check(edits.Items.Count == 4 && edits.Items.Cast<ValueEdit>().All(e => e.DefinitionFix!.Removal != null), "실제 창에서 두 모델·두 정의 삭제만 준비");
        check(((TextBlock)w.FindName("QuerySummary")).Text.Contains("삭제 전용 (2개)"), "삭제 선택 완료 표시와 실제 항목 수 일치");
        void Approval()
        {
            try { typeof(MainWindow).GetMethod("ValidateRemovalApproval", flags)!.Invoke(w, null); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException reason) { throw reason; }
        }
        Approval(); check(true, "새 삭제 룰셋·조회와 일치한 적용 승인 검사 통과");
        var first = (ValueEdit)edits.Items[0]; first.DefinitionFix!.Removal!.Source.Name = "다른 이름";
        reject(Approval, "작업 파일에서 바뀐 삭제 근거도 최종 적용 전 차단");
        w.SelectRules(normal);
        reject(Approval, "삭제 룰셋 해제 후 남은 삭제 예정의 최종 적용 차단");
    }
}
