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
    public bool ReadFailed { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class WorkspaceFile
{
    public int Version { get; set; } = 1;
    public bool Sample { get; set; }
    public List<ModelSnapshot> Models { get; set; } = [];
    public List<string> Paths { get; set; } = [];
    public List<ValueEdit> Edits { get; set; } = [];
    public RuleSet? LoadedRules { get; set; }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ModelItem> _models = [];
    private readonly Dictionary<string, ValueEdit> _edits = [];
    private readonly WorkerClient _worker = new();
    private List<string> _fieldNames = [];
    private RuleSet? _loadedRules;
    private bool _initialized, _filtering, _busy, _sample, _selecting;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Sunny · 매개변수 관리자 " + Protocol.Release;
        ModelsList.ItemsSource = _models; SourceCombo.ItemsSource = _models;
        _worker.Progress = text => StatusText.Text = text;
        _initialized = true;
        UpdatePending();
    }

    private void Guard(Action action)
    {
        try { if (_busy) throw new InvalidOperationException("진행 중인 작업이 끝난 뒤 다시 시도하세요."); action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "Sunny 매개변수 관리자", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        SetBusyState(true);
        try { await action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; ReportBox.AppendText("\n" + DateTime.Now.ToString("HH:mm:ss") + " · " + ex.Message + "\n"); MessageBox.Show(this, ex.Message, "Sunny 매개변수 관리자", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusyState(false); RefreshRows(); UpdatePending(); }
    }
    private void SetBusyState(bool busy)
    {
        _busy = busy;
        // 목록 자체는 활성 상태로 두어 휠·스크롤바·키보드 탐색을 허용한다.
        // 파일/룰셋 변경과 입력·저장은 조회 중에도 계속 잠근다.
        TopActions.IsEnabled = MainTabs.IsEnabled = FileActions.IsEnabled = WelcomePanel.IsEnabled = ShowAllModelsButton.IsEnabled = !busy;
        if (busy) { ApplyButton.IsEnabled = false; ReviewButton.IsEnabled = false; }
    }
    private bool DiscardPending()
    {
        return _edits.Count == 0 || MessageBox.Show(this, "아직 적용하지 않은 변경 예정 목록을 버릴까요? 작업 저장으로 따로 보관할 수 있습니다.", "변경 예정 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async void AddModels(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        List<string> added = [];
        Guard(() =>
        {
        var dialog = new OpenFileDialog { Filter = "Revit 모델 (*.rvt)|*.rvt", Multiselect = true, Title = "매개변수를 읽고 수정할 모델 선택" };
        if (dialog.ShowDialog(this) != true) return;
        added = RegisterModels(dialog.FileNames);
        });
        if (added.Count > 0) await ReadRegisteredModels(true, added);
    }
    private async void AddModelsFromFolders(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        List<string> added = [];
        Guard(() =>
        {
            var dialog = new FolderSearchWindow(_sample ? [] : _models.Select(m => m.Path)) { Owner = this, Resources = Resources };
            if (dialog.ShowDialog() == true) added = RegisterModels(dialog.SelectedPaths);
        });
        if (added.Count > 0) await ReadRegisteredModels(true, added);
    }
    private List<string> RegisterModels(IEnumerable<string> paths)
    {
        var selected = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count == 0) return [];
        if (selected.Any(p => !string.Equals(Path.GetExtension(p), ".rvt", StringComparison.OrdinalIgnoreCase) || !File.Exists(p)))
            throw new InvalidOperationException("존재하지 않거나 RVT가 아닌 파일이 있습니다. 파일을 다시 선택하세요.");
        if (_sample) { if (!DiscardPending()) return []; _edits.Clear(); _models.Clear(); _sample = false; _loadedRules = null; RuleSummaryPanel.Visibility = Visibility.Collapsed; }
        List<string> added = [];
        foreach (var path in selected)
            if (_models.All(m => !string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) { _models.Add(new() { Path = path }); added.Add(path); }
        if (SourceCombo.SelectedIndex < 0) SourceCombo.SelectedIndex = 0;
        ModelsList.SelectedItem = null; MainTabs.SelectedIndex = 0;
        RefreshFields(); UpdatePending(); return added;
    }
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
        if (_worker.NeedsAttention && !_worker.CanRestartForRead) { MessageBox.Show(this, "작업 완료 여부 확인이 필요합니다. 작업용 Revit과 처리 결과를 먼저 확인하세요. 기존 변경 예정은 보존합니다."); return; }
        var failed = _models.Where(m => m.Snapshot?.RequiresRequery == true).ToList();
        if (failed.Count > 0)
        {
            var names = string.Join("\n", failed.Select(m => m.Title));
            if (MessageBox.Show(this, "실패 원인을 원본 모델에서 확인했나요?\n아래 파일의 미적용 변경만 비우고 다시 조회합니다. 다른 파일의 변경 예정은 유지합니다. 필요하면 먼저 ‘작업 저장’으로 보관하세요.\n\n" + names,
                "실패 파일 다시 조회", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            ResetFailedModelsForRequery();
            await ReadRegisteredModels(true);
            return;
        }
        if (_edits.Count > 0) { MessageBox.Show(this, "변경 예정 목록이 있습니다. 적용하거나 초기화한 뒤 새로 조회하세요.", "미리보기 보호"); return; }
        await ReadRegisteredModels(false);
    }
    private void ResetFailedModelsForRequery()
    {
        foreach (var item in _models.Where(m => m.Snapshot?.RequiresRequery == true))
        {
            foreach (var key in _edits.Where(p => string.Equals(p.Value.Original.ModelPath, item.Path, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList()) _edits.Remove(key);
            item.Snapshot = null; item.Status = "재조회 전";
        }
        RefreshFields(); UpdatePending();
    }
    private async void ContinueUnreadModels(object sender, RoutedEventArgs e)
    {
        if (_busy || _sample) return;
        var paths = _models.Where(m => m.Snapshot == null && !m.ReadFailed).Select(m => m.Path).ToList();
        if (paths.Count == 0) { MessageBox.Show(this, "아직 시도하지 않은 파일이 없습니다. 실패한 파일은 원인을 확인한 뒤 ‘다시 조회’를 사용하세요."); return; }
        await ReadRegisteredModels(true, paths);
    }
    private async Task ReadRegisteredModels(bool onlyUnread, IReadOnlyCollection<string>? selectedPaths = null)
    {
        bool completed = false;
        await Run(async () =>
        {
            if (_models.Count == 0) throw new InvalidOperationException("먼저 RVT 파일을 추가하세요.");
            var targets = _models.Where(m => (!onlyUnread || m.Snapshot == null) && (selectedPaths == null || selectedPaths.Contains(m.Path, StringComparer.OrdinalIgnoreCase))).ToList();
            if (targets.Count == 0) { StatusText.Text = "선택한 파일은 이미 읽었습니다."; return; }
            StatusText.Text = "파일을 읽을 준비 중입니다. 처음에는 Revit 시작에 몇 분 걸릴 수 있습니다.";
            if (_worker.NeedsAttention)
            {
                if (!_worker.CanRestartForRead) throw new InvalidOperationException("작업 완료 여부를 확인할 수 없어 조회를 멈췄습니다. 작업용 Revit과 처리 결과를 확인하세요. 기존 조회 결과와 변경 예정은 유지됩니다.");
                if (MessageBox.Show(this, $"조회용 연결을 새로 시작하고 선택한 {targets.Count}개 파일을 조회할까요?\n기존 조회 결과와 변경 예정은 유지합니다. 실패 파일을 자동 재시도하지 않습니다.", "조회 연결 다시 시작", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                await _worker.RestartForReadFailure(RevitPathBox.Text.Trim());
            }
            else await _worker.Connect(RevitPathBox.Text.Trim());
            var byPath = targets.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
            var batch = await ReadBatch.Run(targets.Select(m => m.Path).ToList(), async path =>
            {
                var item = byPath[path];
                item.Snapshot = null; item.ReadFailed = false; item.Status = "조회 중…";
                return await _worker.Send(new Request { Operation = "조회", Path = path, Query = _loadedRules == null ? null : QueryPlan.FromRules(_loadedRules) });
            }, (path, result) =>
            {
                var item = byPath[path];
                if (result.Success && result.Snapshot != null)
                {
                    var snapshot = result.Snapshot;
                    var duplicate = _models.Where(m => m != item && m.Snapshot != null).Any(m => string.Equals(ModelIdentityPath(m.Snapshot!), ModelIdentityPath(snapshot), StringComparison.OrdinalIgnoreCase));
                    if (duplicate) { item.ReadFailed = true; item.Status = "제외 · 같은 중앙/실제 파일이 이미 등록되어 있습니다."; }
                    else { item.Snapshot = snapshot; item.Status = $"{snapshot.Kind} · {(snapshot.Query == null ? "전체" : "룰셋 선택")} 조회 · {snapshot.Rows.Count:N0}개 값"; }
                    ReportBox.AppendText("\n" + item.Title + " · " + result.Message + "\n" + string.Join("\n", snapshot.Notices) + "\n");
                }
                else { item.ReadFailed = true; item.Status = "조회 실패 · 건너뜀"; ReportBox.AppendText("\n" + item.Title + " · " + result.Message + "\n"); }
            }, failure => MessageBox.Show(this,
                $"파일을 조회하지 못했습니다.\n\n{System.IO.Path.GetFileName(failure.Path)}\n{failure.Message}\n\n실패한 파일은 건너뛰고 나머지 {failure.Remaining}개 파일을 계속 조회할까요?" +
                (failure.RestartRequired ? "\n\n계속하면 빈 작업용 Revit을 정상 종료하고 새로 연결합니다." : "") +
                "\n‘아니요’를 눌러도 기존 결과와 변경 예정은 유지합니다. 이후 ‘파일 관리 → 미조회 파일 계속 조회’로 이어갈 수 있습니다.",
                "조회 실패 · 계속 진행 확인", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes,
                () => _worker.RestartForReadFailure(RevitPathBox.Text.Trim()), message =>
                {
                    ReportBox.AppendText("\n" + message + "\n");
                    MessageBox.Show(this, message, "조회 중단 · 확인 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            RefreshFields();
            StatusText.Text = batch.Blocked ? $"조회 중단 · 작업용 Revit 확인 필요. 남은 파일 {batch.Remaining}개와 기존 결과·변경 예정은 유지됩니다." : _models.All(m => m.Snapshot != null)
                ? "파일을 읽었습니다. 룰셋 변경 검토 또는 직접 입력을 진행하세요."
                : $"이번 조회 실패 {batch.Failed}개 · 남은 파일 {batch.Remaining}개. 처리 결과를 확인하세요. 미조회 파일은 ‘파일 관리 → 미조회 파일 계속 조회’로 이어갈 수 있습니다.";
            if (_models.Any(m => m.Snapshot == null)) MainTabs.SelectedIndex = 2;
            completed = !batch.Blocked && !_worker.NeedsAttention;
        });
        if (completed && _loadedRules != null && _models.Count > 0 && _models.All(m => m.Snapshot != null && !m.Snapshot.RequiresRequery))
            Guard(() => PrepareRules(_loadedRules));
    }
    private static string ModelIdentityPath(ModelSnapshot s) => s.CentralPath.Length > 0 ? s.CentralPath : s.CanonicalPath;

    private IEnumerable<ParameterRow> AllRows() => _models.Where(m => m.Snapshot != null).SelectMany(m => m.Snapshot!.Rows);
    private void RefreshFields()
    {
        if (!_initialized) return;
        var current = FieldCombo.SelectedItem as string;
        _fieldNames = AllRows().Select(r => r.Name).Distinct().OrderBy(s => s).ToList();
        ApplyFieldSearch(current != null && _fieldNames.Contains(current) ? current : _fieldNames.Contains("재료명") ? "재료명" : _fieldNames.FirstOrDefault());
        RefreshRows();
    }
    private void ApplyFieldSearch(string? current)
    {
        var search = FieldSearchBox.Text.Trim();
        static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        var matches = _fieldNames.Where(n => Compact(n).Contains(Compact(search), StringComparison.CurrentCultureIgnoreCase)).ToList();
        var count = matches.Count;
        // 검색만으로 선택/행/입력값을 바꾸지 않는다. 검색 밖의 현재 항목은 맨 위에 남긴다.
        var retained = current != null && !matches.Contains(current);
        if (retained) matches.Insert(0, current!);
        _filtering = true;
        try { FieldCombo.ItemsSource = matches; FieldCombo.SelectedItem = current; }
        finally { _filtering = false; }
        FieldSearchStatus.Text = search.Length == 0 ? $"전체 {count:N0}개" : $"검색 {count:N0}개";
        FieldSearchStatus.ToolTip = retained ? "검색 결과와 별도로 현재 선택 항목을 맨 위에 유지합니다." : "등록한 파일에서 조회한 매개변수 이름을 검색합니다.";
        ClearFieldSearchButton.IsEnabled = FieldSearchBox.Text.Length > 0;
    }
    private void FieldSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_filtering) Guard(() => ApplyFieldSearch(FieldCombo.SelectedItem as string));
    }
    private void ClearFieldSearch(object sender, RoutedEventArgs e) => Guard(() => { FieldSearchBox.Clear(); FieldSearchBox.Focus(); });
    private void RefreshRows()
    {
        if (!_initialized || _filtering) return;
        var scope = (ScopeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "전체 범위";
        var field = FieldCombo.SelectedItem as string;
        var search = SearchBox.Text.Trim();
        var model = ModelsList.SelectedItem as ModelItem;
        var rows = AllRows().Where(r => (model == null || r.ModelPath == model.Path) && (scope == "전체 범위" || r.Scope == scope) && (field == null || r.Name == field));
        if (search.Length > 0) rows = rows.Where(r => new[] { r.ModelTitle, r.Category, r.Family, r.TypeName, r.TargetName, r.Display }.Any(s => s.Contains(search, StringComparison.CurrentCultureIgnoreCase)));
        var visible = rows.OrderBy(r => r.ModelTitle).ThenBy(r => r.Family).ThenBy(r => r.TargetName).ThenBy(r => r.ElementId).ToList();
        ValuesGrid.ItemsSource = visible;
        CountText.Text = $"{visible.Count:N0}행 · 편집 가능 {visible.Count(r => r.Editable):N0}행";
        UpdateEditor();
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _filtering) return;
        if (ReferenceEquals(sender, FieldCombo)) ApplyFieldSearch(FieldCombo.SelectedItem as string);
        ValueBox.Clear(); ChoiceCombo.SelectedItem = null; RefreshRows();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_initialized) RefreshRows(); }
    private void ModelSelectionChanged(object sender, SelectionChangedEventArgs e) { if (_initialized && !_busy) RefreshRows(); }
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
        UpdateInputPreview();
    }
    private void InputChanged(object sender, TextChangedEventArgs e) { if (_initialized) UpdateInputPreview(); }
    private void ChoiceChanged(object sender, SelectionChangedEventArgs e) { if (_initialized) UpdateInputPreview(); }
    private void UpdateInputPreview()
    {
        var selected = ValuesGrid.SelectedItems.Cast<ParameterRow>().ToList();
        var value = ChoiceCombo.Visibility == Visibility.Visible ? ChoiceCombo.SelectedItem as string ?? "" : ValueBox.Text;
        var sameField = selected.Select(r => r.FieldKey).Distinct().Count() == 1;
        StageButton.IsEnabled = selected.Count > 0 && sameField && selected.All(r => EditPlanner.Validate(r, value).Length == 0);
        StageButton.Content = selected.Count > 0 ? $"선택한 {selected.Count:N0}개에 입력" : "선택한 행에 입력";
        var before = selected.Select(r => r.ValueLabel).Distinct().Take(2).ToList();
        InputPreview.Text = selected.Count == 0 ? "목록에서 바꿀 행을 먼저 선택하세요." :
            !sameField ? "자료형·범위가 같은 항목끼리 선택하세요." :
            !StageButton.IsEnabled ? "입력할 수 없는 행 또는 값입니다. 상태와 선택 목록을 확인하세요." :
            $"미리보기: {(before.Count == 1 ? before[0] : "(여러 값)")}  →  {(value.Length == 0 ? "(빈 값으로 지움)" : value)} · 아직 파일에 저장하지 않습니다.";
    }
    private void ReviewChanges(object sender, RoutedEventArgs e) => Guard(() => { MainTabs.SelectedIndex = 1; });
    private void LoadRules(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Sunny 룰셋 (*.sunnyrules.json)|*.sunnyrules.json|JSON 파일 (*.json)|*.json", Title = "함께 정한 룰셋 파일 선택" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 2 * 1024 * 1024) throw new InvalidOperationException("룰셋은 2MB 이하만 지원합니다.");
        SelectRules(RulePlanner.Parse(File.ReadAllText(dialog.FileName)));
        if (_models.Count > 0 && _models.All(m => m.Snapshot != null && !m.Snapshot.RequiresRequery && (m.Snapshot.Query == null || m.Snapshot.Query.Covers(QueryPlan.FromRules(_loadedRules!)))))
            PrepareRules(_loadedRules!);
    });
    public void SelectRules(RuleSet file)
    {
        if (_busy) throw new InvalidOperationException("조회가 끝난 뒤 룰셋을 바꾸세요.");
        QueryPlan.FromRules(file);
        if (file.SampleOnly && !_sample) throw new InvalidOperationException("연습용 룰셋은 샘플 모드에서만 사용하세요.");
        _loadedRules = file;
        RuleSummaryPanel.Visibility = Visibility.Collapsed;
        UpdatePending();
        StatusText.Text = _models.Count == 0 ? "룰셋 준비 완료. 이제 RVT 파일을 선택하세요." :
            "룰셋 준비 완료. 필요한 항목이 부족하면 ‘파일 관리 → 다시 조회’를 누르세요.";
    }
    private void UseFullQuery(object sender, RoutedEventArgs e) => Guard(() =>
    {
        _loadedRules = null; UpdatePending();
        StatusText.Text = "다음 조회부터 전체 항목을 읽습니다. 기존 선택 조회값과 변경 예정은 유지합니다. 전체 항목이 필요하면 ‘파일 관리 → 다시 조회’를 누르세요.";
    });
    private void ReviewLoadedRules(object sender, RoutedEventArgs e) => Guard(() =>
        PrepareRules(_loadedRules ?? throw new InvalidOperationException("룰셋을 먼저 불러오세요.")));
    // 룰셋은 조회된 전체 파일로 평가한다. 그리드의 검색/행 선택 때문에 일부만 조용히 적용하면 안 된다.
    public void PrepareRules(RuleSet file)
    {
        if (_busy) throw new InvalidOperationException("진행 중인 파일 작업이 끝난 후 불러오세요.");
        if (_models.Any(m => m.Snapshot?.RequiresRequery == true)) throw new InvalidOperationException("실패한 파일의 원인을 확인하고 다시 조회한 뒤 룰셋을 불러오세요.");
        if (_models.Count == 0 || _models.Any(m => m.Snapshot == null))
            throw new InvalidOperationException("모든 대상 파일을 먼저 읽어야 합니다. 실패하거나 중복된 파일은 ‘파일 관리’에서 제외하세요.");
        QueryPlan.RequireCoverage(_models.Select(m => m.Snapshot!), file);
        var available = _models.SelectMany(m => m.Snapshot!.Owners.Concat(m.Snapshot.Rows));
        var plan = RulePlanner.Plan(file, available, _edits.Values, _sample);
        foreach (var edit in plan.Edits) _edits[edit.Original.Key] = edit;
        RuleSummary.Text = $"불러온 룰셋: {file.Name}\n대상 파일: {string.Join(", ", _models.Select(m => m.Title))}\n" + string.Join("\n", plan.Notes) +
            "\n※ 불러올 당시의 결과입니다. 이후 제외/직접 입력한 내용은 아래 목록이 최종 기준입니다.";
        RuleSummaryPanel.Visibility = Visibility.Visible;
        ReportBox.AppendText("\n" + RuleSummary.Text + "\n");
        UpdatePending(); MainTabs.SelectedIndex = 1;
        StatusText.Text = $"룰셋으로 {plan.Edits.Count:N0}개 변경을 준비했습니다. 기존 값과 바뀔 값을 확인하세요. 아직 저장하지 않았습니다.";
    }
    private void StageValues(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (_edits.Values.Any(e => e.KacCleanup)) throw new InvalidOperationException("유형 정리 변경을 먼저 적용하거나 제외한 뒤 직접 입력하세요. 두 작업은 별도로 검증합니다.");
        var selected = ValuesGrid.SelectedItems.Cast<ParameterRow>().ToList();
        if (selected.Select(r => r.FieldKey).Distinct().Count() > 1) throw new InvalidOperationException("같은 이름·자료형·범위의 매개변수만 함께 입력하세요.");
        var value = ChoiceCombo.Visibility == Visibility.Visible ? ChoiceCombo.Text : ValueBox.Text;
        var planned = EditPlanner.Stage(selected, value);
        foreach (var row in selected) _edits.Remove(row.Key);
        foreach (var edit in planned) _edits[edit.Original.Key] = edit;
        UpdatePending(); StatusText.Text = $"{planned.Count:N0}개 값을 준비했습니다. 오른쪽 아래 ‘③ 변경 내용 확인’을 눌러주세요. 아직 저장하지 않았습니다.";
    });
    private void CopyValues(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (_edits.Values.Any(e => e.KacCleanup)) throw new InvalidOperationException("유형 정리 변경을 먼저 적용하거나 제외한 뒤 복사하세요.");
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
        ApplyButton.IsEnabled = !_busy && !_sample && _edits.Count > 0 && !_worker.NeedsAttention && !HasFailedPending();
        ReviewButton.IsEnabled = !_busy && _edits.Count > 0;
        ReviewButton.Content = $"③ 변경 내용 확인 ({_edits.Count:N0})";
        WelcomePanel.Visibility = _models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RulesButton.IsEnabled = !_busy;
        ReviewRulesButton.IsEnabled = !_busy && _loadedRules != null && _models.Count > 0 && _models.All(m => m.Snapshot != null && !m.Snapshot.RequiresRequery);
        RulesButton.Content = _loadedRules == null ? "룰셋 먼저 불러오기" : "룰셋 바꾸기";
        WelcomeRulesButton.Content = _loadedRules == null ? "1. 룰셋 먼저 불러오기" : "선택한 룰셋 바꾸기";
        WelcomeRulesPanel.Visibility = _loadedRules == null ? Visibility.Collapsed : Visibility.Visible;
        WelcomeRulesName.Text = _loadedRules?.Name ?? "";
        QuerySummary.FontWeight = _loadedRules == null ? FontWeights.Normal : FontWeights.SemiBold;
        QuerySummary.Foreground = new System.Windows.Media.SolidColorBrush(_loadedRules == null ? System.Windows.Media.Color.FromRgb(104, 119, 133) : System.Windows.Media.Color.FromRgb(40, 100, 61));
        QuerySummary.Text = _loadedRules == null ? "조회 방식: 전체 항목 · 룰셋을 먼저 불러오면 필요한 항목만 조회합니다." :
            "✓ 룰셋 선택 완료: " + _loadedRules.Name + " · 다음 조회에 사용 (모델 적용 전) · " + (_loadedRules.Cleanup ? "유형명·총 두께·작업세트·ID 데이터·룸 경계·IFC" : string.Join(", ", QueryPlan.FromRules(_loadedRules).Names));
        QuerySummary.ToolTip = QuerySummary.Text + "\n선택 조회에 없는 항목은 미조회이며 빈 값이 아닙니다. RVT 자체를 여는 시간은 별도로 필요합니다.";
        FileSummary.Text = _models.Count == 0 ? "수정할 파일을 고르면 자동으로 읽어옵니다." :
            $"{_models.Count}개 파일 · {_models.Count(m => m.Snapshot != null)}개 읽음" + (_sample ? " · 연습 중 (실제 저장 불가)" : "");
    }
    private bool HasFailedPending() => _models.Any(m => m.Snapshot?.RequiresRequery == true && _edits.Values.Any(e => string.Equals(e.Original.ModelPath, m.Path, StringComparison.OrdinalIgnoreCase)));
    private void OpenLinkRemoval(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (_sample || _models.Count == 0) throw new InvalidOperationException("실제 RVT 파일을 먼저 등록하세요. 샘플에서는 삭제하지 않습니다.");
        if (_worker.NeedsAttention) throw new InvalidOperationException("작업용 Revit의 미확정 결과를 먼저 확인하세요.");
        if (_edits.Count > 0) throw new InvalidOperationException("매개변수 변경 예정이 남아 있습니다. 먼저 적용하거나 작업 저장 후 변경 목록을 정리하세요. 링크 삭제와 섞어서 처리하지 않습니다.");
        var dialog = new LinkRemovalWindow(_models.Select(m => m.Path), async request =>
        {
            await _worker.Connect(RevitPathBox.Text.Trim()); return await _worker.Send(request);
        }, (path, response) =>
        {
            var item = _models.Single(m => m.Path == path);
            item.Snapshot = null; item.Status = response.Success ? "링크 삭제 완료 · 재조회 필요" : "링크 삭제 실패 · 재조회 필요";
            ReportBox.AppendText($"\n{path}\n{response.Message}\n삭제 전 백업: {response.BackupPath}\n복구: {response.RecoveryPath}\n");
            RefreshFields(); UpdatePending();
        }) { Owner = this, Resources = Resources };
        dialog.ShowDialog();
    });
    private async void ApplyChanges(object sender, RoutedEventArgs e)
    {
        if (_busy || _sample || _edits.Count == 0) return;
        if (HasFailedPending()) { MessageBox.Show(this, "실패한 파일의 기존 변경을 다시 적용할 수 없습니다. 처리 결과의 원인을 확인하고 ‘다시 조회’를 눌러주세요."); return; }
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
                else
                {
                    snapshot.RequiresRequery = true;
                    item.Status = result.RequiresAttention ? "반영 여부 확인 필요" : "적용 실패 · 원인 확인 후 재조회";
                    ReportBox.AppendText("같은 변경 재실행을 차단했습니다. 원본의 원인을 확인한 뒤 ‘다시 조회’에서 이 파일만 새로 준비하세요. 기존 변경 목록은 검토/작업 저장용으로 유지합니다.\n");
                }
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
        Protocol.Write(dialog.FileName, new WorkspaceFile { Sample = _sample, LoadedRules = _loadedRules, Paths = _models.Select(m => m.Path).ToList(), Models = _models.Where(m => m.Snapshot != null).Select(m => m.Snapshot!).ToList(), Edits = _edits.Values.ToList() });
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
        if (file.LoadedRules != null) QueryPlan.FromRules(file.LoadedRules);
        foreach (var model in file.Models) model.Query?.Validate();
        if (file.Models.Any(m => m.Owners.Any(o => o.Name.Length != 0 || o.ParameterKey.Length != 0 || o.Editable)))
            throw new InvalidDataException("대상 식별 정보에 편집 값이 섞여 있습니다. RVT를 다시 조회하세요.");
        if (file.Models.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Models.Count ||
            file.Models.Any(m => !file.Paths.Contains(m.Path, StringComparer.OrdinalIgnoreCase) || m.Rows.Concat(m.Owners).Any(r => r.ModelPath != m.Path)))
            throw new InvalidDataException("작업 파일의 모델 연결이 일치하지 않습니다.");
        var rows = file.Models.SelectMany(m => m.Rows).ToDictionary(r => r.Key);
        foreach (var edit in file.Edits) if (!rows.ContainsKey(edit.Original.Key)) throw new InvalidDataException("변경 대상이 조회 목록에 없습니다.");
        _models.Clear(); _edits.Clear(); _sample = file.Sample; _loadedRules = file.LoadedRules; RuleSummaryPanel.Visibility = Visibility.Collapsed;
        foreach (var path in file.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = file.Models.SingleOrDefault(m => m.Path == path); snapshot?.ConnectCatalogs();
            _models.Add(new() { Path = path, Snapshot = snapshot, Status = snapshot == null ? "조회 전" : snapshot.RequiresRequery ? "적용 실패 · 원인 확인 후 재조회" : "저장된 조회값 · 적용 전 자동 재검증" });
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
        _edits.Clear(); _models.Clear(); _sample = true; _loadedRules = null; RuleSummaryPanel.Visibility = Visibility.Collapsed;
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
        ModelsList.SelectedItem = null; SourceCombo.SelectedIndex = 0; ScopeCombo.SelectedIndex = 0; MainTabs.SelectedIndex = 0; SearchBox.Clear(); FieldSearchBox.Clear();
        RefreshFields(); UpdatePending(); StatusText.Text = "샘플 모드 · 선택·입력·기준값 복사·작업 저장을 시험할 수 있습니다. 실제 RVT에는 적용되지 않습니다.";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; MessageBox.Show(this, "파일 작업 중에는 창을 닫을 수 없습니다. 작업용 Revit의 안내 창이 있다면 확인하세요."); return; }
        if (!DiscardPending()) { e.Cancel = true; return; }
        if (_worker.Connected) MessageBox.Show(this, "작업용 Revit은 강제 종료하지 않습니다. 작업/복구 상태를 확인한 뒤 별도로 종료해 주세요.", "작업용 Revit 안내");
    }
}
