using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

internal static class LinkRemovalTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject, Action<Window, string, int, int> render)
    {
        LinkRemovalPlan Plan(string path = "검사 A.rvt", bool dependent = false)
        {
            var link = new LinkRemovalItem { UniqueId = "link", ElementId = 1, Revision = "version1", Kind = "Revit 링크 정의", Name = "건축 링크", Root = true, Pinned = true };
            var plan = new LinkRemovalPlan { Model = new() { Path = path, Title = path, CanonicalPath = path, Identity = path }, Targets = [link], Affected = [link] };
            if (dependent) plan.Affected.Add(new() { UniqueId = "tag", ElementId = 2, Revision = "v1", Name = "링크 태그", Kind = "종속" });
            return plan;
        }
        var plan = Plan(); plan.Validate(false); check(plan.DependentCount == 0, "종속 없는 링크 계획 유효");
        var restored = JsonSerializer.Deserialize<LinkRemovalPlan>(JsonSerializer.Serialize(plan))!;
        LinkRemovalPlan.CheckSame(plan.Targets, restored.Targets); check(restored.Model.Identity == plan.Model.Identity, "링크 검사 요청 JSON 왕복");
        restored.Targets[0].Revision = "changed"; reject(() => LinkRemovalPlan.CheckSame(plan.Targets, restored.Targets), "링크 검사 revision 변경 차단");
        plan = Plan(dependent: true); reject(() => plan.Validate(false), "종속 요소 승인 필수"); plan.Validate(true); check(plan.DependentCount == 1, "종속 승인 계획 유효");
        plan.BlockReason = "알 수 없는 참조"; reject(() => plan.Validate(true), "미지원 외부 참조가 있으면 삭제 차단");
        plan = Plan(); plan.Targets.Clear(); reject(() => plan.Validate(true), "빈 링크 목록 삭제 요청 차단");
        plan = Plan(); plan.Affected.Clear(); reject(() => plan.Validate(true), "영향 미검사 계획 차단");
        plan = Plan(); plan.Targets.Add(plan.Targets[0]); reject(() => plan.Validate(true), "중복 링크 삭제 계획 차단");
        plan = Plan(); plan.Affected = [new() { UniqueId = "other", ElementId = 2, Revision = "r" }]; reject(() => plan.Validate(true), "정의가 남는 계획 차단");
        var main = new MainWindow(); var requests = new List<Request>();
        var w = new LinkRemovalWindow(["검사 A.rvt", "검사 B.rvt"], r => { requests.Add(r); return Task.FromResult(new Response { Success = true, LinkRemoval = Plan(r.Path, true) }); }, (_,_) => throw new Exception("삭제를 요청하지 않았습니다")) { Resources = main.Resources };
        var models = ((DataGrid)w.FindName("ModelsGrid")).Items.Cast<LinkRemovalModel>().ToList();
        var delete = (Button)w.FindName("DeleteButton"); var confirm = (TextBox)w.FindName("ConfirmText");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Scan(LinkRemovalWindow win) => ((Task)typeof(LinkRemovalWindow).GetMethod("ScanSelected", flags)!.Invoke(win, null)!).GetAwaiter().GetResult();
        check(models.All(m => !m.Selected) && !delete.IsEnabled, "삭제 대상 모델 기본 미선택");
        models[0].Selected = true; confirm.Text = "모두 삭제"; check(!delete.IsEnabled, "삭제 검사 전 확인문구만으로 실행 불가");
        Scan(w); check(requests.Count == 1 && requests[0].Path == "검사 A.rvt" && requests[0].Operation == "링크 검사" && !requests[0].Confirmed, "체크한 모델만 비저장 검사 요청");
        check(((DataGrid)w.FindName("DetailsGrid")).Items.Count == 2 && models[0].Plan?.DependentCount == 1, "삭제 정의와 종속 요소 모두 목록 표시");
        confirm.Text = "모두 삭제"; check(!delete.IsEnabled, "종속 승인 없이 삭제 버튼 차단");
        ((CheckBox)w.FindName("DependentApproval")).IsChecked = true; check(delete.IsEnabled, "검사·문구·종속 승인 완료 후 삭제 버튼 활성");
        confirm.Text = "삭제"; check(!delete.IsEnabled, "잘못된 확인 문구 차단"); confirm.Text = "모두 삭제";
        foreach (var size in new[] { (1120, 800), (960, 740) })
        { render(w, "link-removal-" + size.Item1, size.Item1, size.Item2); check(((DataGrid)w.FindName("DetailsGrid")).ActualHeight > 100 && delete.ActualHeight >= 30, "삭제 창 최소 목록/버튼 영역 " + size.Item1); }
        models[1].Selected = true; check(models.All(m => m.Plan == null) && !delete.IsEnabled && confirm.Text.Length == 0, "모델 변경 시 이전 삭제 승인 무효화");
        Scan(w); check(requests.Count == 3, "선택한 여러 모델 모두 새 검사");
        check(requests.All(r => r.Operation == "링크 검사") && models.All(m => m.Plan != null), "검사만으로 삭제·저장 요청 없음");
        var same = new LinkRemovalWindow(["중앙.rvt", "로컬.rvt"], r => { var p = Plan(r.Path); p.Model.CentralPath = "같은 중앙.rvt"; return Task.FromResult(new Response { Success = true, LinkRemoval = p }); }, (_, _) => { }) { Resources = main.Resources };
        var dup = ((DataGrid)same.FindName("ModelsGrid")).Items.Cast<LinkRemovalModel>().ToList(); foreach (var m in dup) m.Selected = true; Scan(same);
        check(dup.All(m => m.Plan!.BlockReason.Contains("두 번")), "같은 중앙/로컬 이중 삭제 차단");
        var failed = new LinkRemovalWindow(["실패.rvt"], _ => Task.FromResult(new Response { RequiresAttention = true, Message = "확인 필요" }), (_, _) => { }) { Resources = main.Resources };
        ((LinkRemovalModel)((DataGrid)failed.FindName("ModelsGrid")).Items[0]).Selected = true; Scan(failed);
        check(!((Button)failed.FindName("ScanButton")).IsEnabled && !((Button)failed.FindName("DeleteButton")).IsEnabled, "미확정 응답은 재검사·삭제 잠금");
        check(requests.All(r => r.LinkRemoval == null && r.Edits.Count == 0), "삭제 검사와 매개변수 변경 요청 분리");
    }
}
