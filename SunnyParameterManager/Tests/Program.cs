using SunnyParameterManager.Core;
using SunnyParameterManager.Desktop;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

internal static class Program
{
    private static int _count;
    private static readonly string RenderOutput = Path.Combine(AppContext.BaseDirectory, "renders", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); _count++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, name); }
    private static ParameterRow Row(string path = "A", string owner = "1") => new() { ModelPath = path, ModelTitle = path, OwnerId = owner, Scope = "유형", CategoryKey = "wall", Family = "기본벽", TypeName = "W1", ParameterKey = "guid:aaa", Name = "재료명", DataType = "text", Storage = "String", Editor = "텍스트", HasValue = true, Raw = "기존", Display = "기존" };
    private static ParameterRow Clone(ParameterRow r) => JsonSerializer.Deserialize<ParameterRow>(JsonSerializer.Serialize(r))!;

    [STAThread]
    public static int Main()
    {
        try
        {
            KacRuleTests.Run(Check, Reject);
            SpaceRuleTests.Run(Check, Reject);
            ReadBatchTests.Run(Check);
            var a = Row();
            Check(EditPlanner.Stage([a], "새값").Single().Value == "새값", "텍스트 직접 입력");
            Check(a.Raw == "기존", "미리보기는 원본 불변");
            Check(EditPlanner.Stage([a], "").Single().Value == "", "빈 텍스트 명시 삭제");
            Check(EditPlanner.Stage([a], "기존").Count == 0, "동일값 제외");
            var missing = Clone(a); missing.HasValue = false; missing.Display = "";
            Check(EditPlanner.Stage([missing], "").Count == 1, "없음과 빈 문자열 구분");
            Reject(() => EditPlanner.Stage([], "x"), "빈 선택 차단");
            Check(EditPlanner.Stage([a, a], "x").Count == 1, "중복 대상 제거");
            var readOnly = Clone(a); readOnly.OwnerId = "2"; readOnly.BlockReason = "수식";
            Reject(() => EditPlanner.Stage([a, readOnly], "x"), "일부 읽기전용 전체 미리보기 차단");
            var n = Clone(a); n.Editor = "정수";
            Check(EditPlanner.Validate(n, "-12") == "", "정수 지원");
            Check(EditPlanner.Validate(n, "1.2") != "", "정수 소수 거부");
            Check(EditPlanner.Validate(n, "2147483648") != "", "정수 오버플로 거부");
            n.Editor = "숫자";
            Check(EditPlanner.Validate(n, "2.5") == "", "실수 지원");
            Check(EditPlanner.Validate(n, "NaN") != "", "NaN 거부");
            Check(EditPlanner.Validate(n, "Infinity") != "", "무한대 거부");
            n.Editor = "선택"; n.Choices = [new("key1", "작업세트1")];
            Check(EditPlanner.Stage([n], "작업세트1").Single().ReferenceKey == "key1", "모델별 선택 키 저장");
            Check(EditPlanner.Validate(n, "9999") != "", "참조 숫자 입력 거부");
            n.Choices.Add(new("key2", "작업세트1"));
            Check(EditPlanner.Validate(n, "작업세트1") != "", "동명 참조 모호성 차단");
            var b = Row("B");
            Check(a.Key != b.Key, "동일 요소번호 모델 격리");
            var source = new ModelSnapshot { Path = "A", Title = "기준", Rows = [a] };
            a.Raw = a.Display = "복사값";
            Check(EditPlanner.CopyTypeValues(source, [b], false).Single().RawValue == "복사값", "공유 GUID 모델간 복사");
            Reject(() => EditPlanner.CopyTypeValues(source, [a], false), "자기 모델 복사 차단");
            b.Scope = "인스턴스";
            Reject(() => EditPlanner.CopyTypeValues(source, [b], false), "인스턴스 유형명 추정 복사 금지"); b.Scope = "유형";
            b.Family = "다른벽";
            Reject(() => EditPlanner.CopyTypeValues(source, [b], false), "패밀리 불일치 차단"); b.Family = a.Family;
            b.CategoryKey = "floor";
            Reject(() => EditPlanner.CopyTypeValues(source, [b], false), "카테고리 불일치 차단"); b.CategoryKey = a.CategoryKey;
            b.ParameterKey = "guid:bbb";
            Reject(() => EditPlanner.CopyTypeValues(source, [b], true), "동명 다른 GUID 추정 연결 금지");
            a.ParameterKey = "id:1"; b.ParameterKey = "id:2";
            Reject(() => EditPlanner.CopyTypeValues(source, [b], false), "일반 매개변수 승인 필수");
            Check(EditPlanner.CopyTypeValues(source, [b], true).Count == 1, "일반 매개변수 승인 연결");
            b.DataType = "number";
            Reject(() => EditPlanner.CopyTypeValues(source, [b], true), "동명 다른 자료형 차단"); b.DataType = a.DataType;
            source.Rows.Add(Clone(a));
            Reject(() => EditPlanner.CopyTypeValues(source, [b], true), "복수 원본 모호성 차단"); source.Rows.RemoveAt(1);
            a.HasValue = false;
            Reject(() => EditPlanner.CopyTypeValues(source, [b], true), "기준값 없음 자동 지우기 금지"); a.HasValue = true;
            var copy = Clone(b); EditPlanner.CheckFresh(b, copy); Check(true, "변경 전 일치 허용");
            foreach (var field in new[] { "OwnerId", "ParameterKey", "Scope", "Name", "DataType", "Storage", "Editor", "TypeName", "Unit", "Raw" })
            {
                copy = Clone(b); typeof(ParameterRow).GetProperty(field)!.SetValue(copy, "달라짐");
                Reject(() => EditPlanner.CheckFresh(b, copy), "최신성 검사 " + field);
            }
            copy = Clone(b); copy.HasValue = false; Reject(() => EditPlanner.CheckFresh(b, copy), "최신성 검사 값 유무");
            copy = Clone(b); copy.BlockReason = "읽기전용"; Reject(() => EditPlanner.CheckFresh(b, copy), "최신성 검사 권한");
            var model = new ModelSnapshot { Rows = [n], Catalogs = new() { ["workset"] = [new("x", "작업세트1")] } }; n.ChoiceCatalog = "workset";
            var roundtrip = JsonSerializer.Deserialize<ModelSnapshot>(JsonSerializer.Serialize(model))!; roundtrip.ConnectCatalogs();
            Check(roundtrip.Rows[0].Choices.Single().Key == "x", "IPC 선택 목록 재연결");
            Check(!JsonSerializer.Serialize(n).Contains("Choices"), "대용량 선택 목록 중복 직렬화 방지");
            bool invalidSession = false;
            try { Protocol.SessionFolder("../other"); } catch (FormatException) { invalidSession = true; }
            Check(invalidSession, "세션 경로 탈출 차단");
            RuleTests.Run(Check, Reject);
            return UiTests();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int UiTests()
    {
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            QueryTests.Run(Check, Reject);
            var w = new MainWindow();
            Check(((FrameworkElement)w.FindName("WelcomePanel")).Visibility == Visibility.Visible, "첫 화면 파일 선택 안내");
            Check(!((Expander)w.FindName("AdvancedSettings")).IsExpanded && !((Expander)w.FindName("CopyExpander")).IsExpanded, "세부 설정 기본 숨김");
            Check(((Button)w.FindName("RulesButton")).IsEnabled && !((Button)w.FindName("ReviewRulesButton")).IsEnabled, "조회 전 룰셋 선택 허용·변경 검토 방지");
            Render(w, "welcome", 1320, 820);
            Render(w, "welcome-1080", 1080, 700);
            w.LoadSample();
            Check(((FrameworkElement)w.FindName("WelcomePanel")).Visibility == Visibility.Collapsed && ((Button)w.FindName("RulesButton")).IsEnabled, "파일 준비 후 룰셋 버튼 사용");
            var grid = (DataGrid)w.FindName("ValuesGrid");
            Check(grid.Items.Count == 36, "실제 화면 샘플 36행");
            Check(grid.SelectionMode == DataGridSelectionMode.Extended, "Shift 범위 선택 기본 컨트롤");
            var before = ((ParameterRow)grid.Items[0]).Raw;
            grid.SelectedItems.Add(grid.Items[0]);
            ((TextBox)w.FindName("ValueBox")).Text = "직접 테스트";
            Check(((TextBlock)w.FindName("InputPreview")).Text.Contains("직접 테스트"), "입력 즉시 기존값 변경값 표시");
            Invoke(w, "StageValues");
            var edits = (DataGrid)w.FindName("EditsGrid");
            Check(edits.Items.Count == 1 && ((ValueEdit)edits.Items[0]).Value == "직접 테스트", "실제 UI 미리보기 연결");
            Check(((ParameterRow)grid.Items[0]).Raw == before, "실제 UI 원본 보존");
            Check(!((Button)w.FindName("ApplyButton")).IsEnabled, "샘플 실제 적용 차단");
            ((TextBox)w.FindName("ValueBox")).Text = before; Invoke(w, "StageValues");
            Check(edits.Items.Count == 0, "기존값 입력 시 누적 변경 취소");
            ((TextBox)w.FindName("ValueBox")).Text = "다른 값"; Invoke(w, "StageValues");
            ((TextBox)w.FindName("SearchBox")).Text = "없는 검색어";
            Check(grid.Items.Count == 0 && edits.Items.Count == 1, "검색 변경 후 누적 작업 보존");
            ((TextBox)w.FindName("SearchBox")).Text = "";
            Check(grid.Items.Count == 36, "검색 해제 복원");
            Check(!((Button)w.FindName("StageButton")).IsEnabled, "선택 없는 입력 방지");
            Invoke(w, "ReviewChanges");
            Check(((TabControl)w.FindName("MainTabs")).SelectedIndex == 1 && edits.Items.Count == 1, "검토 이동은 저장 없이 미리보기만");
            ((TabControl)w.FindName("MainTabs")).SelectedIndex = 0;
            foreach (var size in new[] { (1320, 820), (1080, 700) })
            {
                var content = (FrameworkElement)w.Content;
                content.Measure(new Size(size.Item1, size.Item2)); content.Arrange(new Rect(0, 0, size.Item1, size.Item2)); content.UpdateLayout();
                Render(w, "main-" + size.Item1, size.Item1, size.Item2);
                Check(grid.ActualWidth > 500 && grid.ActualHeight > 145, $"목록 표시 영역 {size.Item1} ({grid.ActualWidth:N0}×{grid.ActualHeight:N0})");
            }
            w.LoadSample();
            var rules = new RuleSet { Version = 1, Name = "연습 룰셋", SampleOnly = true, Rules = [new() { Name = "업체명 입력", Match = new() { Scope = "유형", Type = "KAC-W", TypeMatch = "시작" }, Set = [new() { Name = "업체명", DataType = "text", Storage = "String", Value = "연습 업체" }] }] };
            w.SelectRules(rules);
            Render(w, "query-ready-1080", 1080, 700);
            ((TextBox)w.FindName("SearchBox")).Text = "해당없음";
            w.PrepareRules(rules);
            Check(edits.Items.Count == 36 && ((TabControl)w.FindName("MainTabs")).SelectedIndex == 1, "룰셋은 검색/선택과 무관하게 등록 파일 전체 평가 후 검토 이동");
            Check(((TextBox)w.FindName("RuleSummary")).Text.Contains("연습 룰셋") && !((Button)w.FindName("ApplyButton")).IsEnabled, "룰셋 요약 및 샘플 저장 차단");
            w.PrepareRules(rules); Check(edits.Items.Count == 36, "룰셋 재불러오기 중복 누적 방지");
            rules.Rules[0].Set[0].Value = "충돌";
            Reject(() => w.PrepareRules(rules), "UI 룰셋 충돌 보류");
            Check(edits.Items.Count == 36 && ((ValueEdit)edits.Items[0]).Value == "연습 업체", "UI 룰셋 실패 기존 작업 보호");
            Render(w, "rules-review", 1320, 820);
            w.LoadSample();
            w.PrepareRules(RulePlanner.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "연습용.sunnyrules.json"))));
            Check(edits.Items.Count == 36 && ((TextBox)w.FindName("RuleSummary")).Text.Contains("연습 전용"), "실제 배포 연습용 파일 불러오기");
            FieldSearchTests();
            FailedApplyTests();
            FolderSearchTests.Run(Check, Reject, Render);
            ViewEditingTests.Run(Check, Reject, Render);
            LinkRemovalTests.Run(Check, Reject, Render);
            Console.WriteLine("화면 렌더: " + RenderOutput);
            Console.WriteLine($"TOTAL {_count} PASS · 실제 Revit/중앙 서버 검증은 별도 필요");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void FailedApplyTests()
    {
        var w = new MainWindow(); w.LoadSample();
        Check(w.Title.Contains(Protocol.Release), "창 제목에 실제 실행 버전 표시");
        var models = ((ListBox)w.FindName("ModelsList")).Items.Cast<ModelItem>().ToList();
        var grid = (DataGrid)w.FindName("ValuesGrid");
        foreach (var row in grid.Items.Cast<ParameterRow>()) grid.SelectedItems.Add(row);
        ((TextBox)w.FindName("ValueBox")).Text = "재시도 테스트"; Invoke(w, "StageValues");
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(MainWindow).GetField("_sample", flags)!.SetValue(w, false);
        void Refresh() => typeof(MainWindow).GetMethod("UpdatePending", flags)!.Invoke(w, null);
        Refresh();
        Check(((Button)w.FindName("ApplyButton")).IsEnabled, "정상 미리보기는 적용 가능");
        models[0].Snapshot!.RequiresRequery = true; Refresh();
        Check(!((Button)w.FindName("ApplyButton")).IsEnabled && !((Button)w.FindName("ReviewRulesButton")).IsEnabled, "실패 조회값 재적용 및 룰셋 반복 차단");
        Reject(() => w.PrepareRules(new RuleSet()), "직접 룰셋 호출도 실패 조회값 차단");
        var restored = JsonSerializer.Deserialize<WorkspaceFile>(JsonSerializer.Serialize(new WorkspaceFile { Models = [models[0].Snapshot!] }))!;
        Check(restored.Models.Single().RequiresRequery, "작업 저장과 불러오기에서 실패 상태 보존");
        Check(!JsonSerializer.Deserialize<ModelSnapshot>("{}")!.RequiresRequery, "이전 조회 파일 기본값 호환");
        var edits = (DataGrid)w.FindName("EditsGrid");
        var keep = edits.Items.Cast<ValueEdit>().Where(e => e.Original.ModelPath != models[0].Path).ToList();
        typeof(MainWindow).GetMethod("ResetFailedModelsForRequery", flags)!.Invoke(w, null);
        Check(models[0].Snapshot == null && models[1].Snapshot != null, "실패 모델만 조회값 초기화");
        Check(keep.Count > 0 && edits.Items.Cast<ValueEdit>().SequenceEqual(keep), "실패 모델 미적용 변경만 비우고 다른 모델 예정 보존");
        Check(((Button)w.FindName("ApplyButton")).IsEnabled, "실패 파일 제외 후 정상 파일 변경 적용 가능");
        Check(!((Button)w.FindName("ReviewRulesButton")).IsEnabled, "재조회 완료 전 전체 룰셋 평가 차단");
    }
    private static void FieldSearchTests()
    {
        var w = new MainWindow(); w.LoadSample();
        var search = (TextBox)w.FindName("FieldSearchBox");
        var combo = (ComboBox)w.FindName("FieldCombo");
        var status = (TextBlock)w.FindName("FieldSearchStatus");
        var grid = (DataGrid)w.FindName("ValuesGrid");
        var input = (TextBox)w.FindName("ValueBox");
        var edits = (DataGrid)w.FindName("EditsGrid");
        var row = grid.Items[0]; grid.SelectedItems.Add(row); input.Text = "검색 전 입력"; Invoke(w, "StageValues");
        search.Text = "업체";
        Check(combo.Items.Cast<string>().SequenceEqual(new[] { "재료명", "업체명" }) && status.Text == "검색 1개", "매개변수 부분 검색 + 현재 선택 별도 유지");
        Check((string)combo.SelectedItem == "재료명" && grid.SelectedItems.Contains(row) && input.Text == "검색 전 입력", "매개변수 검색은 선택 행과 입력값 불변");
        Check(edits.Items.Count == 1 && ((ValueEdit)edits.Items[0]).Value == "검색 전 입력", "매개변수 검색은 변경 예정 보존");
        search.Text = "없는매개변수";
        Check(status.Text == "검색 0개" && combo.Items.Count == 1 && (string)combo.SelectedItem == "재료명", "검색 결과 없음에도 다른 항목 자동 선택 금지");
        Check(grid.Items.Count == 36 && grid.Items.Cast<ParameterRow>().All(r => r.Name == "재료명"), "검색 실패 시 모든 매개변수 행으로 확장 금지");
        Invoke(w, "ClearFieldSearch");
        Check(search.Text == "" && combo.Items.Count == 4 && (string)combo.SelectedItem == "재료명" && grid.SelectedItems.Contains(row), "검색 지우기는 전체 후보 복원 및 선택 유지");
        Check(!((Button)w.FindName("ClearFieldSearchButton")).IsEnabled, "빈 검색 지우기 버튼 비활성");
        search.Text = "  ifc  ";
        Check(combo.Items.Contains("다음으로 IFC로 내보내기") && status.Text == "검색 1개", "영문 대소문자 무시 및 앞뒤 공백 제거");
        combo.SelectedItem = "다음으로 IFC로 내보내기";
        Check(combo.Items.Count == 1 && grid.Items.Count == 36 && grid.Items.Cast<ParameterRow>().All(r => r.Name == "다음으로 IFC로 내보내기"), "검색 결과 선택 후 해당 필드만 표시 및 이전 유지 항목 제거");
        Check(input.Text == "" && grid.SelectedItems.Count == 0 && edits.Items.Count == 1, "실제 필드 변경은 입력 초기화하되 누적 변경 보존");
        ((TextBox)w.FindName("SearchBox")).Text = "W01";
        search.Text = "공사코드";
        Check(grid.Items.Count == 2 && combo.Items.Contains("조달청표준공사코드"), "이름/현재 값 검색과 매개변수 후보 검색 독립");
        ((ComboBox)w.FindName("ScopeCombo")).SelectedIndex = 1;
        Check(grid.Items.Count == 0 && combo.Items.Contains("조달청표준공사코드"), "범위가 달라도 검색 후보와 선택 필드 유지");
        ((ComboBox)w.FindName("ScopeCombo")).SelectedIndex = 0;
        ((ListBox)w.FindName("ModelsList")).SelectedIndex = 0;
        Check(grid.Items.Count == 1 && (string)combo.SelectedItem == "다음으로 IFC로 내보내기", "파일 필터 변경 시 검색과 선택 필드 유지");
        search.Text = "업체";
        combo.SelectedItem = "업체명";
        Render(w, "field-search", 1080, 700);
    }
    private static void Invoke(MainWindow w, string method) => typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, [w, new RoutedEventArgs()]);
    private static void Render(Window w, string name, int width, int height)
    {
        var content = (FrameworkElement)w.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(RenderOutput);
        using var stream = File.Create(Path.Combine(RenderOutput, name + ".png")); encoder.Save(stream);
    }
}
