using Microsoft.Win32;
using SunnyParameterManager.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SunnyParameterManager.Desktop;

public sealed class ModelItem : INotifyPropertyChanged
{
    public string Path { get; set; } = "";
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    private string _status = "조회 전";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public ModelSnapshot? Snapshot { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class WorkspaceFile
{
    public int Version { get; set; } = 1;
    public bool Sample { get; set; }
    public List<ModelSnapshot> Models { get; set; } = [];
    public List<string> Paths { get; set; } = [];
    public List<ValueEdit> Edits { get; set; } = [];
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ModelItem> _models = [];
    private readonly Dictionary<string, ValueEdit> _edits = [];
    private readonly WorkerClient _worker = new();
    private bool _initialized, _filtering, _busy, _sample, _selecting;

    public MainWindow()
    {
        InitializeComponent();
        ModelsList.ItemsSource = _models; SourceCombo.ItemsSource = _models;
        _worker.Progress = text => StatusText.Text = text;
        _initialized = true;
    }

    private void Guard(Action action)
    {
        try { if (_busy) throw new InvalidOperationException("진행 중인 작업이 끝난 뒤 다시 시도하세요."); action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "Sunny 매개변수 관리자", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; WorkspacePanel.IsEnabled = false; ApplyButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; ReportBox.AppendText("\n" + DateTime.Now.ToString("HH:mm:ss") + " · " + ex.Message + "\n"); MessageBox.Show(this, ex.Message, "Sunny 매개변수 관리자", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; WorkspacePanel.IsEnabled = true; UpdatePending(); }
    }
    private bool DiscardPending()
    {
        return _edits.Count == 0 || MessageBox.Show(this, "아직 적용하지 않은 변경 예정 목록을 버릴까요? 작업 저장으로 따로 보관할 수 있습니다.", "변경 예정 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void AddModels(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Revit 모델 (*.rvt)|*.rvt", Multiselect = true, Title = "매개변수를 읽고 수정할 모델 선택" };
        if (dialog.ShowDialog(this) != true) return;
        if (_sample) { if (!DiscardPending()) return; _edits.Clear(); _models.Clear(); _sample = false; }
        foreach (var path in dialog.FileNames)
            if (_models.All(m => !string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) _models.Add(new() { Path = path });
        if (SourceCombo.SelectedIndex < 0) SourceCombo.SelectedIndex = 0;
        RefreshFields(); UpdatePending();
    });
    private void RemoveModel(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (ModelsList.SelectedItem is not ModelItem item) return;
        var count = _edits.Values.Count(v => v.Original.ModelPath == item.Path);
        if (count > 0 && MessageBox.Show(this, $"이 모델의 변경 예정 {count}건도 제거할까요?", "모델 제거", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var key in _edits.Where(p => p.Value.Original.ModelPath == item.Path).Select(p => p.Key).ToList()) _edits.Remove(key);
        _models.Remove(item); RefreshFields(); UpdatePending();
    });

    private async void ConnectWorker(object sender, RoutedEventArgs e) => await Run(async () => await _worker.Connect(RevitPathBox.Text.Trim()));
    private async void StopWorker(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var result = await _worker.Send(new Request { Operation = "종료" });
        StatusText.Text = result.Message;
        if (!result.Success) throw new InvalidOperationException(result.Message);
    });
    private async void ReadModels(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_sample) { StatusText.Text = "샘플에서는 실제 RVT를 조회하지 않습니다. RVT 추가로 실제 작업을 시작하세요."; return; }
        if (_edits.Count > 0) { MessageBox.Show(this, "변경 예정 목록이 있습니다. 적용하거나 초기화한 뒤 새로 조회하세요.", "미리보기 보호"); return; }
        await Run(async () =>
        {
            if (_models.Count == 0) throw new InvalidOperationException("먼저 RVT 파일을 추가하세요.");
            await _worker.Connect(RevitPathBox.Text.Trim());
            foreach (var item in _models)
            {
                item.Snapshot = null; item.Status = "조회 중…";
                var result = await _worker.Send(new Request { Operation = "조회", Path = item.Path });
                if (result.Success && result.Snapshot != null)
                {
                    var snapshot = result.Snapshot;
                    var duplicate = _models.Where(m => m != item && m.Snapshot != null).Any(m => string.Equals(ModelIdentityPath(m.Snapshot!), ModelIdentityPath(snapshot), StringComparison.OrdinalIgnoreCase));
                    if (duplicate) item.Status = "제외 · 같은 중앙/실제 파일이 이미 등록되어 있습니다.";
                    else { item.Snapshot = snapshot; item.Status = $"{snapshot.Kind} · {snapshot.Rows.Count:N0}개 값"; }
                    ReportBox.AppendText("\n" + item.Title + " · " + result.Message + "\n" + string.Join("\n", snapshot.Notices) + "\n");
                }
                else { item.Status = "조회 실패"; ReportBox.AppendText("\n" + item.Title + " · " + result.Message + "\n"); }
                if (result.RequiresAttention) break;
            }
            RefreshFields(); StatusText.Text = "조회 완료 · 실패/제외 사유는 처리 결과 탭에서 확인하세요.";
        });
    }
    private static string ModelIdentityPath(ModelSnapshot s) => s.CentralPath.Length > 0 ? s.CentralPath : s.CanonicalPath;

    private IEnumerable<ParameterRow> AllRows() => _models.Where(m => m.Snapshot != null).SelectMany(m => m.Snapshot!.Rows);
    private void RefreshFields()
    {
        if (!_initialized) return;
        _filtering = true;
        var current = FieldCombo.SelectedItem as string;
        var names = AllRows().Select(r => r.Name).Distinct().OrderBy(s => s).ToList();
        FieldCombo.ItemsSource = names;
        FieldCombo.SelectedItem = current != null && names.Contains(current) ? current : names.Contains("재료명") ? "재료명" : names.FirstOrDefault();
        _filtering = false; RefreshRows();
    }
    private void RefreshRows()
    {
        if (!_initialized || _filtering) return;
        var scope = (ScopeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "전체 범위";
        var field = FieldCombo.SelectedItem as string;
        var search = SearchBox.Text.Trim();
        var model = ModelsList.SelectedItem as ModelItem;
        var rows = AllRows().Where(r => (model == null || r.ModelPath == model.Path) && (scope == "전체 범위" || r.Scope == scope) && (field == null || r.Name == field));
        if (search.Length > 0) rows = rows.Where(r => new[] { r.ModelTitle, r.Category, r.Family, r.TypeName, r.Display }.Any(s => s.Contains(search, StringComparison.CurrentCultureIgnoreCase)));
        var visible = rows.OrderBy(r => r.ModelTitle).ThenBy(r => r.Family).ThenBy(r => r.TypeName).ThenBy(r => r.ElementId).ToList();
        ValuesGrid.ItemsSource = visible;
        CountText.Text = $"{visible.Count:N0}행 · 편집 가능 {visible.Count(r => r.Editable):N0}행";
        UpdateEditor();
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (_initialized) RefreshRows(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_initialized) RefreshRows(); }
    private void ModelSelectionChanged(object sender, SelectionChangedEventArgs e) { if (_initialized) RefreshRows(); }
    private void ShowAllModels(object sender, RoutedEventArgs e) => Guard(() => { ModelsList.SelectedItem = null; RefreshRows(); });
    private void SelectEditable(object sender, RoutedEventArgs e) => Guard(() =>
    {
        _selecting = true;
        try { ValuesGrid.SelectedItems.Clear(); foreach (ParameterRow r in ValuesGrid.Items) if (r.Editable) ValuesGrid.SelectedItems.Add(r); }
        finally { _selecting = false; UpdateEditor(); }
    });
    private void ClearSelection(object sender, RoutedEventArgs e) => Guard(() => ValuesGrid.SelectedItems.Clear());
    private void ValuesSorting(object sender, DataGridSortingEventArgs e) { /* WPF 기본 열 정렬과 열 너비 조절을 유지한다. */ }
    private void ValueSelectionChanged(object sender, SelectionChangedEventArgs e) { if (_initialized && !_selecting) UpdateEditor(); }
    private void UpdateEditor()
    {
        var selected = ValuesGrid.SelectedItems.Cast<ParameterRow>().ToList();
        var choice = selected.Count > 0 && selected.All(r => r.Editor is "선택" or "예/아니요");
        ChoiceCombo.Visibility = choice ? Visibility.Visible : Visibility.Collapsed;
        ValueBox.Visibility = choice ? Visibility.Collapsed : Visibility.Visible;
        if (choice)
        {
            var names = selected[0].Choices.Select(c => c.Name).ToHashSet();
            foreach (var r in selected.Skip(1)) names.IntersectWith(r.Choices.Select(c => c.Name));
            ChoiceCombo.ItemsSource = names.OrderBy(s => s).ToList();
        }
        EditorHint.Text = selected.Count == 0 ? "행을 선택하세요. Ctrl·Shift로 여러 행을 선택하고, 머리글을 눌러 정렬할 수 있습니다." :
            $"선택 {selected.Count:N0}행 · {string.Join(", ", selected.Select(r => r.Editor.Length == 0 ? "표시 전용" : r.Editor).Distinct())} · " +
            (selected.Any(r => r.Editor == "단위 숫자") ? "숫자는 단위까지 입력하세요(예: 200 mm). 단위 생략 시 각 모델 단위로 해석됩니다." : "값을 비우면 텍스트를 지웁니다. 아직 RVT에는 반영되지 않습니다.");
    }
    private void StageValues(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var selected = ValuesGrid.SelectedItems.Cast<ParameterRow>().ToList();
        if (selected.Select(r => r.FieldKey).Distinct().Count() > 1) throw new InvalidOperationException("같은 이름·자료형·범위의 매개변수만 함께 입력하세요.");
        var value = ChoiceCombo.Visibility == Visibility.Visible ? ChoiceCombo.Text : ValueBox.Text;
        var planned = EditPlanner.Stage(selected, value);
        foreach (var row in selected) _edits.Remove(row.Key);
        foreach (var edit in planned) _edits[edit.Original.Key] = edit;
        UpdatePending(); StatusText.Text = $"{planned.Count:N0}건을 변경 예정에 추가했습니다. 최종 적용 전에는 파일을 수정하지 않습니다.";
    });
    private void CopyValues(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (SourceCombo.SelectedItem is not ModelItem source || source.Snapshot == null) throw new InvalidOperationException("조회가 완료된 기준 모델을 선택하세요.");
        var selected = ValuesGrid.SelectedItems.Cast<ParameterRow>().ToList();
        if (selected.Count == 0) throw new InvalidOperationException("값을 받을 대상 행을 선택하세요.");
        var changes = EditPlanner.CopyTypeValues(source.Snapshot, selected, ApproveNamed.IsChecked == true);
        foreach (var row in selected) _edits.Remove(row.Key);
        foreach (var edit in changes) _edits[edit.Original.Key] = edit;
        UpdatePending(); MainTabs.SelectedIndex = 1; StatusText.Text = $"기준 모델의 현재 조회값으로 {changes.Count:N0}건 준비했습니다. 아직 적용하지 않았습니다.";
    });
    private void RemoveEdits(object sender, RoutedEventArgs e) => Guard(() => { foreach (var edit in EditsGrid.SelectedItems.Cast<ValueEdit>().ToList()) _edits.Remove(edit.Original.Key); UpdatePending(); });
    private void ClearEdits(object sender, RoutedEventArgs e) => Guard(() => { if (!DiscardPending()) return; _edits.Clear(); UpdatePending(); });
    private void UpdatePending()
    {
        EditsGrid.ItemsSource = _edits.Values.OrderBy(e => e.Model).ThenBy(e => e.Type).ThenBy(e => e.Field).ToList();
        PendingText.Text = $"변경 예정 {_edits.Count:N0}건 / {_edits.Values.Select(e => e.Original.ModelPath).Distinct().Count()}개 모델" + (_sample ? " · 샘플: 실제 적용 불가" : " · 최종 적용 전에는 파일을 수정하지 않습니다.");
        ApplyButton.IsEnabled = !_busy && !_sample && _edits.Count > 0 && !_worker.NeedsAttention;
    }
    private async void ApplyChanges(object sender, RoutedEventArgs e)
    {
        if (_busy || _sample || _edits.Count == 0) return;
        MainTabs.SelectedIndex = 1;
        var groups = _edits.Values.GroupBy(e => e.Original.ModelPath).ToList();
        var local = groups.Any(g => _models.Single(m => m.Path == g.Key).Snapshot?.Kind == "로컬");
        var summary = string.Join("\n", groups.Select(g => $"{Path.GetFileName(g.Key)}: {g.Count():N0}건"));
        var text = "아래 변경을 실제 RVT에 저장하고, 작업공유 모델은 중앙에 동기화할까요?\n\n" + summary +
            "\n\n모델별 순차 처리이며 여러 모델을 한 번에 되돌릴 수 없습니다. 유형 값은 그 유형을 사용하는 모든 요소에 영향을 줍니다. 작업세트/치수/재료 변경은 모델 동작에 영향을 줄 수 있습니다.\n운영 모델 적용 전 별도 테스트 중앙에서 검증해 주세요." +
            (local ? "\n\n주의: 기존 로컬에 저장된 다른 미동기화 작업도 중앙에 함께 반영됩니다." : "");
        if (MessageBox.Show(this, text, "실제 파일 변경 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            await _worker.Connect(RevitPathBox.Text.Trim());
            MainTabs.SelectedIndex = 2;
            int completed = 0;
            foreach (var group in groups)
            {
                var item = _models.Single(m => m.Path == group.Key);
                var snapshot = item.Snapshot ?? throw new InvalidOperationException("대상 모델을 다시 조회하세요.");
                var result = await _worker.Send(new Request { Operation = "적용", Path = item.Path, Identity = snapshot.Identity, CanonicalPath = snapshot.CanonicalPath, Confirmed = true, LocalChangesConfirmed = local, Edits = group.ToList() });
                ReportBox.AppendText($"\n[{DateTime.Now:HH:mm:ss}] {item.Title}\n{result.Stage} · {result.Message}\n작업/복구 경로: {result.RecoveryPath}\n변경 전 파일 백업: {result.BackupPath}\n");
                if (result.Success)
                {
                    foreach (var edit in group) _edits.Remove(edit.Original.Key);
                    item.Status = "반영 완료 · 재조회 필요"; item.Snapshot = null; completed++;
                }
                else item.Status = result.RequiresAttention ? "반영 여부 확인 필요" : "적용 실패";
                if (!result.Success || result.RequiresAttention)
                {
                    ReportBox.AppendText($"나머지 {groups.Count - groups.IndexOf(group) - 1}개 모델은 처리하지 않았습니다.\n"); break;
                }
            }
            RefreshFields(); StatusText.Text = $"완료 {completed}/{groups.Count}개 모델 · 처리 결과를 확인하세요. 성공한 모델은 다시 조회할 수 있습니다.";
        });
    }

    private void SaveWorkspace(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = new SaveFileDialog { Filter = "Sunny 작업 파일 (*.spm.json)|*.spm.json", FileName = "매개변수_작업.spm.json" };
        if (dialog.ShowDialog(this) != true) return;
        Protocol.Write(dialog.FileName, new WorkspaceFile { Sample = _sample, Paths = _models.Select(m => m.Path).ToList(), Models = _models.Where(m => m.Snapshot != null).Select(m => m.Snapshot!).ToList(), Edits = _edits.Values.ToList() });
        StatusText.Text = "조회값과 변경 예정 목록을 저장했습니다. 이 파일에는 모델 경로와 매개변수 값이 포함됩니다.";
    });
    private void LoadWorkspace(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!DiscardPending()) return;
        var dialog = new OpenFileDialog { Filter = "Sunny 작업 파일 (*.spm.json)|*.spm.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 256 * 1024 * 1024) throw new InvalidDataException("작업 파일은 256MB 이하만 지원합니다.");
        var file = Protocol.Read<WorkspaceFile>(dialog.FileName);
        if (file.Version != 1) throw new InvalidDataException("지원하지 않는 작업 파일 버전입니다.");
        if (file.Models.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Models.Count ||
            file.Models.Any(m => !file.Paths.Contains(m.Path, StringComparer.OrdinalIgnoreCase) || m.Rows.Any(r => r.ModelPath != m.Path)))
            throw new InvalidDataException("작업 파일의 모델 연결이 일치하지 않습니다.");
        var rows = file.Models.SelectMany(m => m.Rows).ToDictionary(r => r.Key);
        foreach (var edit in file.Edits) if (!rows.ContainsKey(edit.Original.Key)) throw new InvalidDataException("변경 대상이 조회 목록에 없습니다.");
        _models.Clear(); _edits.Clear(); _sample = file.Sample;
        foreach (var path in file.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = file.Models.SingleOrDefault(m => m.Path == path); snapshot?.ConnectCatalogs();
            _models.Add(new() { Path = path, Snapshot = snapshot, Status = snapshot == null ? "조회 전" : "저장된 조회값 · 적용 전 자동 재검증" });
        }
        foreach (var edit in file.Edits) { edit.Original = rows[edit.Original.Key]; _edits[edit.Original.Key] = edit; }
        SourceCombo.SelectedIndex = 0; RefreshFields(); UpdatePending(); StatusText.Text = "작업 파일을 불러왔습니다. 실제 파일은 변경하지 않았습니다.";
    });
    private void OpenLogs(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (_worker.SessionFolder.Length == 0) throw new InvalidOperationException("아직 작업 세션이 없습니다.");
        Process.Start(new ProcessStartInfo(_worker.SessionFolder) { UseShellExecute = true });
    });
    private void SampleClick(object sender, RoutedEventArgs e) => Guard(() => { if (DiscardPending()) LoadSample(); });
    public void LoadSample()
    {
        _edits.Clear(); _models.Clear(); _sample = true;
        for (int n = 1; n <= 2; n++)
        {
            var m = new ModelSnapshot { Path = $"샘플_건축_{n}.rvt", Title = $"샘플_건축_{n}", Kind = "샘플", Identity = "sample" };
            for (int i = 0; i < 18; i++)
                foreach (var name in new[] { "재료명", "업체명", "조달청표준공사코드", "다음으로 IFC로 내보내기" })
                {
                    var value = name == "재료명" ? i % 3 == 0 ? "콘크리트" : i % 3 == 1 ? "석고보드" : "금속 패널" : name == "업체명" ? "미지정" : name.StartsWith("다음") ? "IfcWall" : "A-" + (100 + i);
                    m.Rows.Add(new() { ModelPath = m.Path, ModelTitle = m.Title, OwnerId = "sample-" + i, ElementId = (10000 + i).ToString(), Category = "벽", CategoryKey = "walls", Family = "기본 벽", TypeName = $"KAC-W{i + 1:00}_벽체_{100 + i * 10}", Name = name, ParameterKey = "guid:sample-" + name, DataType = "text", Storage = "String", Editor = "텍스트", HasValue = true, Raw = value, Display = value });
                }
            m.ConnectCatalogs(); _models.Add(new() { Path = m.Path, Snapshot = m, Status = "샘플 데이터 · 파일 변경 없음" });
        }
        ModelsList.SelectedItem = null; SourceCombo.SelectedIndex = 0; ScopeCombo.SelectedIndex = 0;
        RefreshFields(); UpdatePending(); StatusText.Text = "샘플 모드 · 선택·입력·기준값 복사·작업 저장을 시험할 수 있습니다. 실제 RVT에는 적용되지 않습니다.";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; MessageBox.Show(this, "파일 작업 중에는 창을 닫을 수 없습니다. 작업용 Revit의 안내 창이 있다면 확인하세요."); return; }
        if (!DiscardPending()) { e.Cancel = true; return; }
        if (_worker.Connected) MessageBox.Show(this, "작업용 Revit은 강제 종료하지 않습니다. 작업/복구 상태를 확인한 뒤 별도로 종료해 주세요.", "작업용 Revit 안내");
    }
}
