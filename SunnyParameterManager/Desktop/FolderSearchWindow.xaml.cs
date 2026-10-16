using Microsoft.Win32;
using SunnyParameterManager.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace SunnyParameterManager.Desktop;

public sealed class RvtCandidate : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    private bool _selected = true;
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class FolderSearchWindow : Window
{
    private readonly ObservableCollection<string> _roots = [];
    private readonly HashSet<string> _registered;
    private List<RvtCandidate> _candidates = [];
    private List<string> _warnings = [];
    private CancellationTokenSource? _search;
    private bool _ready, _closed;
    public List<string> SelectedPaths { get; private set; } = [];

    public FolderSearchWindow(IEnumerable<string> registered)
    {
        _registered = registered.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InitializeComponent(); RootsList.ItemsSource = _roots; _ready = true; UpdateCount();
    }
    private void AddFolders(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new OpenFolderDialog { Title = "검색할 폴더 선택 — Ctrl/Shift로 여러 폴더 선택 가능", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            foreach (var path in picker.FolderNames) if (!_roots.Contains(path, StringComparer.OrdinalIgnoreCase)) _roots.Add(path);
            Invalidate();
        }
        catch (Exception ex) { SearchStatus.Text = "폴더 선택 실패 · " + ex.Message; }
    }
    private void RemoveFolders(object sender, RoutedEventArgs e)
    {
        foreach (var root in RootsList.SelectedItems.Cast<string>().ToList()) _roots.Remove(root);
        Invalidate();
    }
    private void CriteriaChanged(object sender, RoutedEventArgs e) { if (_ready) Invalidate(); }
    private void Invalidate()
    {
        _candidates = []; _warnings = []; CandidatesGrid.ItemsSource = _candidates; SelectedPaths = [];
        SearchStatus.Text = "조건을 변경했습니다. ‘파일 찾기’를 눌러 새 결과를 확인하세요."; UpdateCount();
    }
    private async void Search(object sender, RoutedEventArgs e)
    {
        if (_search != null) return;
        if (_roots.Count == 0 || string.IsNullOrWhiteSpace(ContainsBox.Text)) { SearchStatus.Text = "폴더와 파일명에 포함할 문자열을 지정하세요."; return; }
        Invalidate();
        var roots = _roots.ToArray(); var contains = ContainsBox.Text; var recursive = RecursiveBox.IsChecked == true; var backups = ExcludeBackupsBox.IsChecked == true;
        using var cancellation = new CancellationTokenSource(); _search = cancellation;
        SearchControls.IsEnabled = SearchButton.IsEnabled = false; StopSearchButton.IsEnabled = true;
        SearchStatus.Text = "파일명을 검색하고 있습니다. Revit 모델은 아직 열지 않습니다."; UpdateCount();
        try
        {
            var result = await Task.Run(() => RvtFileSearch.Find(roots, contains, recursive, backups, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            PresentResults(result);
        }
        catch (OperationCanceledException) { if (!_closed) SearchStatus.Text = "검색을 중지했습니다. 파일을 추가하거나 열지 않았습니다."; }
        catch (Exception ex) { if (!_closed) SearchStatus.Text = "검색 실패 · " + ex.Message; }
        finally
        {
            _search = null;
            if (!_closed) { SearchControls.IsEnabled = SearchButton.IsEnabled = true; StopSearchButton.IsEnabled = false; UpdateCount(); }
        }
    }
    private void PresentResults(RvtSearchResult result)
    {
        var omitted = result.Files.Count(p => _registered.Contains(p));
        _candidates = result.Files.Where(p => !_registered.Contains(p)).Select(p => new RvtCandidate { Path = p }).ToList();
        _warnings = result.Warnings; CandidatesGrid.ItemsSource = _candidates;
        SearchStatus.Text = $"검색 완료 · 이미 등록된 파일 {omitted:N0}개 제외 · 백업 파일/폴더 {result.SkippedBackups:N0}개 제외" +
            (_warnings.Count == 0 ? "" : "\n일부 경로는 검색하지 못했습니다 (최대 200건 표시):\n" + string.Join("\n", _warnings));
        UpdateCount();
    }
    private void StopSearch(object sender, RoutedEventArgs e)
    {
        _search?.Cancel();
        SearchStatus.Text = "중지 요청 중입니다. 네트워크 경로가 응답을 기다리는 중이면 반환 후 중지됩니다.";
    }
    private void CandidateChanged(object sender, RoutedEventArgs e) { if (_ready) Dispatcher.BeginInvoke(new Action(UpdateCount)); }
    private void UpdateCount()
    {
        var selected = _candidates.Count(c => c.Selected);
        ResultCount.Text = $"후보 {_candidates.Count:N0}개 · 체크 {selected:N0}개";
        ConfirmButton.IsEnabled = _search == null && selected > 0;
        SelectAllButton.IsEnabled = ClearAllButton.IsEnabled = _search == null && _candidates.Count > 0;
    }
    private void SelectAll(object sender, RoutedEventArgs e) { foreach (var c in _candidates) c.Selected = true; UpdateCount(); }
    private void ClearAll(object sender, RoutedEventArgs e) { foreach (var c in _candidates) c.Selected = false; UpdateCount(); }
    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (_search != null) return;
        var selected = _candidates.Where(c => c.Selected).Select(c => c.Path).ToList();
        if (selected.Count == 0) return;
        if (_warnings.Count > 0 && MessageBox.Show(this, "일부 폴더를 검색하지 못했습니다. 표시된 후보 중 체크한 파일만 추가할까요?\n검색되지 않은 파일은 포함되지 않습니다.", "검색 누락 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (selected.Any(p => !File.Exists(p))) { SearchStatus.Text = "검색 후 사라진 파일이 있습니다. 다시 검색하세요."; return; }
        SelectedPaths = selected; DialogResult = true;
    }
    private void Cancel(object sender, RoutedEventArgs e) => Close();
    private void WindowClosing(object? sender, CancelEventArgs e) { _closed = true; _search?.Cancel(); }
}
