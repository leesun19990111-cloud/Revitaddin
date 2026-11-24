using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static class FolderSearchTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(Path.GetTempPath(), "Sunny-folder-search-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "A"); var b = Path.Combine(root, "B"); var child = Path.Combine(a, "하위");
        var backup = Path.Combine(a, "model_backup"); var misleading = Path.Combine(a, "MWX-폴더명");
        foreach (var dir in new[] { a, b, child, backup, misleading }) Directory.CreateDirectory(dir);
        // RVT 내용은 읽지 않는 검색 테스트용 빈 파일. 실제 모델이 아니다.
        string FileAt(string dir, string name) { var path = Path.Combine(dir, name); File.WriteAllText(path, ""); return path; }
        var first = FileAt(a, "MWX-15여객터미널-SP.rvt");
        var same = FileAt(b, "MWX-15여객터미널-SP.rvt");
        var sub = FileAt(child, "mwx-16건물-sp.RVT");
        var numbered = FileAt(a, "MWX-15여객터미널-SP.0001.rvt");
        var saved = FileAt(backup, "MWX-15여객터미널-SP.rvt");
        FileAt(a, "MWX-15여객터미널-SP.rfa"); FileAt(a, "MWX-15여객터미널-SP.rvt.txt");
        FileAt(misleading, "CJU-15건물-SP.rvt"); FileAt(a, "PUS-건물-A.rvt");
        var result = RvtFileSearch.Find([a, b, child, a.ToUpperInvariant()], " mwx ", true, true);
        check(result.Files.Count == 3, "다중/중복/상하위 폴더에서 RVT만 검색");
        check(result.Files.Contains(first) && result.Files.Contains(same), "이름 같고 경로 다른 파일은 각각 보존");
        check(result.Files.Contains(sub), "영문 대소문자 무시 및 하위 폴더 포함");
        check(result.Files.All(p => Path.GetFileName(p).Contains("mwx", StringComparison.OrdinalIgnoreCase)), "폴더명이 아닌 파일명으로 필터");
        check(result.SkippedBackups == 2 && !result.Files.Contains(numbered) && !result.Files.Contains(saved), "번호 백업과 백업 폴더 기본 제외");
        check(RvtFileSearch.Find([a, b], "MWX", false, true).Files.Count == 2, "하위 폴더 옵션 해제");
        check(RvtFileSearch.Find([a], "MWX", true, false).Files.Count == 4, "백업 제외 해제 시 명시 포함");
        check(RvtFileSearch.Find([a], "여객터미널", true, true).Files.SequenceEqual(new[] { first }), "한글 부분 문자열 검색");
        check(RvtFileSearch.Find([a], "MWX*", true, true).Files.Count == 0, "검색어는 와일드카드가 아닌 문자 그대로");
        check(RvtFileSearch.Find([a], "존재하지않음", true, true).Files.Count == 0, "일치 파일 없음은 빈 결과");
        reject(() => RvtFileSearch.Find([a], "  ", true, true), "빈 검색어로 모든 파일 자동 추가 방지");
        reject(() => RvtFileSearch.Find([], "MWX", true, true), "검색 폴더 필수");
        result = RvtFileSearch.Find([a, Path.Combine(root, "없는폴더")], "MWX", true, true);
        check(result.Warnings.Count == 1 && result.Files.Count == 2, "누락 경로는 경고하고 정상 폴더 후보 보존");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool cancelled = false;
        try { RvtFileSearch.Find([a], "MWX", true, true, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "검색 취소 지원");
        var main = new MainWindow(); var w = new FolderSearchWindow([first]) { Resources = main.Resources };
        render(main, "folder-search-entry", 1080, 700);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var roots = (ObservableCollection<string>)typeof(FolderSearchWindow).GetField("_roots", flags)!.GetValue(w)!;
        roots.Add(a); roots.Add(b);
        ((TextBox)w.FindName("ContainsBox")).Text = "MWX";
        result = RvtFileSearch.Find([a, b], "MWX", true, true);
        typeof(FolderSearchWindow).GetMethod("PresentResults", flags)!.Invoke(w, [result]);
        var candidates = ((DataGrid)w.FindName("CandidatesGrid")).Items.Cast<RvtCandidate>().ToList();
        check(candidates.Count == 2 && candidates.All(c => c.Path != first) && ((TextBox)w.FindName("SearchStatus")).Text.Contains("이미 등록된 파일 1개 제외"), "검색 결과에서 이미 등록한 경로 제외");
        void Call(string name, bool args = true) => typeof(FolderSearchWindow).GetMethod(name, flags)!.Invoke(w, args ? [w, new RoutedEventArgs()] : null);
        Call("UpdateCount", false);
        check(((Button)w.FindName("ConfirmButton")).IsEnabled && ((TextBlock)w.FindName("ResultCount")).Text.Contains("체크 2"), "검색 후보 체크 개수와 추가 버튼");
        Call("ClearAll"); check(candidates.All(c => !c.Selected) && !((Button)w.FindName("ConfirmButton")).IsEnabled, "전체 체크 해제 후 추가 차단");
        Call("SelectAll"); check(candidates.All(c => c.Selected), "후보 전체 체크");
        candidates[0].Selected = false; Call("UpdateCount", false);
        check(((TextBlock)w.FindName("ResultCount")).Text.Contains("체크 1"), "개별 후보 제외");
        foreach (var size in new[] { (960, 740), (800, 640) })
        {
            render(w, "folder-search-" + size.Item1, size.Item1, size.Item2);
            check(((DataGrid)w.FindName("CandidatesGrid")).ActualHeight > 130 && ((Button)w.FindName("ConfirmButton")).ActualWidth > 100, "폴더 검색 화면 최소 영역 " + size.Item1);
        }
        ((TextBox)w.FindName("ContainsBox")).Text = "-SP";
        check(((DataGrid)w.FindName("CandidatesGrid")).Items.Count == 0 && !((Button)w.FindName("ConfirmButton")).IsEnabled, "검색 조건 변경 시 이전 후보 적용 방지");
        check(w.SelectedPaths.Count == 0, "후보 검색/체크만으로 모델 추가 없음");
        w.Close();
        // 등록 단계는 신규 경로만 반환하고 기존 파일의 미적용 편집을 보존한다.
        main.LoadSample(); typeof(MainWindow).GetField("_sample", flags)!.SetValue(main, false);
        var grid = (DataGrid)main.FindName("ValuesGrid"); grid.SelectedItems.Add(grid.Items[0]);
        ((TextBox)main.FindName("ValueBox")).Text = "보존할 값";
        typeof(MainWindow).GetMethod("StageValues", flags)!.Invoke(main, [main, new RoutedEventArgs()]);
        List<string> Register(params string[] paths) => (List<string>)typeof(MainWindow).GetMethod("RegisterModels", flags)!.Invoke(main, [paths])!;
        check(Register(first, first, sub).Count == 2, "폴더 선택 신규 경로만 중복 없이 등록");
        check(Register(first).Count == 0, "기존 등록 파일만 선택하면 재조회 없음");
        check(((DataGrid)main.FindName("EditsGrid")).Items.Count == 1, "파일 추가 시 기존 변경 예정 보존");
        check(((ListBox)main.FindName("ModelsList")).Items.Cast<ModelItem>().Where(m => m.Path == first || m.Path == sub).All(m => m.Snapshot == null), "경로 등록은 실제 Revit 열기/저장 없음");
        bool invalid = false;
        try { Register(same, Path.Combine(root, "없음.rvt")); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { invalid = true; }
        check(invalid && !((ListBox)main.FindName("ModelsList")).Items.Cast<ModelItem>().Any(m => m.Path == same), "사라진 파일 포함이면 부분 등록 없이 보류");
    }
}
