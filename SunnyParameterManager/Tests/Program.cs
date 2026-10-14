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
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); _count++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, name); }
    private static ParameterRow Row(string path = "A", string owner = "1") => new() { ModelPath = path, ModelTitle = path, OwnerId = owner, Scope = "유형", CategoryKey = "wall", Family = "기본벽", TypeName = "W1", ParameterKey = "guid:aaa", Name = "재료명", DataType = "text", Storage = "String", Editor = "텍스트", HasValue = true, Raw = "기존", Display = "기존" };
    private static ParameterRow Clone(ParameterRow r) => JsonSerializer.Deserialize<ParameterRow>(JsonSerializer.Serialize(r))!;

    [STAThread]
    public static int Main()
    {
        try
        {
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
            return UiTests();
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int UiTests()
    {
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var w = new MainWindow(); w.LoadSample();
            var grid = (DataGrid)w.FindName("ValuesGrid");
            Check(grid.Items.Count == 36, "실제 화면 샘플 36행");
            Check(grid.SelectionMode == DataGridSelectionMode.Extended, "Shift 범위 선택 기본 컨트롤");
            var before = ((ParameterRow)grid.Items[0]).Raw;
            grid.SelectedItems.Add(grid.Items[0]);
            ((TextBox)w.FindName("ValueBox")).Text = "직접 테스트";
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
            var output = Path.Combine(AppContext.BaseDirectory, "renders"); Directory.CreateDirectory(output);
            foreach (var size in new[] { (1320, 820), (1080, 700) })
            {
                var content = (FrameworkElement)w.Content;
                content.Measure(new Size(size.Item1, size.Item2)); content.Arrange(new Rect(0, 0, size.Item1, size.Item2)); content.UpdateLayout();
                Check(grid.ActualWidth > 500 && grid.ActualHeight > 180, "목록 표시 영역 " + size.Item1);
                var bitmap = new RenderTargetBitmap(size.Item1, size.Item2, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, "main-" + size.Item1 + ".png")); encoder.Save(stream);
            }
            Console.WriteLine($"TOTAL {_count} PASS · 실제 Revit/중앙 서버 검증은 별도 필요");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Invoke(MainWindow w, string method) => typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, [w, new RoutedEventArgs()]);
}
