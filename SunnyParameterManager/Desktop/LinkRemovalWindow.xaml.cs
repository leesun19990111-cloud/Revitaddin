using SunnyParameterManager.Core;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace SunnyParameterManager.Desktop;

public sealed class LinkRemovalModel(string path, Action changed) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    private bool _selected;
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); } }
    private string _status = "검사 전";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public LinkRemovalPlan? Plan { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record LinkRemovalDisplay(string Model, string Kind, string Name, long ElementId, string Category, string Pinned, string Source);

public partial class LinkRemovalWindow : Window
{
    private readonly List<LinkRemovalModel> _models;
    private readonly Func<Request, Task<Response>> _send;
    private readonly Action<string, Response> _removed;
    private bool _ready, _busy, _blocked;
    public LinkRemovalWindow(IEnumerable<string> paths, Func<Request, Task<Response>> send, Action<string, Response> removed)
    {
        _send = send; _removed = removed;
        _models = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => new LinkRemovalModel(p, Invalidate)).ToList();
        InitializeComponent(); ModelsGrid.ItemsSource = _models; _ready = true; UpdateApproval();
    }
    private void Invalidate()
    {
        if (!_ready) return;
        foreach (var m in _models) { m.Plan = null; m.Status = "검사 전"; }
        DetailsGrid.ItemsSource = null; ConfirmText.Clear(); DependentApproval.IsChecked = false;
        SummaryText.Text = "모델 선택이 바뀌었습니다. 삭제 영향을 다시 검사하세요."; UpdateApproval();
    }
    private void SelectAll(object sender, RoutedEventArgs e) { if (!_busy) foreach (var m in _models) m.Selected = true; }
    private void ClearAll(object sender, RoutedEventArgs e) { if (!_busy) foreach (var m in _models) m.Selected = false; }
    private void ApprovalChanged(object sender, RoutedEventArgs e) => UpdateApproval();
    private void ConfirmationChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateApproval();
    private bool CanDelete()
    {
        var chosen = _models.Where(m => m.Selected).ToList();
        return !_busy && !_blocked && chosen.Count > 0 && chosen.All(m => m.Plan != null && m.Plan.BlockReason.Length == 0) &&
            chosen.Sum(m => m.Plan!.Targets.Count) > 0 && ConfirmText.Text == "모두 삭제" &&
            (chosen.Sum(m => m.Plan!.DependentCount) == 0 || DependentApproval.IsChecked == true);
    }
    private void UpdateApproval()
    {
        if (!_ready) return;
        DeleteButton.IsEnabled = CanDelete(); ScanButton.IsEnabled = !_busy && !_blocked && _models.Any(m => m.Selected);
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; ModelsGrid.IsEnabled = ScanActions.IsEnabled = ConfirmText.IsEnabled = DependentApproval.IsEnabled = !busy && !_blocked;
        UpdateApproval();
    }
    private async void ScanClick(object sender, RoutedEventArgs e) => await ScanSelected();
    internal async Task ScanSelected()
    {
        if (_busy || _blocked) return;
        var selected = _models.Where(m => m.Selected).ToList(); if (selected.Count == 0) return;
        Invalidate(); SetBusy(true);
        try
        {
            foreach (var item in selected)
            {
                item.Status = "삭제 영향 검사 중…";
                var response = await _send(new() { Operation = "링크 검사", Path = item.Path });
                LogBox.AppendText($"\n{item.Path}\n{response.Message}\n");
                if (response.RequiresAttention) { _blocked = true; item.Status = "작업용 Revit 확인 필요"; break; }
                if (!response.Success || response.LinkRemoval == null) { item.Status = "검사 실패 · 기록 확인"; continue; }
                var plan = response.LinkRemoval;
                if (!string.Equals(plan.Model.Path, item.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("다른 모델의 검사 결과가 반환되었습니다.");
                if (plan.Targets.Count > 0 && plan.BlockReason.Length == 0) plan.Validate(true);
                item.Plan = plan;
                item.Status = plan.BlockReason.Length > 0 ? "삭제 보류 · 기록 확인" : $"링크·가져오기 {plan.Targets.Count:N0} / 종속 {plan.DependentCount:N0}";
                LogBox.AppendText(plan.BlockReason + "\n" + string.Join("\n", plan.Notices));
            }
            var plans = selected.Where(m => m.Plan != null).ToList();
            foreach (var duplicate in plans.GroupBy(m => m.Plan!.Model.CentralPath.Length > 0 ? m.Plan.Model.CentralPath : m.Plan.Model.CanonicalPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var item in duplicate) { item.Plan!.BlockReason = "같은 중앙/실제 파일을 두 번 선택했습니다. 하나만 선택하고 다시 검사하세요."; item.Status = item.Plan.BlockReason; }
            Present();
        }
        catch (Exception ex) { _blocked = true; LogBox.AppendText("\n검사 중단: " + ex.Message); SummaryText.Text = "검사를 완료하지 못했습니다. 기록과 작업용 Revit을 확인하세요."; }
        finally { SetBusy(false); }
    }
    private void Present()
    {
        var selected = _models.Where(m => m.Selected).ToList();
        var rows = new List<LinkRemovalDisplay>();
        foreach (var m in selected.Where(m => m.Plan != null))
        {
            foreach (var t in m.Plan!.Targets) rows.Add(new(m.Title, t.Kind, t.Name, t.ElementId, t.Category, t.Pinned ? "고정" : "", t.Source));
            var targetIds = m.Plan.Targets.Select(t => t.UniqueId).ToHashSet();
            foreach (var t in m.Plan.Affected.Where(a => !targetIds.Contains(a.UniqueId))) rows.Add(new(m.Title, "함께 삭제되는 종속 요소", t.Name, t.ElementId, t.Category, t.Pinned ? "고정" : "", ""));
        }
        DetailsGrid.ItemsSource = rows;
        SummaryText.Text = $"{selected.Count}개 모델 · 삭제 영향 {rows.Count:N0}개 · 종속 {selected.Sum(m => m.Plan?.DependentCount ?? 0):N0}개" +
            (selected.Any(m => m.Plan == null || m.Plan.BlockReason.Length > 0) ? " · 검사 실패/보류 있음" : " · 아직 저장하지 않았습니다.");
    }
    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (!CanDelete()) return;
        var chosen = _models.Where(m => m.Selected && m.Plan!.Targets.Count > 0).ToList();
        var local = chosen.Any(m => m.Plan!.Model.Kind == "로컬");
        var message = "아래 모델의 모든 링크·가져오기 정의와 승인한 종속 요소를 완전히 삭제하고 저장할까요?\n\n" +
            string.Join("\n", chosen.Select(m => m.Path + " — 삭제 " + m.Plan!.Affected.Count + "개")) +
            "\n\n언로드가 아니며 파일을 다시 열어도 삭제 상태입니다. 외부 원본 파일은 보존합니다. 작업공유 모델은 중앙에 동기화됩니다. 여러 모델 전체를 한 번에 되돌릴 수 없습니다. 복원은 보관된 삭제 전 파일을 검토하여 별도로 진행해야 합니다." +
            (local ? "\n\n주의: 기존 로컬에 저장된 다른 미동기화 작업도 함께 중앙에 반영됩니다." : "");
        if (MessageBox.Show(this, message, "링크 완전 삭제 최종 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true); DetailsTabs.SelectedIndex = 1;
        try
        {
            foreach (var item in chosen)
            {
                var plan = item.Plan!;
                var response = await _send(new() { Operation = "링크 삭제", Path = item.Path, Identity = plan.Model.Identity, CanonicalPath = plan.Model.CanonicalPath,
                    Confirmed = true, DependentsConfirmed = DependentApproval.IsChecked == true, LocalChangesConfirmed = local, LinkRemoval = plan });
                LogBox.AppendText($"\n{item.Path}\n{response.Stage} · {response.Message}\n삭제 전 백업: {response.BackupPath}\n복구 경로: {response.RecoveryPath}\n");
                _removed(item.Path, response); item.Plan = null;
                item.Status = response.Success ? "삭제·저장 완료 · 재검사 필요" : "실패 · 기록 확인";
                if (!response.Success || response.RequiresAttention) { _blocked |= response.RequiresAttention; LogBox.AppendText("나머지 모델은 처리하지 않았습니다.\n"); break; }
            }
            SummaryText.Text = "처리가 끝났습니다. 모델별 결과와 복구 경로를 확인하세요.";
        }
        catch (Exception ex) { _blocked = true; LogBox.AppendText("\n응답 확인 필요: " + ex.Message + "\n자동 재실행하지 마세요."); }
        finally
        {
            foreach (var item in _models) item.Plan = null;
            ConfirmText.Clear(); DependentApproval.IsChecked = false; SetBusy(false);
        }
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; SummaryText.Text = "작업 진행 중에는 창을 닫지 않습니다. 완료 후 결과를 확인하세요.";
    }
}
