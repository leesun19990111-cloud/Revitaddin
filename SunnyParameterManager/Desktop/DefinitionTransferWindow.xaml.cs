using SunnyParameterManager.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SunnyParameterManager.Desktop;

public sealed class TransferDefinition(ParameterDefinitionInfo definition, Action changed) : INotifyPropertyChanged
{
    public ParameterDefinitionInfo Definition { get; } = definition;
    public string Name => Definition.Name;
    public string Binding => Definition.Binding;
    public string Kind => CanSelect ? "공유 매개변수" : "일반 · GUID 없음";
    public bool CanSelect => Guid.TryParse(Definition.Guid, out _);
    private bool _selected;
    public bool Selected { get => _selected; set { if (_selected == value || !CanSelect && value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TransferTarget(string path, Action changed) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    private bool _selected;
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); } }
    private string _status = "검사 전";
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public ModelSnapshot? Snapshot { get; set; }
    public IReadOnlyList<DefinitionTransferPlanner.Decision>? Definitions { get; set; }
    public IReadOnlyList<DefinitionTransferPlanner.ValueDecision>? Values { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record TransferPreviewRow(string Model, string Name, string Status, string Reason);

public partial class DefinitionTransferWindow : Window
{
    private readonly List<TransferTarget> _targets;
    private readonly List<TransferDefinition> _definitions = [];
    private readonly Func<Request, Task<Response>> _send;
    private readonly Action<string, Response> _changed;
    private ModelSnapshot? _source;
    private bool _ready, _busy, _blocked, _stopRequested, _definitionPreview, _valuePreview;

    public DefinitionTransferWindow(IEnumerable<string> paths, Func<Request, Task<Response>> send, Action<string, Response> changed)
    {
        _send = send; _changed = changed;
        _targets = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => new TransferTarget(p, Invalidate)).ToList();
        InitializeComponent();
        SourceCombo.ItemsSource = _targets; TargetsGrid.ItemsSource = _targets;
        SourceCombo.SelectedIndex = 0;
        foreach (var target in _targets.Skip(1)) target.Selected = true;
        _ready = true; UpdateButtons();
    }

    private TransferTarget? SourceItem => SourceCombo.SelectedItem as TransferTarget;
    private List<TransferTarget> Chosen => _targets.Where(t => t.Selected && t != SourceItem).ToList();
    private List<ParameterDefinitionInfo> Selected => _definitions.Where(d => d.Selected).Select(d => d.Definition).ToList();

    private void SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        _source = null; _definitions.Clear(); if (!_ready) return;
        DefinitionsGrid.ItemsSource = null;
        if (SourceItem != null) SourceItem.Selected = false;
        Invalidate(); SummaryText.Text = "기준 파일이 바뀌었습니다. 정의를 다시 읽으세요.";
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        var search = SearchBox.Text.Trim();
        DefinitionsGrid.ItemsSource = _definitions.Where(d => search.Length == 0 || d.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }
    private void SelectVisible(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        foreach (var item in (DefinitionsGrid.ItemsSource as IEnumerable<TransferDefinition> ?? [])) if (item.CanSelect) item.Selected = true;
    }
    private void ClearDefinitions(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        foreach (var item in _definitions) item.Selected = false;
    }
    private void OptionsChanged(object sender, RoutedEventArgs e) => Invalidate();
    private void Invalidate()
    {
        if (!_ready || _busy) return;
        _definitionPreview = _valuePreview = false;
        foreach (var target in _targets) { target.Definitions = null; target.Values = null; target.Snapshot = null; target.Status = "검사 전"; }
        PreviewGrid.ItemsSource = null;
        SummaryText.Text = "선택이 바뀌었습니다. 대상 검사를 다시 실행하세요.";
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        if (!_ready) return;
        ReadSourceButton.IsEnabled = !_busy && !_blocked && SourceItem != null;
        PreviewButton.IsEnabled = !_busy && !_blocked && _source != null && Chosen.Count > 0 && Selected.Count > 0;
        ApplyDefinitionsButton.IsEnabled = !_busy && !_blocked && _definitionPreview && (Chosen.Any(t => t.Definitions?.Any(d => d.Edit != null) == true) ||
            CopyValuesCheck.IsChecked == true && Eligible.Count > 0);
        ApplyValuesButton.IsEnabled = !_busy && !_blocked && _valuePreview && Chosen.Any(t => t.Values?.Any(v => v.Edit != null) == true);
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy) _stopRequested = false;
        SourceCombo.IsEnabled = TargetsGrid.IsEnabled = DefinitionsGrid.IsEnabled = SearchBox.IsEnabled = CopyValuesCheck.IsEnabled = !busy;
        StopButton.IsEnabled = busy; ReadSourceButton.IsEnabled = PreviewButton.IsEnabled = ApplyDefinitionsButton.IsEnabled = ApplyValuesButton.IsEnabled = !busy;
        if (!busy) UpdateButtons();
    }
    private void StopClick(object sender, RoutedEventArgs e)
    {
        if (!_busy) return;
        _stopRequested = true; StopButton.IsEnabled = false;
        SummaryText.Text = "중지 요청됨 — 현재 파일의 저장·동기화·소유권 반환이 끝난 뒤 중지합니다.";
    }
    private void Log(string text)
    {
        LogBox.AppendText("\n" + DateTime.Now.ToString("HH:mm:ss") + " · " + text + "\n");
        LogBox.ScrollToEnd();
    }
    private async Task<ModelSnapshot?> Read(string path, QueryPlan query)
    {
        var response = await _send(new Request { Operation = "조회", Path = path, Query = query });
        Log(System.IO.Path.GetFileName(path) + " · " + response.Stage + " · " + response.Message);
        if (response.RequiresAttention) _blocked = true;
        if (!response.Success || response.Snapshot == null) return null;
        if (!string.Equals(response.Snapshot.Path, path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("다른 모델의 조회 결과가 반환되었습니다.");
        return response.Snapshot;
    }
    private async void ReadSourceClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _blocked || SourceItem == null) return;
        SetBusy(true);
        try
        {
            _source = await Read(SourceItem.Path, DefinitionTransferPlanner.DefinitionQuery());
            _definitions.Clear();
            if (_source?.Definitions == null) { SummaryText.Text = "기준 파일의 정의를 읽지 못했습니다. 처리 기록을 확인하세요."; return; }
            foreach (var def in _source.Definitions.Definitions.OrderBy(d => d.Name)) _definitions.Add(new TransferDefinition(def, Invalidate));
            SearchChanged(this, null!);
            var shared = _definitions.Count(d => d.CanSelect);
            SummaryText.Text = $"기준 정의 {_definitions.Count:N0}개 · 전송 가능 공유 매개변수 {shared:N0}개 · 일반 프로젝트 매개변수는 이유를 표시하고 제외합니다.";
        }
        catch (Exception ex) { Log("기준 조회 실패: " + ex.Message); SummaryText.Text = "기준 파일을 읽지 못했습니다. 처리 기록을 확인하세요."; }
        finally { SetBusy(false); }
    }
    private async void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _blocked || _source == null || Chosen.Count == 0 || Selected.Count == 0) return;
        _definitionPreview = _valuePreview = false;
        foreach (var target in _targets) { target.Definitions = null; target.Values = null; target.Snapshot = null; }
        PreviewGrid.ItemsSource = null;
        SetBusy(true);
        try
        {
            foreach (var target in Chosen)
            {
                if (_stopRequested) break;
                target.Status = "정의 검사 중…";
                target.Snapshot = await Read(target.Path, DefinitionTransferPlanner.DefinitionQuery());
                if (target.Snapshot?.Definitions == null) { target.Status = "조회 실패"; if (_blocked) break; continue; }
                try
                {
                    target.Definitions = DefinitionTransferPlanner.PlanDefinitions(_source, target.Snapshot, Selected);
                    target.Status = target.Definitions.Any(d => d.Status == "제외") ? "충돌 · 제외" :
                        $"추가 {target.Definitions.Count(d => d.Edit != null):N0}개 · 기존 {target.Definitions.Count(d => d.Status == "이미 있음"):N0}개";
                }
                catch (Exception ex) { target.Status = "검사 실패 · 제외"; Log(target.Title + " · " + ex.Message); }
            }
            var rows = Chosen.SelectMany(t => t.Definitions?.Select(d => new TransferPreviewRow(t.Title, d.Name, d.Status, d.Reason)) ??
                [new TransferPreviewRow(t.Title, "", "제외", t.Status)]).ToList();
            PreviewGrid.ItemsSource = rows;
            _definitionPreview = !_stopRequested && !_blocked && Chosen.All(t => t.Definitions != null);
            SummaryText.Text = $"대상 {Chosen.Count:N0}개 · 추가 예정 {rows.Count(r => r.Status == "추가 예정"):N0}개 · 충돌/제외 {rows.Count(r => r.Status == "제외"):N0}개. 제외된 모델은 적용하지 않습니다.";
        }
        catch (Exception ex) { Log("검사 중단: " + ex.Message); SummaryText.Text = "검사를 완료하지 못했습니다. 처리 기록을 확인하세요."; }
        finally { SetBusy(false); }
    }
    private List<TransferTarget> Eligible => Chosen.Where(t => t.Definitions != null && t.Definitions.All(d => d.Status != "제외")).ToList();

    private async void ApplyDefinitionsClick(object sender, RoutedEventArgs e)
    {
        if (!_definitionPreview || _busy || _blocked || _source == null) return;
        var eligible = Eligible;
        var count = eligible.Sum(t => t.Definitions!.Count(d => d.Edit != null));
        if (count == 0)
        {
            if (CopyValuesCheck.IsChecked != true) return;
            _definitionPreview = false; SetBusy(true);
            try
            {
                foreach (var target in eligible) target.Status = "정의 일치 · 변경 없음";
                await PrepareValues();
            }
            catch (Exception ex) { Log("값 조회 실패: " + ex.Message); SummaryText.Text = "값을 검토하지 못했습니다. 기록을 확인하세요."; }
            finally { SetBusy(false); }
            return;
        }
        var message = $"기준 파일: {_source.Path}\n대상 모델: {eligible.Count:N0}개\n추가할 공유 매개변수 정의: {count:N0}개\n\n이 정의를 대상 RVT에 추가하고 작업공유 모델은 중앙에 동기화할까요? 기존 같은 이름의 다른 정의가 있는 모델은 제외합니다. 여러 모델은 순차 처리하며, 앞서 완료한 모델은 자동으로 되돌리지 않습니다. 현재 사용자의 소유권은 동기화 뒤 반환합니다.";
        if (MessageBox.Show(this, message, "정의 전송 최종 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _definitionPreview = false; SetBusy(true); DetailsTabs.SelectedIndex = 1;
        try
        {
            foreach (var target in eligible)
            {
                if (_stopRequested) break;
                var edits = target.Definitions!.Where(d => d.Edit != null).Select(d => d.Edit!).ToList();
                if (edits.Count == 0) { target.Status = "정의 일치 · 변경 없음"; continue; }
                target.Status = "정의 적용·동기화 중…";
                var snapshot = target.Snapshot!;
                var response = await _send(new Request { Operation = "적용", Path = target.Path, Identity = snapshot.Identity,
                    CanonicalPath = snapshot.CanonicalPath, Confirmed = true, LocalChangesConfirmed = snapshot.Kind == "로컬", Edits = edits });
                _changed(target.Path, response);
                Log(target.Title + " · " + response.Stage + " · " + response.Message + "\n복구: " + response.RecoveryPath + "\n백업: " + response.BackupPath);
                target.Status = response.Success && (!snapshot.Kind.Equals("중앙") && !snapshot.Kind.Equals("로컬") || response.CentralSynchronized && response.OwnershipReleased) ? "정의 적용 완료" : "실패 · 기록 확인";
                if (target.Status != "정의 적용 완료" || response.RequiresAttention) { _blocked |= response.RequiresAttention; break; }
            }
            if (!_stopRequested && !_blocked && CopyValuesCheck.IsChecked == true && Eligible.All(t => t.Status is "정의 적용 완료" or "정의 일치 · 변경 없음"))
                await PrepareValues();
            else SummaryText.Text = "정의 전송 처리 결과를 확인하세요. 실패하거나 중지된 모델은 재조회 후 다시 검토해야 합니다.";
        }
        catch (Exception ex) { Log("적용 결과 확인 필요: " + ex.Message); _blocked = true; SummaryText.Text = "작업 결과를 확인해야 합니다. 자동 재실행하지 마세요."; }
        finally { SetBusy(false); }
    }

    private async Task PrepareValues()
    {
        var query = DefinitionTransferPlanner.ValueQuery(Selected);
        var source = await Read(SourceItem!.Path, query);
        if (source == null) { SummaryText.Text = "값 복사를 위한 기준 재조회 실패. 정의 적용 결과는 기록에 남았습니다."; return; }
        var rows = new List<TransferPreviewRow>();
        foreach (var target in Eligible)
        {
            if (_stopRequested) break;
            target.Status = "값 재조회 중…";
            target.Snapshot = await Read(target.Path, query);
            if (target.Snapshot == null) { target.Status = "값 조회 실패"; if (_blocked) break; continue; }
            target.Values = DefinitionTransferPlanner.PlanValues(source, target.Snapshot, Selected);
            rows.AddRange(target.Values.Select(v => new TransferPreviewRow(target.Title, v.Name + " · " + v.Target, v.Status, v.Reason)));
            target.Status = $"값 변경 예정 {target.Values.Count(v => v.Edit != null):N0}개";
        }
        PreviewGrid.ItemsSource = rows; DetailsTabs.SelectedIndex = 0;
        _valuePreview = !_stopRequested && !_blocked && Eligible.All(t => t.Values != null);
        SummaryText.Text = $"값 변경 예정 {rows.Count(r => r.Status == "변경 예정"):N0}개 · 동일/미대응/읽기 전용 {rows.Count(r => r.Status != "변경 예정"):N0}개. 인스턴스는 동일 UniqueId만 대응합니다. 목록을 검토한 뒤 값 적용을 누르세요.";
    }
    private async void ApplyValuesClick(object sender, RoutedEventArgs e)
    {
        if (!_valuePreview || _busy || _blocked) return;
        var targets = Eligible.Where(t => t.Values?.Any(v => v.Edit != null) == true).ToList();
        if (targets.Count == 0) return;
        var count = targets.Sum(t => t.Values!.Count(v => v.Edit != null));
        if (MessageBox.Show(this, $"대상 {targets.Count:N0}개 모델의 값 {count:N0}건을 덮어쓰고, 작업공유 모델은 중앙에 동기화할까요?\n\n인스턴스는 동일 UniqueId만 복사합니다. 미대응·읽기 전용 값은 제외합니다. 여러 모델 전체를 한 번에 되돌릴 수 없습니다.", "값 전송 최종 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _valuePreview = false; SetBusy(true); DetailsTabs.SelectedIndex = 1;
        try
        {
            foreach (var target in targets)
            {
                if (_stopRequested) break;
                var snapshot = target.Snapshot!;
                var response = await _send(new Request { Operation = "적용", Path = target.Path, Identity = snapshot.Identity,
                    CanonicalPath = snapshot.CanonicalPath, Confirmed = true, LocalChangesConfirmed = snapshot.Kind == "로컬",
                    Edits = target.Values!.Where(v => v.Edit != null).Select(v => v.Edit!).ToList() });
                _changed(target.Path, response);
                Log(target.Title + " · 값 " + response.Stage + " · " + response.Message + "\n복구: " + response.RecoveryPath);
                target.Status = response.Success && (snapshot.Kind == "일반" || response.CentralSynchronized && response.OwnershipReleased) ? "값 적용 완료" : "값 적용 실패";
                if (target.Status != "값 적용 완료" || response.RequiresAttention) { _blocked |= response.RequiresAttention; break; }
            }
            SummaryText.Text = "모델별 값 전송·동기화·소유권 반환 결과를 처리 기록에서 확인하세요.";
        }
        catch (Exception ex) { Log("값 적용 결과 확인 필요: " + ex.Message); _blocked = true; SummaryText.Text = "작업 결과를 확인해야 합니다. 자동 재실행하지 마세요."; }
        finally { SetBusy(false); }
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; SummaryText.Text = "현재 모델 처리가 끝난 뒤 창을 닫으세요.";
    }
}
