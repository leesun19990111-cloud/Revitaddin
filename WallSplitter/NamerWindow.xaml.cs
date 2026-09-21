using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;
// Autodesk.Revit.DB.Grid(데이텀 그리드)와 이름이 겹치므로 WPF 쪽은 항상 별칭으로 쓴다
// (Visibility/Control/Color/Binding과 같은 종류의 충돌 - 루트 CLAUDE.md 참고).
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    public partial class NamerWindow : Window
    {
        // internal: ChangeLogEntry.Category와 ChangeReplayEngine이 "어느 카테고리였는지"/"대상 문서에서
        // 후보를 어떻게 모으는지"를 그대로 재사용하기 위해 이 창 바깥에서도 접근해야 한다.
        internal enum NamerCategory { View, Sheet, Family, Type, Legend, Schedule, Material, ViewTemplate }
        private enum NamerMode { Replace, Insert, Swap, DeleteRange }

        private sealed class RenameRow
        {
            public ElementId ElementId = ElementId.InvalidElementId;
            public string OriginalName = "";
            public CheckBox CheckBox = null!;
            // 기존 이름 칸은 TextBlock 하나가 아니라 Grid다 - 그 안에 이름 TextBlock과, 마우스를 올렸을
            // 때만 오른쪽 끝에 나타나는 '특성' 버튼, 그리고 더블클릭 시의 인라인 편집칸이 겹쳐 놓인다.
            public WpfGrid OldNameHost = null!;
            public TextBlock OldNameText = null!;
            public Button PropsButton = null!;
            public TextBox? Editor;
            public TextBlock NewNameText = null!;
        }

        // 2026-09-21부터 이 창은 **모드리스**다 - NAMER를 띄워 둔 채로 Revit에서 계속 작업할 수 있고,
        // '특성' 버튼이 Revit 기본 창을 띄울 수 있게 하려면 반드시 모드리스여야 한다
        // (PostCommand는 "지금 명령이 끝난 뒤"에 실행되므로 모달인 동안에는 영영 실행되지 않는다 —
        // 자세한 설명은 NamerExternalEventHandler 참고). 그래서 모델을 건드리는 일은 전부
        // ExternalEvent를 거치고, 이 창 자체는 DialogResult를 쓰지 않는다(모드리스 창에서 설정하면 예외).
        public static NamerWindow? Instance { get; private set; }

        // **UIApplication을 여기에 저장하지 말 것 (2026-09-21 확인된 치명적 크래시).**
        // ExternalCommandData.Application(UIApplication)은 그 명령이 실행되는 동안에만 유효하다.
        // v84에서 이걸 필드에 담아 두고 창이 닫힐 때 `_uiApp.Application.DocumentClosing -= ...`을
        // 호출했는데, 명령이 끝난 지 한참 뒤라 여기서 관리 예외가 났고 WPF Closed 핸들러의 예외는
        // 아무도 잡지 않아 그대로 Revit 네이티브로 넘어가 "복구 불가능한 오류"가 됐다(저널 기록:
        // Registering DocumentClosing 두 번, Unregistering은 한 번도 없음, ExceptionCode=0xe0434352).
        // Revit이 필요한 일은 전부 ExternalEvent가 **그때그때 넘겨주는** UIApplication으로 한다.
        // Autodesk.Revit.UI에도 Button이 있어(리본 버튼) using으로 통째로 끌어오면 WPF Button과 충돌한다 -
        // 이 파일에서는 Revit UI 타입을 전부 전체 이름으로 쓴다.
        private readonly NamerExternalEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _event;
        private Document _doc;
        private HashSet<ElementId> _preSelectedIds;
        private readonly HashSet<ElementId> _checkedIds = new();
        private readonly HashSet<NamerCategory> _categoriesInitialized = new();

        // "적용"은 이 두 딕셔너리만 갱신하고 Revit 모델은 건드리지 않는다. 실제 Transaction은
        // "최종 적용"을 눌러 창이 닫힐 때 NamerCommand가 (TrueOriginal, Working)이 서로 다른 것만 모아 처리한다.
        // 여러 카테고리를 오가며 작업해도 전부 누적되도록 카테고리 전환과 무관하게 유지한다.
        private readonly Dictionary<ElementId, string> _trueOriginalNames = new();
        private readonly Dictionary<ElementId, string> _workingNames = new();

        private NamerCategory _category = NamerCategory.View;
        private NamerMode _mode = NamerMode.Replace;

        // 여러 행을 드래그해서 한 번에 체크/해제하기 위한 상태.
        private bool _dragging;
        private bool _dragTargetChecked;
        private RenameRow? _lastDragRow;
        private List<Element> _categoryElements = new();
        private readonly List<RenameRow> _rows = new();

        // 목록을 통째로 렌더링하는 대신 페이지 단위로 나눠 그린다 (아래 RenderMoreRows 설명 참고).
        private const int PageSize = 200;

        // 인라인 편집칸의 높이(=기존 이름 칸이 항상 확보하는 높이). 편집을 열고 닫을 때 행 높이가
        // 달라지지 않게 하려는 값이다 - 위 oldNameHost.MinHeight 주석 참고.
        private const double InlineEditHeight = 20;
        private List<Element> _filteredElements = new();
        private int _renderedCount;
        private Button? _loadMoreButton;

        internal NamerWindow(Document doc, List<ElementId> preSelectedIds)
        {
            InitializeComponent();
            Instance = this;
            _doc = doc;
            _preSelectedIds = new HashSet<ElementId>(preSelectedIds);

            _handler = new NamerExternalEventHandler { TargetDocument = doc };
            _event = Autodesk.Revit.UI.ExternalEvent.Create(_handler);

            // 문서가 닫힐 때 이 창도 닫아야 하지만(아래 CloseForDocument 참고) **여기서 Revit 이벤트를
            // 구독하지 않는다** - 구독/해지 모두 세션 내내 유효한 객체로 해야 하고, 이 창이 들고 있는
            // 것은 명령 실행 중에만 유효한 것뿐이기 때문이다. App.OnStartup이 ControlledApplication에
            // 이미 붙여 둔 DocumentClosing 핸들러가 대신 CloseForDocument를 불러 준다.
            Closed += (_, _) =>
            {
                // WPF 이벤트 핸들러에서 예외가 새어 나가면 Revit이 통째로 죽는다(위 UIApplication
                // 주석의 그 사고) - 닫는 길목은 무슨 일이 있어도 조용히 끝나야 한다.
                try { if (Instance == this) Instance = null; }
                catch { /* 창을 닫는 중이라 사용자에게 알릴 것이 없다 */ }
            };

            // 인라인 편집 중에 창 어디를 클릭하든 편집에서 빠져나오게 한다. 터널링(Preview) 이벤트라
            // 클릭 대상이 그 일을 처리하기 **전에** 먼저 들어오고, handledEventsToo로 붙여 두면 버튼처럼
            // 이벤트를 소비하는 컨트롤을 클릭해도 놓치지 않는다.
            AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(Window_PreviewMouseDown), true);
            Deactivated += NamerWindow_Deactivated;

            NamerCategory initial = DetectInitialCategory(doc, preSelectedIds);
            SetCategoryRadio(initial);
            LoadCategory(initial);

            // 생성자 시점(Loaded 전)엔 OldNameColumn/NewNameColumn의 ActualWidth가 아직 0이라,
            // LoadCategory가 바로 그린 첫 페이지 행들은 이름 TextBlock의 Width가 0으로 잡혀 텍스트가
            // 안 보이는 것처럼(체크는 되지만 투명하게) 렌더링된다. 필터에 한 글자 치면 RenderRows가
            // 다시 그려지는데 그때는 이미 창이 표시돼 ActualWidth가 정상이라 그제서야 보이던 문제.
            // 창이 실제로 표시된 뒤(Loaded) 이미 그려진 행들의 너비를 한 번 다시 써서 고친다.
            Loaded += NamerWindow_Loaded;
        }

        // **모드리스 창의 WPF 이벤트 핸들러에서 예외가 새어 나가면 Revit이 그대로 죽는다.**
        // 모달일 때는 ShowDialog가 IExternalCommand.Execute 프레임 안에서 돌아, 예외가 Revit의 명령
        // 래퍼까지 올라가 "애드인 오류" 대화상자로 끝났다. 모드리스에는 우리 핸들러와 Revit 네이티브
        // 메시지 루프 사이에 관리 프레임이 하나도 없어서, 같은 예외가 0xe0434352 치명적 오류가 된다
        // (2026-09-21 실제 발생). 그래서 Revit을 건드리거나 창을 닫는 길목은 전부 이걸로 감싼다.
        private void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (System.Exception ex)
            {
                ShowStatus($"{what} 중 문제가 생겼습니다: {ex.GetBaseException().Message}");
            }
        }

        // App.cs의 DocumentClosing 핸들러(OnStartup에서 ControlledApplication에 등록된 것)가 호출한다.
        // 모드리스라 목록이 살아 있는 동안 사용자가 그 문서를 닫아 버릴 수 있는데, _categoryElements가
        // 들고 있는 Element는 그 순간 전부 무효가 되어 필터에 한 글자만 쳐도 el.Name에서 예외가 난다.
        internal void CloseForDocument(Document closing)
        {
            if (DocKey(closing) != DocKey(_doc)) return;
            Close();
        }

        // Document는 API 래퍼 객체라 같은 열린 문서라도 조회 시점이 다르면 참조가 다를 수 있다 -
        // 경로(저장 안 된 문서는 제목)를 식별자로 쓴다(경고Pick에서 확인된 라이브 버그와 같은 이유).
        private static string DocKey(Document doc) => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

        private void NamerWindow_Loaded(object sender, RoutedEventArgs e) => Guard("창 표시", () =>
        {
            foreach (RenameRow row in _rows)
            {
                row.OldNameHost.Width = OldNameColumn.ActualWidth;
                row.NewNameText.Width = NewNameColumn.ActualWidth;
            }
        });

        private static NamerCategory DetectInitialCategory(Document doc, List<ElementId> ids)
        {
            foreach (ElementId id in ids)
            {
                Element? el = doc.GetElement(id);
                if (el is ViewSheet) return NamerCategory.Sheet;
                if (el is ViewSchedule) return NamerCategory.Schedule;
                if (el is View vt && vt.IsTemplate) return NamerCategory.ViewTemplate;
                if (el is View v && v.ViewType == ViewType.Legend) return NamerCategory.Legend;
                if (el is View) return NamerCategory.View;
                if (el is Family) return NamerCategory.Family;
                if (el is ElementType) return NamerCategory.Type;
                if (el is Material) return NamerCategory.Material;
            }
            return NamerCategory.View;
        }

        private void SetCategoryRadio(NamerCategory category)
        {
            switch (category)
            {
                case NamerCategory.View: CategoryViewRadio.IsChecked = true; break;
                case NamerCategory.Sheet: CategorySheetRadio.IsChecked = true; break;
                case NamerCategory.Family: CategoryFamilyRadio.IsChecked = true; break;
                case NamerCategory.Type: CategoryTypeRadio.IsChecked = true; break;
                case NamerCategory.Legend: CategoryLegendRadio.IsChecked = true; break;
                case NamerCategory.Schedule: CategoryScheduleRadio.IsChecked = true; break;
                case NamerCategory.Material: CategoryMaterialRadio.IsChecked = true; break;
                case NamerCategory.ViewTemplate: CategoryViewTemplateRadio.IsChecked = true; break;
            }
        }

        // ===================== 대상 분류 =====================

        // Revit 문서를 훑는다(CollectCandidates) - 모드리스 창에서 여기서 예외가 새면 Revit이 죽는다.
        private void CategoryRadio_Checked(object sender, RoutedEventArgs e) => Guard("대상 목록 불러오기", () =>
        {
            if (CategoryViewRadio == null) return; // InitializeComponent 도중 발생하는 초기 Checked 이벤트 방지

            NamerCategory category =
                CategoryViewRadio.IsChecked == true ? NamerCategory.View :
                CategorySheetRadio.IsChecked == true ? NamerCategory.Sheet :
                CategoryFamilyRadio.IsChecked == true ? NamerCategory.Family :
                CategoryTypeRadio.IsChecked == true ? NamerCategory.Type :
                CategoryLegendRadio.IsChecked == true ? NamerCategory.Legend :
                CategoryScheduleRadio.IsChecked == true ? NamerCategory.Schedule :
                CategoryViewTemplateRadio.IsChecked == true ? NamerCategory.ViewTemplate :
                NamerCategory.Material;

            LoadCategory(category);
        });

        private void LoadCategory(NamerCategory category)
        {
            _category = category;
            _categoryElements = CollectCandidates(_doc, category);
            // _trueOriginalNames/_workingNames는 여기서 전체를 미리 채우지 않는다 — "유형"처럼 카테고리가
            // 커지면 요소 하나하나 Name을 읽는 것 자체가(체크 여부와 무관하게 전부) 눈에 띄는 지연이었다.
            // 대신 WorkingNameOf가 항목이 없을 때 el.Name을 직접 읽는 폴백을 쓰고, 실제로 이름이 바뀌는
            // 순간(ApplyButton_Click)에만 그 요소 하나에 대해서만 기록한다.

            if (!_categoriesInitialized.Contains(category))
            {
                _categoriesInitialized.Add(category);
                HashSet<ElementId> resolvedPreSelection = ResolvePreSelectionForCategory(category);
                List<ElementId> preSelectedHere = _categoryElements
                    .Select(el => el.Id)
                    .Where(id => resolvedPreSelection.Contains(id))
                    .ToList();

                // 미리 선택한 게 있으면 그것만 체크한다. 없으면 "전부 체크"가 아니라 "아무것도 체크 안 함"으로
                // 시작한다 — 특히 "유형"은 문서 전체의 ElementType(전선/케이블 종류 등 관련 없는 것까지 포함)이라
                // 잘못 걸리면 그대로 최종 적용 시 엉뚱한 요소들의 이름까지 바뀌어버리는 사고로 실제로 이어졌다.
                foreach (ElementId id in preSelectedHere) _checkedIds.Add(id);
            }

            if (FilterBox != null) FilterBox.Text = "";
            // 중복 이름 처리 정책(병합/숫자 접미사)은 재료를 이름 변경할 때만 의미가 있으므로, 다른 카테고리를
            // 보는 동안은 숨긴다 - 라디오 자체의 선택 상태는 숨겨져 있어도 그대로 유지되므로, 다시 "재료"로
            // 돌아오거나 "최종 적용"을 누를 때 마지막으로 골랐던 정책이 그대로 남아있다.
            if (MaterialDuplicatePanel != null)
                MaterialDuplicatePanel.Visibility = category == NamerCategory.Material
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            RenderRows();
            UpdatePendingChangesText();
        }

        // Revit에서 미리 선택한 건 대부분 모델 "인스턴스"(벽, 배선, 패밀리 인스턴스 등)이지, 유형/패밀리
        // 그 자체가 아니다. 그래서 인스턴스 id를 그대로 _categoryElements(유형/패밀리 목록)와 비교하면 절대
        // 일치하지 않는다 — 인스턴스가 속한 유형/패밀리로 옮겨서 비교해야 "선택한 것만" 정확히 체크된다.
        private HashSet<ElementId> ResolvePreSelectionForCategory(NamerCategory category)
        {
            var resolved = new HashSet<ElementId>();
            foreach (ElementId id in _preSelectedIds)
            {
                Element? el = _doc.GetElement(id);
                if (el == null) continue;

                switch (category)
                {
                    case NamerCategory.Type:
                        if (el is ElementType) resolved.Add(el.Id);
                        else
                        {
                            ElementId typeId = el.GetTypeId();
                            if (typeId != ElementId.InvalidElementId) resolved.Add(typeId);
                        }
                        break;
                    case NamerCategory.Family:
                        if (el is Family family) resolved.Add(family.Id);
                        else if (el is FamilySymbol symbol) resolved.Add(symbol.Family.Id);
                        else if (el is FamilyInstance instance) resolved.Add(instance.Symbol.Family.Id);
                        else
                        {
                            // 유형이지만 FamilySymbol이 아닌 경우(시스템 패밀리 등)는 대응하는 Family 개념이 없다.
                            ElementId typeId = el.GetTypeId();
                            if (typeId != ElementId.InvalidElementId && _doc.GetElement(typeId) is FamilySymbol typeSymbol)
                                resolved.Add(typeSymbol.Family.Id);
                        }
                        break;
                    default:
                        resolved.Add(el.Id); // 뷰/시트/범례/일람표는 선택한 그 요소 자체가 목록의 항목과 같은 것이다.
                        break;
                }
            }
            return resolved;
        }

        // internal: ChangeReplayEngine이 다른 문서에서 같은 카테고리의 "이름이 Key인 요소"를 찾을 때 그대로
        // 재사용한다 - 카테고리별 제외 규칙(예: 뷰 카테고리는 범례/일람표/뷰 템플릿을 제외)이 두 곳에서
        // 따로 유지되면 언젠가 조용히 어긋나므로, 한 곳만 두고 공유한다.
        internal static List<Element> CollectCandidates(Document doc, NamerCategory category)
        {
            switch (category)
            {
                case NamerCategory.View:
                    // 범례/일람표는 각각 별도 카테고리로 빠지므로 여기서는 제외한다 (둘 다 View의 하위 종류라 안 그러면 중복됨).
                    return new FilteredElementCollector(doc).OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule && v.ViewType != ViewType.Legend)
                        .Cast<Element>()
                        .OrderBy(e => e.Name)
                        .ToList();
                case NamerCategory.Sheet:
                    return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                        .OrderBy(e => e.Name)
                        .ToList();
                case NamerCategory.Family:
                    return new FilteredElementCollector(doc).OfClass(typeof(Family))
                        .OrderBy(e => e.Name)
                        .ToList();
                case NamerCategory.Type:
                    return new FilteredElementCollector(doc).WhereElementIsElementType()
                        .OrderBy(e => e.Name)
                        .ToList();
                case NamerCategory.Legend:
                    return new FilteredElementCollector(doc).OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend)
                        .Cast<Element>()
                        .OrderBy(e => e.Name)
                        .ToList();
                case NamerCategory.Schedule:
                    return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule))
                        .Cast<ViewSchedule>()
                        .Where(vs => !vs.IsTemplate)
                        .Cast<Element>()
                        .OrderBy(e => e.Name)
                        .ToList();
                case NamerCategory.Material:
                    // Material은 ElementType이 아니라 Element 자체이지만(WhereElementIsElementType에 안 걸림),
                    // Name을 그대로 get/set할 수 있어 다른 카테고리와 동일하게 다룰 수 있다.
                    return new FilteredElementCollector(doc).OfClass(typeof(Material))
                        .OrderBy(e => e.Name)
                        .ToList();
                case NamerCategory.ViewTemplate:
                    // 뷰 템플릿은 IsTemplate == true인 View일 뿐 별도 클래스가 없다 - "뷰" 카테고리는
                    // v.IsTemplate으로 이들을 명시적으로 제외하므로 여기서만 별도로 모아야 중복이 안 생긴다.
                    return new FilteredElementCollector(doc).OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => v.IsTemplate)
                        .Cast<Element>()
                        .OrderBy(e => e.Name)
                        .ToList();
                default:
                    return new List<Element>();
            }
        }

        // ===================== 작업 모드 =====================

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (ReplacePanel == null) return;

            _mode = ModeReplaceRadio.IsChecked == true ? NamerMode.Replace :
                    ModeInsertRadio.IsChecked == true ? NamerMode.Insert :
                    ModeSwapRadio.IsChecked == true ? NamerMode.Swap :
                    NamerMode.DeleteRange;

            ReplacePanel.Visibility = _mode == NamerMode.Replace ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            InsertPanel.Visibility = _mode == NamerMode.Insert ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            SwapPanel.Visibility = _mode == NamerMode.Swap ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            DeleteRangePanel.Visibility = _mode == NamerMode.DeleteRange ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            RefreshPreview();
        }

        private void Param_TextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();

        private string ComputeNewName(string oldName)
        {
            switch (_mode)
            {
                case NamerMode.Replace:
                {
                    string find = FindBox.Text ?? "";
                    if (string.IsNullOrEmpty(find)) return oldName;
                    return oldName.Replace(find, ReplaceBox.Text ?? "");
                }
                case NamerMode.Insert:
                {
                    if (!int.TryParse(PositionBox.Text, out int position)) return oldName;
                    string insertText = InsertTextBox.Text ?? "";
                    if (insertText.Length == 0) return oldName;
                    int index = ClampInt(position - 1, 0, oldName.Length);
                    return oldName.Insert(index, insertText);
                }
                case NamerMode.Swap:
                {
                    string delimiter = DelimiterBox.Text ?? "";
                    if (delimiter.Length == 0) return oldName;
                    int idx = oldName.IndexOf(delimiter, StringComparison.Ordinal);
                    if (idx < 0) return oldName;
                    string left = oldName.Substring(0, idx);
                    string right = oldName.Substring(idx + delimiter.Length);
                    return right + delimiter + left;
                }
                case NamerMode.DeleteRange:
                {
                    if (!int.TryParse(StartPosBox.Text, out int startPos)) return oldName;
                    if (!int.TryParse(EndPosBox.Text, out int endPos)) return oldName;
                    if (startPos < 1 || endPos < startPos || oldName.Length == 0) return oldName;

                    int start = startPos - 1;
                    if (start >= oldName.Length) return oldName; // 시작 위치가 이름 길이를 넘으면 변경 없음

                    int end = ClampInt(endPos - 1, start, oldName.Length - 1);
                    int count = end - start + 1;
                    return oldName.Remove(start, count);
                }
                default:
                    return oldName;
            }
        }

        private string WorkingNameOf(Element el) =>
            _workingNames.TryGetValue(el.Id, out string? name) ? name : SafeNameOf(el);

        // 모드리스라 목록을 만든 뒤에도 요소가 삭제되거나 문서가 닫힐 수 있다 - 그때 el.Name은 예외를
        // 던지고, WPF 이벤트 핸들러에서 새어 나간 예외는 곧바로 Revit 충돌 대화상자가 된다.
        private static string SafeNameOf(Element el)
        {
            try { return el.IsValidObject ? (el.Name ?? "") : ""; }
            catch { return ""; }
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ===================== 목록 렌더링 =====================

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => Guard("필터", () => RenderRows());

        // ComboBoxItem의 SelectedIndex="0" 기본값도 RadioButton의 IsChecked="True"처럼 InitializeComponent
        // 도중 SelectionChanged를 먼저 발생시킨다 - 이 시점엔 Row4의 ItemsPanel이 아직 연결 전이라
        // RenderRows()가 바로 NullReferenceException을 낸다(ModeRadio_Checked의 ReplacePanel 가드와 동일한 이유).
        private void FilterModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Guard("필터", () =>
        {
            if (ItemsPanel == null) return;
            RenderRows();
        });

        private void RenderRows() => RenderRows(false);

        // preserveView: 지금까지 "더 보기"로 펼쳐 둔 분량과 스크롤 위치를 그대로 되살린다. 그냥 다시
        // 그리면 _renderedCount가 0으로 돌아가 첫 페이지만 남으므로, 한참 아래까지 펼쳐 놓고 작업하던
        // 사용자가 "적용"을 누를 때마다 "더 보기"를 처음부터 다시 눌러 내려가야 했다(2026-09-21 사용자
        // 보고). 카테고리/필터가 바뀌는 경우는 목록 자체가 달라지므로 되살리지 않는 게 맞다.
        private void RenderRows(bool preserveView)
        {
            int previouslyRendered = preserveView ? _renderedCount : 0;
            double previousOffset = preserveView && ItemsScroll != null ? ItemsScroll.VerticalOffset : 0;

            ItemsPanel.Children.Clear();
            _rows.Clear();
            _renderedCount = 0;
            _loadMoreButton = null;

            string filter = FilterBox?.Text ?? "";
            int filterMode = FilterModeCombo?.SelectedIndex ?? 0;

            if (string.IsNullOrEmpty(filter))
            {
                // 필터 텍스트가 비어 있으면 "포함하지 않음"/"일치하지 않음"이어도 검색 조건 자체가 없는
                // 것이므로 전과 동일하게 전체를 보여준다 - 조건 없이 "전부 제외"가 되는 것을 막기 위함.
                _filteredElements = _categoryElements;
            }
            else
            {
                _filteredElements = _categoryElements.Where(el =>
                {
                    string name = WorkingNameOf(el);
                    bool contains = name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
                    bool exact = string.Equals(name, filter, StringComparison.CurrentCultureIgnoreCase);
                    bool startsWith = name.StartsWith(filter, StringComparison.CurrentCultureIgnoreCase);
                    bool endsWith = name.EndsWith(filter, StringComparison.CurrentCultureIgnoreCase);
                    return filterMode switch
                    {
                        0 => contains,    // 포함됨
                        1 => !contains,   // 포함하지 않음
                        2 => exact,       // 일치함
                        3 => !exact,      // 일치하지 않음
                        4 => startsWith,  // ~로 시작하는
                        5 => endsWith,    // ~로 끝나는
                        _ => contains,
                    };
                }).ToList();
            }

            // 필터로 화면에서 사라진 항목은 체크 상태도 같이 지운다 — "체크됨"은 항상 필터를 거쳐 실제로
            // 보이는 항목만을 뜻해야 하므로, 필터를 바꿔서 안 보이게 된 항목이 예전 체크 상태를 그대로 들고
            // 있다가 최종 적용에 몰래 끼어드는 일이 없어야 한다. 다른 카테고리에서 체크된 항목은 건드리지 않는다.
            var categoryIds = new HashSet<ElementId>(_categoryElements.Select(el => el.Id));
            var visibleIds = new HashSet<ElementId>(_filteredElements.Select(el => el.Id));
            _checkedIds.RemoveWhere(id => categoryIds.Contains(id) && !visibleIds.Contains(id));

            RenderMoreRows();

            // 펼쳐 둔 만큼 다시 펼친다. 필터가 좁아져 항목 수가 줄었으면 남은 만큼에서 멈춘다.
            while (_renderedCount < previouslyRendered && _renderedCount < _filteredElements.Count)
                RenderMoreRows();

            RestoreScrollOffset(preserveView, previousOffset);
            UpdatePendingChangesText();
        }

        // 스크롤 가능 범위(ExtentHeight)는 행을 다시 배치한 뒤에야 정해지므로 UpdateLayout이 먼저다.
        // 그래도 ScrollViewer가 배치 직후 오프셋을 잘라내는 경우가 있어, 레이아웃이 완전히 끝난 뒤
        // (DispatcherPriority.Loaded) 한 번 더 같은 값을 써 준다.
        private void RestoreScrollOffset(bool preserveView, double offset)
        {
            if (!preserveView || offset <= 0 || ItemsScroll == null) return;

            ItemsScroll.UpdateLayout();
            ItemsScroll.ScrollToVerticalOffset(offset);
            Dispatcher.BeginInvoke(new Action(() => ItemsScroll.ScrollToVerticalOffset(offset)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // 이름이 길면 "..."으로 잘리는 문제(TextTrimming) 때문에, 행의 이름 TextBlock 너비를 위쪽 헤더 Grid의
        // ColumnDefinition 너비에 맞춘다. 처음에는 일반 Binding(ElementName + ActualWidth)과
        // DependencyPropertyDescriptor.AddValueChanged(ColumnDefinition.ActualWidthProperty) 둘 다 시도했으나,
        // 라이브 테스트로 확인된 바로는 전자는 애초에 갱신 알림이 안 오고 후자는 이 WPF 버전에
        // ActualWidthProperty가 공개 정적 필드로 없어 컴파일조차 안 됐다. 결국 가장 직접적인 방법으로,
        // GridSplitter 자체의 DragDelta(드래그 도중 계속 발생)에서 강제로 레이아웃을 갱신(UpdateLayout)한 뒤
        // ColumnDefinition.ActualWidth를 읽어 그려진 모든 행에 즉시 다시 써준다.
        private void NameColumnSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            UpdateLayout();
            foreach (RenameRow row in _rows)
            {
                row.OldNameHost.Width = OldNameColumn.ActualWidth;
                row.NewNameText.Width = NewNameColumn.ActualWidth;
            }
        }

        // 느려짐의 근본 원인: WPF StackPanel은 가상화가 안 돼서, "유형"처럼 필터 없이 수천 개가 나오는
        // 카테고리를 한 번에 전부 렌더링하면 그 자체로 몇 초~수십 초씩 멈춘다. 게다가 필터 입력칸에 한 글자
        // 칠 때마다 RenderRows가 통째로 다시 그려서, 아직 많이 남은 채로 타이핑하면 매 키 입력마다 이 지연이
        // 반복됐다. 카테고리/필터가 아무리 커도 한 번에 최대 PageSize개만 그리고, 나머지는 "더 보기"로 넘긴다.
        private void RenderMoreRows()
        {
            if (_loadMoreButton != null)
            {
                ItemsPanel.Children.Remove(_loadMoreButton);
                _loadMoreButton = null;
            }

            int start = _renderedCount;
            int end = Math.Min(start + PageSize, _filteredElements.Count);

            for (int i = start; i < end; i++)
            {
                Element el = _filteredElements[i];
                string oldName = WorkingNameOf(el);
                var row = new RenameRow { ElementId = el.Id, OriginalName = oldName };

                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(2, 3, 2, 3),
                    Background = Brushes.Transparent, // 배경이 null이면 자식 사이 빈 공간에서 드래그가 히트테스트되지 않는다
                    Tag = row
                };
                rowPanel.MouseLeftButtonDown += RowPanel_MouseLeftButtonDown;

                var checkBox = new CheckBox
                {
                    IsChecked = _checkedIds.Contains(el.Id),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsHitTestVisible = false // 클릭/드래그는 전부 rowPanel에서 처리하고, 체크박스는 상태 표시 용도로만 쓴다
                };
                checkBox.Checked += (_, _) => { _checkedIds.Add(row.ElementId); UpdateRowPreview(row); UpdatePendingChangesText(); };
                checkBox.Unchecked += (_, _) => { _checkedIds.Remove(row.ElementId); UpdateRowPreview(row); UpdatePendingChangesText(); };
                row.CheckBox = checkBox;
                rowPanel.Children.Add(checkBox);

                // 기존 이름 칸: 이름 TextBlock 위에 '특성' 버튼을 겹쳐 놓는다. 버튼은 평소 Hidden이라
                // 이름이 칸 너비를 다 쓰고, 마우스를 올렸을 때만 오른쪽 끝에 나타나 이름의 꼬리를 덮는다
                // (자리를 미리 비워 두면 안 그래도 좁은 이름 칸이 항상 그만큼 줄어든다).
                var oldNameHost = new WpfGrid
                {
                    Width = OldNameColumn.ActualWidth,
                    // 인라인 편집칸은 이름 TextBlock보다 키가 크다 - 높이를 미리 확보해 두지 않으면 편집을
                    // 열고 닫을 때마다 그 행이 늘었다 줄고, 아래 행들이 전부 몇 픽셀씩 밀린다. 그 상태에서
                    // 다른 행을 클릭하면(편집이 닫히며 레이아웃이 되돌아가므로) **겨눈 행이 아니라 옆 행이
                    // 눌린다** - 하네스에서 실제로 잡힌 문제다. 항상 편집칸 높이만큼 잡아 둬 흔들리지 않게 한다.
                    MinHeight = InlineEditHeight,
                    Background = Brushes.Transparent, // 배경이 null이면 글자 없는 빈 곳에서 더블클릭이 안 잡힌다
                };
                oldNameHost.MouseLeftButtonDown += OldNameHost_MouseLeftButtonDown;
                oldNameHost.Tag = row;
                row.OldNameHost = oldNameHost;

                var oldNameText = new TextBlock
                {
                    Text = oldName,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.OldNameText = oldNameText;
                oldNameHost.Children.Add(oldNameText);

                var propsButton = new Button
                {
                    Content = "특성",
                    FontSize = 10,
                    Padding = new Thickness(6, 0, 6, 0),
                    MinWidth = 0,
                    MinHeight = 0,
                    Height = 18,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = System.Windows.Visibility.Hidden,
                    ToolTip = "Revit 기본 창(유형 특성 / 특성 팔레트 등)으로 이 항목을 엽니다",
                    Tag = row,
                    // 이름 글자 위에 겹쳐 놓는 버튼이라 **배경이 불투명해야 한다** - 기본 버튼 배경은
                    // 반투명/연한 색이어서 뒤의 이름이 비쳐 보이고 버튼이 글자에 묻혔다(사용자 지적).
                    // 강조색(스틸 블루) + 흰 글자인 PrimaryButtonStyle을 그대로 써서 확실히 덮고,
                    // 동시에 "누를 수 있는 것"으로 바로 읽히게 한다.
                    Style = (Style)FindResource("PrimaryButtonStyle"),
                };
                // Grid에서는 나중에 추가된 자식이 위에 그려진다 - 이름 TextBlock 뒤에 추가해 항상 위에 온다.
                propsButton.Click += PropsButton_Click;
                row.PropsButton = propsButton;
                oldNameHost.Children.Add(propsButton);

                // 행 위에 마우스가 있을 때만 '특성' 버튼을 드러낸다.
                rowPanel.MouseEnter += (_, _) => propsButton.Visibility = System.Windows.Visibility.Visible;
                rowPanel.MouseLeave += (_, _) => propsButton.Visibility = System.Windows.Visibility.Hidden;

                rowPanel.Children.Add(oldNameHost);

                rowPanel.Children.Add(new TextBlock
                {
                    Text = " → ",
                    Foreground = Theme.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var newNameText = new TextBlock
                {
                    Width = NewNameColumn.ActualWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold
                };
                row.NewNameText = newNameText;
                rowPanel.Children.Add(newNameText);

                _rows.Add(row);
                ItemsPanel.Children.Add(rowPanel);
                UpdateRowPreview(row);
            }

            _renderedCount = end;

            if (_renderedCount < _filteredElements.Count)
            {
                int remaining = _filteredElements.Count - _renderedCount;
                _loadMoreButton = new Button
                {
                    Content = $"더 보기 ({remaining}개 남음)",
                    Margin = new Thickness(4, 8, 4, 4),
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
                };
                _loadMoreButton.Click += (_, _) => RenderMoreRows();
                ItemsPanel.Children.Add(_loadMoreButton);
            }

            UpdateCountText();
        }

        // 체크된 항목만 "적용하면 이렇게 바뀝니다" 미리보기를 보여준다 - 체크 해제된 항목은 ComputeNewName을
        // 다시 계산하지 않고 지금까지 확정된 이름(row.OriginalName = WorkingNameOf)을 그대로 회색으로 보여준다.
        // (ApplyButton_Click은 여전히 체크된 항목만 대상으로 하지만, FinalApplyButton_Click은 그렇지 않다 -
        // 아래 FinalApplyButton_Click 주석 참고.)
        private void UpdateRowPreview(RenameRow row)
        {
            bool isChecked = _checkedIds.Contains(row.ElementId);
            string newName = isChecked ? ComputeNewName(row.OriginalName) : row.OriginalName;
            row.NewNameText.Text = newName;
            row.NewNameText.Foreground = isChecked && newName != row.OriginalName ? Theme.TextPrimary : Theme.TextSecondary;
        }

        // ===================== 드래그로 여러 행 체크/해제 =====================

        // 클릭한 행의 반대 상태를 "드래그 목표 상태"로 삼아, 드래그가 지나가는 모든 행에 그 상태를 그대로 적용한다
        // (토글이 아니라 절대값 지정이라, 같은 행을 두 번 지나가도 깜빡이지 않는다).
        private void RowPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not RenameRow row) return;

            _dragging = true;
            _dragTargetChecked = row.CheckBox.IsChecked != true;
            ApplyDragState(row);
            _lastDragRow = row;

            ItemsPanel.CaptureMouse();
            e.Handled = true;
        }

        private void ItemsPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            System.Windows.Point pos = e.GetPosition(ItemsPanel);
            HitTestResult hit = VisualTreeHelper.HitTest(ItemsPanel, pos);
            if (hit == null) return;

            RenameRow? row = FindRowFromVisual(hit.VisualHit);
            if (row == null || row == _lastDragRow) return;

            ApplyDragState(row);
            _lastDragRow = row;
        }

        private void ItemsPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
        }

        private void ItemsPanel_LostMouseCapture(object sender, MouseEventArgs e)
        {
            EndDrag();
        }

        private void EndDrag()
        {
            _dragging = false;
            _lastDragRow = null;
            if (ItemsPanel.IsMouseCaptured) ItemsPanel.ReleaseMouseCapture();
        }

        private void ApplyDragState(RenameRow row)
        {
            row.CheckBox.IsChecked = _dragTargetChecked;
            UpdateCountText();
        }

        private static RenameRow? FindRowFromVisual(DependencyObject visual)
        {
            DependencyObject? current = visual;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag is RenameRow row) return row;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ===================== 이름 하나만 직접 고치기 / 특성 창 =====================

        // 기존 이름 칸을 더블클릭하면 그 항목 하나만 바로 고칠 수 있게 한다 (2026-09-21 사용자 요청).
        // 작업 모드(치환/삽입/...)를 거치지 않는 수동 편집이라 체크 여부와 무관하게 동작한다.
        private void OldNameHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return; // 한 번 클릭은 그대로 행으로 흘려보내 체크/드래그가 되게 둔다
            if (sender is not FrameworkElement fe || fe.Tag is not RenameRow row) return;

            // 더블클릭의 첫 번째 클릭은 이미 rowPanel의 핸들러를 타고 체크 상태를 한 번 뒤집어 놓았다
            // (두 번째 클릭은 여기서 Handled로 막는다). 이름을 고치려던 것뿐이므로 되돌려 준다.
            EndDrag();
            row.CheckBox.IsChecked = row.CheckBox.IsChecked != true;

            BeginInlineEdit(row);
            e.Handled = true;
        }

        // 지금 인라인 편집 중인 행 - 창 어디를 클릭하든 편집에서 빠져나오게 하려면(아래
        // Window_PreviewMouseDown) "편집 중인가"를 한 곳에서 알아야 한다.
        private RenameRow? _editingRow;

        // 편집칸 밖을 클릭하면 어디든 편집을 끝낸다 (2026-09-21 사용자 요청:
        // *"입력칸에서 벗어나오려면 엔터, 빈공간클릭을 해야하는데, 다른 어느부분을 클릭해도 벗어나올 수
        // 있도록"*). LostKeyboardFocus만으로는 부족하다 - 목록의 행(StackPanel)이나 라벨(TextBlock)처럼
        // **키보드 포커스를 가져가지 않는 요소**를 클릭하면 포커스가 편집칸에 그대로 남아 편집이 안 닫힌다.
        // 그래서 창 전체의 터널링 이벤트(PreviewMouseDown)에서 직접 판단한다.
        // 이벤트를 Handled로 막지 않는 것이 중요하다 - 클릭한 곳이 원래 하려던 일(체크 토글, 버튼 누름)도
        // 그대로 일어나야 "아무 데나 클릭하면 빠져나온다"가 자연스럽게 느껴진다.
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // WPF 핸들러의 예외는 Revit 네이티브로 넘어가 "복구 불가능한 오류"가 된다 - 창의 모든
            // 입력 길목은 예외를 밖으로 내보내지 않는다(App.cs의 Revit 이벤트 핸들러와 같은 방침).
            try
            {
                RenameRow? editing = _editingRow;
                if (editing?.Editor == null) return;
                if (IsInsideEditor(e.OriginalSource as DependencyObject, editing.Editor)) return;
                EndInlineEdit(editing, commit: true);
            }
            catch (System.Exception ex)
            {
                ShowStatus("이름 편집을 마치지 못했습니다: " + ex.Message);
            }
        }

        private static bool IsInsideEditor(DependencyObject? source, TextBox editor)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (ReferenceEquals(current, editor)) return true;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : null;
            }
            return false;
        }

        // Revit 쪽(창 바깥)을 클릭해 이 창이 비활성화될 때도 편집을 끝낸다 - 모드리스라 흔한 상황이다.
        private void NamerWindow_Deactivated(object? sender, EventArgs e)
        {
            // 창이 닫힐 때도 Deactivated가 난다 - 여기서 예외가 새면 그대로 Revit이 죽는다.
            try { if (_editingRow != null) EndInlineEdit(_editingRow, commit: true); }
            catch { /* 창을 떠나는 중이라 사용자에게 알릴 것이 없다 */ }
        }

        private void BeginInlineEdit(RenameRow row)
        {
            if (row.Editor != null) return;

            var box = new TextBox
            {
                Text = row.OriginalName,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(2, 0, 2, 0),
                Height = InlineEditHeight,   // 행 높이가 흔들리지 않도록 호스트가 확보해 둔 높이와 맞춘다
            };
            row.Editor = box;
            _editingRow = row;

            // 이름 TextBlock과 '특성' 버튼을 숨기고 같은 자리에 편집칸을 올린다 (셋 다 같은 Grid 칸).
            row.OldNameText.Visibility = System.Windows.Visibility.Collapsed;
            row.PropsButton.Visibility = System.Windows.Visibility.Collapsed;
            row.OldNameHost.Children.Add(box);

            box.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter) { EndInlineEdit(row, commit: true); args.Handled = true; }
                else if (args.Key == Key.Escape) { EndInlineEdit(row, commit: false); args.Handled = true; }
            };
            // 다른 곳을 클릭해서 편집을 떠나는 것도 확정으로 본다(Revit 이름 편집과 같은 감각).
            box.LostKeyboardFocus += (_, _) => EndInlineEdit(row, commit: true);

            // 방금 트리에 넣은 컨트롤은 아직 포커스를 받을 수 없어, 배치가 끝난 뒤(Loaded)에 잡는다.
            box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        }

        private void EndInlineEdit(RenameRow row, bool commit)
        {
            TextBox? box = row.Editor;
            if (box == null) return;
            // 편집칸을 트리에서 떼는 순간 LostKeyboardFocus가 또 들어오므로, 먼저 비워 재진입을 막는다
            // (창 전체의 PreviewMouseDown/Deactivated도 같은 경로로 들어오므로 가드는 하나로 충분하다).
            row.Editor = null;
            if (_editingRow == row) _editingRow = null;

            string text = (box.Text ?? "").Trim();
            row.OldNameHost.Children.Remove(box);
            row.OldNameText.Visibility = System.Windows.Visibility.Visible;
            row.PropsButton.Visibility = row.OldNameHost.IsMouseOver
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Hidden;

            if (commit) ApplyManualName(row, text);
        }

        // 수동으로 정한 이름을 작업 중 이름에 반영한다 - ApplyButton_Click과 같은 규칙으로
        // "이 요소가 세션 중 처음 바뀌는 순간"에만 진짜 원래 이름을 기록한다. 목록을 다시 그리지 않으므로
        // 펼쳐 둔 분량과 스크롤 위치가 그대로 유지된다.
        private void ApplyManualName(RenameRow row, string newName)
        {
            if (newName.Length == 0 || newName == row.OriginalName) return;

            Element? el = _doc.GetElement(row.ElementId);
            if (el == null) return;

            if (!_trueOriginalNames.ContainsKey(row.ElementId))
                _trueOriginalNames[row.ElementId] = SafeNameOf(el);
            _workingNames[row.ElementId] = newName;

            row.OriginalName = newName;
            row.OldNameText.Text = newName;
            UpdateRowPreview(row);
            UpdatePendingChangesText();
        }

        // '특성' 버튼 - **Revit 기본 창을 여는 것이 기본 동작**이다(2026-09-21 사용자 요청으로 이 창을
        // 모드리스로 바꾼 이유가 바로 이것). 어떤 기본 창을 어떻게 여는지는 NamerNativeProperties 참고.
        // 기본 창으로 갈 수 없는 경우(유형을 쓰는 부재가 모델에 하나도 없을 때)에만 자체 특성 창으로 간다.
        private void PropsButton_Click(object sender, RoutedEventArgs e) => Guard("특성 창 열기", () =>
        {
            if (sender is not FrameworkElement fe || fe.Tag is not RenameRow row) return;

            _propsRow = row;
            _handler.PendingProperties = new NamerExternalEventHandler.PropertyRequest
            {
                Category = _category,
                Id = row.ElementId,
                Name = row.OriginalName,
            };
            ShowStatus("Revit 기본 창을 여는 중...");
            _event.Raise();
        });

        // 기본 창 요청을 보낸 행 - ExternalEvent가 끝난 뒤 자체 특성 창으로 넘어갈 때 어느 행이었는지
        // 알아야 새 이름을 그 행에 돌려줄 수 있다.
        private RenameRow? _propsRow;

        // 기본 창을 열 수 없을 때 NamerExternalEventHandler가 호출한다. **이 호출은 ExternalEvent 안,
        // 즉 유효한 API 컨텍스트에서 일어나므로** 자체 특성 창이 자기 Transaction을 그대로 열 수 있다.
        internal void ShowFallbackProperties(Document doc, NamerExternalEventHandler.PropertyRequest request, string? reason)
        {
            Element? el = doc.GetElement(request.Id);
            if (el == null)
            {
                ShowStatus("이 항목을 모델에서 더 이상 찾을 수 없습니다.");
                return;
            }

            var window = new NamerPropertiesWindow(doc, el, request.Category, request.Name, reason) { Owner = this };
            bool? ok = window.ShowDialog();
            ShowStatus(reason ?? "");

            if (ok != true || window.NewName == null) return;
            RenameRow? row = _propsRow;
            if (row != null && row.ElementId == request.Id) ApplyManualName(row, window.NewName);
        }

        private void RefreshPreview()
        {
            foreach (RenameRow row in _rows) UpdateRowPreview(row);
        }

        private void UpdateCountText()
        {
            int checkedInFiltered = _filteredElements.Count(el => _checkedIds.Contains(el.Id));
            CountText.Text = $"{_renderedCount} / {_filteredElements.Count}개 표시 중 (전체 {_categoryElements.Count}개), 선택됨 {checkedInFiltered}개";
        }

        // 필터에 걸리는 전체(_filteredElements) 기준으로 동작해야, 아직 "더 보기"로 렌더링되지 않은
        // 항목까지 전체 선택/해제가 실제로 반영된다 (렌더링된 _rows만 기준으로 하면 페이지 밖 항목이 빠짐).
        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (Element el in _filteredElements) _checkedIds.Add(el.Id);
            foreach (RenameRow row in _rows) row.CheckBox.IsChecked = true;
            UpdateCountText();
            UpdatePendingChangesText();
        }

        private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (Element el in _filteredElements) _checkedIds.Remove(el.Id);
            foreach (RenameRow row in _rows) row.CheckBox.IsChecked = false;
            UpdateCountText();
            UpdatePendingChangesText();
        }

        // ===================== 적용(창 안에서만)/최종 적용(모델에 반영)/취소 =====================

        // 체크된 항목의 작업 중 이름(_workingNames)만 갱신한다. Revit 모델은 전혀 건드리지 않으므로,
        // 다른 카테고리로 옮겨가거나 다른 작업 모드를 골라 계속 이어서 적용할 수 있다.
        private void ApplyButton_Click(object sender, RoutedEventArgs e) => Guard("적용", () =>
        {
            int changedCount = 0;
            foreach (Element el in _categoryElements)
            {
                if (!_checkedIds.Contains(el.Id)) continue;
                string current = WorkingNameOf(el);
                string newName = ComputeNewName(current);
                if (newName == current) continue;

                // 이 요소가 세션 중 처음으로 실제 바뀌는 순간에만 "진짜 원래 이름"을 기록한다
                // (이미 한 번 바뀐 적이 있으면 current는 그 이전 작업 결과이므로 덮어쓰면 안 됨).
                if (!_trueOriginalNames.ContainsKey(el.Id))
                    _trueOriginalNames[el.Id] = SafeNameOf(el);
                _workingNames[el.Id] = newName;
                changedCount++;
            }

            if (changedCount == 0)
            {
                MessageBox.Show("변경될 항목이 없습니다.", "NAMER", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 입력칸을 비워서, 이미 적용된 작업이 입력값 그대로 남아있다가 실수로(또는 다음 적용 때) 한 번 더
            // 적용되는 일이 없도록 한다. 모든 모드가 빈 입력값에서는 이름을 바꾸지 않으므로(ComputeNewName 참고) 안전하다.
            ClearModeInputs();
            RenderRows(preserveView: true);
            UpdatePendingChangesText();
        });

        private void ClearModeInputs()
        {
            FindBox.Text = "";
            ReplaceBox.Text = "";
            PositionBox.Text = "";
            InsertTextBox.Text = "";
            DelimiterBox.Text = "";
            StartPosBox.Text = "";
            EndPosBox.Text = "";
        }

        // 이 세션에서 누적된 모든 카테고리의 변경 사항을 모아 실제 Revit 모델에 반영한다 (창을 닫아야 NamerCommand가 Transaction을 실행함).
        // 중요: 이 메서드는 절대로 ComputeNewName을 호출하지 않는다 — 입력칸에 아직 "적용"하지 않은 값이 남아있더라도
        // 그 작업이 여기서 몰래 한 번 더 실행되는 일은 구조적으로 불가능하다. 이미 _workingNames에 누적된(=지난 "적용"
        // 클릭들로 확정된) 이름만 그대로 모델에 옮겨 쓴다.
        private void FinalApplyButton_Click(object sender, RoutedEventArgs e) => Guard("최종 적용", () =>
        {
            var result = new List<(ElementId, string)>();
            foreach (KeyValuePair<ElementId, string> kvp in _workingNames)
            {
                // 체크 여부는 여기서 더 이상 보지 않는다 - _workingNames에 들어가는 시점(ApplyButton_Click)에
                // 이미 체크된 상태에서만 기록되므로, 그 이후 필터가 바뀌어 자동으로 체크가 해제되더라도
                // (RenderRows 참고) 이미 "적용"으로 확정한 변경 사항은 그대로 최종 적용 대상에 남아야 한다.
                string trueOriginal = _trueOriginalNames.TryGetValue(kvp.Key, out string? orig) ? orig : kvp.Value;
                if (kvp.Value != trueOriginal) result.Add((kvp.Key, kvp.Value));
            }

            if (result.Count == 0)
            {
                MessageBox.Show("모델에 적용할 변경 사항이 없습니다. 먼저 항목을 선택하고 '적용'을 눌러 이름을 바꿔보세요.", "NAMER", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 모드리스라 여기서 바로 Transaction을 열 수 없다 - 요청만 넣고 Revit이 유효한 컨텍스트에서
            // 처리한 뒤 OnRenamesApplied로 결과를 돌려준다. 창은 닫지 않는다(모드리스의 요점).
            _handler.PendingRenames = result;
            _handler.PendingMergeDuplicateMaterials = DuplicateMergeRadio?.IsChecked == true;
            ShowStatus($"{result.Count}개 이름을 모델에 반영하는 중...");
            _event.Raise();
        });

        // NamerExternalEventHandler가 이름 변경을 끝낸 뒤 호출한다. Revit API 스레드에서 바로 불리지만
        // Revit은 WPF와 같은 단일 STA 스레드를 쓰므로 Dispatcher 없이 UI를 갱신해도 안전하다
        // (경고Pick의 ApplyRefreshedTypeGroups와 같은 전제).
        internal void OnRenamesApplied(NamerCommand.RenameResult result)
        {
            if (result.Status != TransactionStatus.Committed)
            {
                ShowStatus("이름 변경이 모델에 반영되지 않았습니다 (롤백). 작업 중 이름은 그대로 남아 있습니다.");
                return;
            }

            // 모델이 이제 새 이름을 갖고 있으므로 작업 중 이름은 역할을 다했다 - 남겨 두면 "아직 반영되지
            // 않은 변경"으로 계속 세어져 사용자가 또 최종 적용을 누르게 된다. 체크 상태는 유지한다.
            _trueOriginalNames.Clear();
            _workingNames.Clear();
            _categoryElements = CollectCandidates(_doc, _category);
            RenderRows(preserveView: true);
            UpdatePendingChangesText();

            string detail = result.Failed.Count > 0 ? $" (실패 {result.Failed.Count}개)" : "";
            ShowStatus($"{result.Renamed}개 이름을 모델에 반영했습니다{detail}. 되돌리려면 Revit에서 Ctrl+Z.");
        }

        // NamerCommand가 이미 열려 있는 창을 재사용할 때 호출 - 그 사이 다른 문서로 갈아탔을 수 있다.
        internal void UpdateDocumentAndSelection(Document doc, List<ElementId> preSelectedIds)
        {
            bool sameDocument = DocKey(doc) == DocKey(_doc);

            _doc = doc;
            _handler.TargetDocument = doc;
            _preSelectedIds = new HashSet<ElementId>(preSelectedIds);

            if (!sameDocument)
            {
                // ElementId는 문서마다 독립적이라, 다른 문서로 갈아탔으면 지금까지 모아 둔 체크/작업 중
                // 이름은 전부 엉뚱한 요소를 가리키게 된다 - 그대로 두면 최종 적용이 남의 요소를 바꾼다.
                _checkedIds.Clear();
                _trueOriginalNames.Clear();
                _workingNames.Clear();
                _categoriesInitialized.Clear();
                ShowStatus("다른 문서로 바뀌어 작업 중이던 내용을 비웠습니다.");
            }

            LoadCategory(DetectInitialCategory(doc, preSelectedIds));
        }

        internal void ShowStatus(string text)
        {
            if (StatusText != null) StatusText.Text = text;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Guard("닫기", Close);

        // "최종 적용"이 실제로 반영할 개수와 정확히 같은 기준(이름이 실제로 바뀌었는지)으로 센다 - 체크 여부는
        // FinalApplyButton_Click과 마찬가지로 더 이상 보지 않는다.
        private void UpdatePendingChangesText()
        {
            int pending = _workingNames.Count(kvp =>
                _trueOriginalNames.TryGetValue(kvp.Key, out string? orig) && kvp.Value != orig);
            PendingChangesText.Text = pending == 0
                ? "모델에 아직 반영되지 않은 변경 사항이 없습니다."
                : $"아직 모델에 반영되지 않은 변경 사항 {pending}개 (전체 카테고리 합계, '적용'으로 확정한 것 전부 — 이후 체크 해제되어도 포함됨) — '최종 적용'을 눌러야 실제로 저장됩니다.";
        }
    }
}
