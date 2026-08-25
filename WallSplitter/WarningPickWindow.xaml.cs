using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
// Autodesk.Revit.DB에도 Grid(그리드 라인 요소) 타입이 있어 System.Windows.Controls.Grid와 이름이
// 겹친다(QuickToggleToolbar.xaml.cs의 비슷한 별칭 사용 참고) - 행을 코드로 그릴 때 쓰는 WPF Grid만
// 별칭으로 명확히 구분한다.
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // 경고Pick: 문서의 경고(Document.GetWarnings())를 종류(상위)-발생 건(중위)-관련 요소(하위) 3단으로
    // 묶어 보여주고, 골라서 바로 선택 + 그 요소가 있는 뷰로 이동시켜주는 모드리스 창. 같은 종류의 경고가
    // 발생 건마다 별도 FailureMessage로 쪼개져 나오는 경우가 흔해서(예: 벽 겹침 쌍마다 별도 경고),
    // 종류로 한 번 더 묶지 않으면 같은 문구가 여러 번 반복되어 알아보기 어렵다는 요청으로 3단 구조가 됐다.
    // QuickToggleToolbar와 같은 이유로 세션 중 계속 열어 둔 채 모델을 조작할 수 있어야 해서(경고를 확인하고
    // 바로 고치는 워크플로우), 모달(ShowDialog)이 아니라 모드리스(Show)로 띄우고 실제 선택/뷰이동/단면상자/
    // 격리는 ExternalEvent로 위임한다(WarningPickExternalEventHandler, App.OnStartup의 QuickToggleEvent
    // 주석 참고).
    public partial class WarningPickWindow : Window
    {
        public static WarningPickWindow? Instance { get; private set; }

        private readonly WarningPickExternalEventHandler _handler;
        private readonly ExternalEvent _event;
        private Document _doc;
        private List<WarningPickTypeGroup> _allTypeGroups;

        // 매 렌더링마다 다시 채워지는, 화면에 실제로 그려진 "요소" 체크박스-요소 짝 목록(렌더링 순서 =
        // 화면 위에서 아래 순서) - "체크한 요소 선택"/단면상자/격리 버튼과 쉬프트 범위 선택이 이 목록을 쓴다.
        private readonly List<(CheckBox CheckBox, WarningPickElement Element)> _elementCheckboxes = new();

        // "전체 선택"/"전체 해제"는 요소 체크박스뿐 아니라 종류·발생 건 체크박스도 같이 꺼야 한다 -
        // 요소만 끄고 상위 체크박스를 그대로 두면 상위가 눈에 계속 체크된 채로 남는 라이브 버그가 있었다
        // (2026-08-25 확인). 그래서 레벨과 상관없이 화면에 그려진 체크박스 전부를 한 목록에 모아둔다.
        private readonly List<CheckBox> _allCheckboxes = new();

        // ===== 요소 행 드래그/쉬프트 범위 체크 (NamerWindow의 드래그 체크와 같은 패턴) =====
        private bool _dragging;
        private bool _dragTargetChecked;
        private WarningPickElement? _lastDragElement;
        private WarningPickElement? _lastClickedElement; // 쉬프트 범위 선택의 시작점

        public WarningPickWindow(UIApplication uiapp, Document doc, List<WarningPickTypeGroup> typeGroups)
        {
            InitializeComponent();
            Instance = this;
            _doc = doc;
            _allTypeGroups = typeGroups;

            new WindowInteropHelper(this) { Owner = uiapp.MainWindowHandle };

            _handler = new WarningPickExternalEventHandler { TargetDocument = doc };
            _event = ExternalEvent.Create(_handler);

            Closed += (_, _) => { if (Instance == this) Instance = null; };

            UpdateDocumentText();
            RenderTypeGroups(_allTypeGroups);
        }

        // WarningPickCommand가 이미 열려 있는 창을 재사용할 때 호출 - 그 사이 다른 문서로 전환해서 다시
        // 실행했을 수도 있으므로 대상 문서/핸들러도 함께 갱신한다.
        public void UpdateDocumentAndTypeGroups(Document doc, List<WarningPickTypeGroup> typeGroups)
        {
            _doc = doc;
            _handler.TargetDocument = doc;
            _allTypeGroups = typeGroups;
            StatusText.Text = "";
            UpdateDocumentText();
            RenderTypeGroups(FilterTypeGroups(_allTypeGroups, FilterBox.Text));
        }

        // WarningPickExternalEventHandler.ExecuteRefresh가 새로 조회한 목록을 되돌려줄 때 호출 - Revit
        // API 스레드에서 바로 호출되지만, Revit은 WPF와 같은 스레드(단일 STA)를 쓰므로 Dispatcher 없이
        // UI를 바로 갱신해도 안전하다(QuickToggleExternalEventHandler가 QuickToggleToolbar.Instance를
        // 직접 갱신하는 것과 같은 전제).
        public void ApplyRefreshedTypeGroups(List<WarningPickTypeGroup> typeGroups)
        {
            _allTypeGroups = typeGroups;
            StatusText.Text = $"새로고침됨 ({DateTime.Now:HH:mm:ss})";
            RenderTypeGroups(FilterTypeGroups(_allTypeGroups, FilterBox.Text));
        }

        public void ShowDocumentMismatch()
        {
            TaskDialog.Show("경고Pick", "경고를 조회한 문서가 더 이상 활성 문서가 아닙니다. 해당 문서를 활성화한 뒤 '새로고침'을 눌러 다시 시도하세요.");
        }

        private void UpdateDocumentText()
        {
            DocumentText.Text = $"문서: {(string.IsNullOrEmpty(_doc.Title) ? "(제목 없음)" : _doc.Title)}";
        }

        private static List<WarningPickTypeGroup> FilterTypeGroups(List<WarningPickTypeGroup> typeGroups, string? filter)
        {
            filter = filter?.Trim() ?? "";
            if (filter.Length == 0) return typeGroups;
            return typeGroups.Where(t =>
                t.TypeLabel.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                t.Occurrences.Any(o =>
                    o.Description.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    o.Elements.Any(e =>
                        e.ElementName.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                        e.Category.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                        e.IdText.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0))
            ).ToList();
        }

        private void RenderTypeGroups(List<WarningPickTypeGroup> typeGroups)
        {
            GroupsPanel.Children.Clear();
            _elementCheckboxes.Clear();
            _allCheckboxes.Clear();
            _lastDragElement = null;
            _lastClickedElement = null;

            if (_allTypeGroups.Count == 0)
            {
                GroupsPanel.Children.Add(new TextBlock
                {
                    Text = "현재 이 문서에 경고가 없습니다.",
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(6, 10, 0, 0),
                });
            }
            else
            {
                foreach (WarningPickTypeGroup typeGroup in typeGroups)
                    GroupsPanel.Children.Add(BuildTypeGroupPanel(typeGroup));
            }

            int totalOccurrences = _allTypeGroups.Sum(t => t.Occurrences.Count);
            int shownOccurrences = typeGroups.Sum(t => t.Occurrences.Count);
            CountText.Text = _allTypeGroups.Count == 0 ? "" : $"{typeGroups.Count}종류/{shownOccurrences}건 표시 (전체 {_allTypeGroups.Count}종류/{totalOccurrences}건)";
        }

        // 경고 종류 하나 = 상위 헤더(심각도+대표 설명+건수) + 그 아래 발생 건들(항상 펼쳐짐 - 발생 건
        // 개수는 보통 적어서 굳이 접어둘 필요가 없다는 판단, "하위 요소"만 드롭다운으로 접는다).
        // 상위 체크박스는 그 종류에 속한 모든 발생 건 체크박스를 한꺼번에 켜고/끈다.
        private UIElement BuildTypeGroupPanel(WarningPickTypeGroup typeGroup)
        {
            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var typeCheck = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "이 종류에 속한 모든 발생 건·요소를 모두 체크/해제",
            };
            Brush severityBrush = typeGroup.Severity == FailureSeverity.Error
                ? (Brush)FindResource("DangerTextBrush")
                : (Brush)FindResource("WarningTextBrush");
            var severityText = new TextBlock
            {
                Text = typeGroup.SeverityLabel,
                Foreground = severityBrush,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            int totalElements = typeGroup.Occurrences.Sum(o => o.Elements.Count);
            var labelText = new TextBlock
            {
                Text = $"{typeGroup.TypeLabel}  ({typeGroup.Occurrences.Count}건, 총 {totalElements}개 요소)",
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            headerPanel.Children.Add(typeCheck);
            headerPanel.Children.Add(severityText);
            headerPanel.Children.Add(labelText);
            container.Children.Add(headerPanel);

            var occurrenceCheckboxes = new List<CheckBox>();
            int index = 1;
            foreach (WarningPickGroup occurrence in typeGroup.Occurrences)
            {
                UIElement occurrencePanel = BuildOccurrencePanel(occurrence, index++, out CheckBox occurrenceCheck);
                occurrenceCheckboxes.Add(occurrenceCheck);
                _allCheckboxes.Add(occurrenceCheck);
                container.Children.Add(occurrencePanel);
            }

            typeCheck.Checked += (_, _) => { foreach (CheckBox cb in occurrenceCheckboxes) cb.IsChecked = true; };
            typeCheck.Unchecked += (_, _) => { foreach (CheckBox cb in occurrenceCheckboxes) cb.IsChecked = false; };
            _allCheckboxes.Add(typeCheck);

            container.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("AppBorderBrush"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 8, 0, 0),
            });

            return container;
        }

        // 발생 건 하나 = 접혔다 펼쳐지는 요소 목록(기본은 접힘 - "하위 요소는 드롭다운으로 펼쳐야 보이게"
        // 라는 요청). 화살표(▸/▾)를 눌러야 그 안의 요소 행이 보인다. 발생 건 체크박스는 접힌 상태에서도
        // 눌러서 그 안의 요소를 전부 체크할 수 있다(펼치지 않고도 "이 건 전체"를 고를 수 있어야 하므로).
        private UIElement BuildOccurrencePanel(WarningPickGroup occurrence, int index, out CheckBox occurrenceCheck)
        {
            var container = new StackPanel { Margin = new Thickness(20, 0, 0, 4) };

            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            var toggle = new TextBlock
            {
                Text = "▸", // ▸ 접힘 표시
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.Bold,
            };
            occurrenceCheck = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "이 발생 건의 요소를 모두 체크/해제",
            };
            var descText = new TextBlock
            {
                Text = $"{index}. {occurrence.Description}  ({occurrence.Elements.Count}개 요소)",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Cursor = Cursors.Hand,
            };
            headerPanel.Children.Add(toggle);
            headerPanel.Children.Add(occurrenceCheck);
            headerPanel.Children.Add(descText);
            container.Children.Add(headerPanel);

            // Window(UIElement)에도 인스턴스 속성 Visibility가 있어, 이 안에서 그냥 "Visibility.Collapsed"라고
            // 쓰면 열거형이 아니라 "this.Visibility.Collapsed"로 해석돼 CS0176이 난다 - 완전한 이름 필요.
            var elementsPanel = new StackPanel { Visibility = System.Windows.Visibility.Collapsed };
            container.Children.Add(elementsPanel);

            void ToggleExpanded(object? sender, MouseButtonEventArgs e)
            {
                bool expand = elementsPanel.Visibility != System.Windows.Visibility.Visible;
                elementsPanel.Visibility = expand ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                toggle.Text = expand ? "▾" : "▸"; // ▾ 펼침 / ▸ 접힘
            }
            toggle.MouseLeftButtonDown += ToggleExpanded;
            descText.MouseLeftButtonDown += ToggleExpanded;

            var elementCheckboxes = new List<CheckBox>();
            foreach (WarningPickElement element in occurrence.Elements)
            {
                UIElement row = BuildElementRow(element, out CheckBox elementCheck);
                elementCheckboxes.Add(elementCheck);
                _elementCheckboxes.Add((elementCheck, element));
                _allCheckboxes.Add(elementCheck);
                elementsPanel.Children.Add(row);
            }

            CheckBox capturedOccurrenceCheck = occurrenceCheck;
            capturedOccurrenceCheck.Checked += (_, _) => { foreach (CheckBox cb in elementCheckboxes) cb.IsChecked = true; };
            capturedOccurrenceCheck.Unchecked += (_, _) => { foreach (CheckBox cb in elementCheckboxes) cb.IsChecked = false; };

            return container;
        }

        // 체크박스는 시각적 표시 전용(IsHitTestVisible=false)이고, 행 전체(Grid) 클릭으로 드래그/쉬프트
        // 범위 체크를 처리한다 - NamerWindow의 드래그 체크와 같은 패턴(ItemsPanel_MouseMove 등 참고).
        // "선택" 버튼만은 예외적으로 직접 클릭이 통과되어야 하므로 Tag로 요소를 식별해 행 핸들러 안에서
        // 버튼 위 클릭인지 먼저 걸러낸다.
        private UIElement BuildElementRow(WarningPickElement element, out CheckBox checkBox)
        {
            var grid = new WpfGrid { Margin = new Thickness(20, 2, 0, 2), Background = Brushes.Transparent, Tag = element };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

            checkBox = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            WpfGrid.SetColumn(checkBox, 0);

            var categoryText = new TextBlock { Text = element.Category, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            WpfGrid.SetColumn(categoryText, 1);

            var nameText = new TextBlock { Text = element.ElementName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            WpfGrid.SetColumn(nameText, 2);

            var idText = new TextBlock { Text = element.IdText, VerticalAlignment = VerticalAlignment.Center };
            WpfGrid.SetColumn(idText, 3);

            var selectButton = new Button { Content = "선택", Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            selectButton.Click += (_, _) => RequestSelect(new List<ElementId> { element.ElementId }, $"{element.Category} \"{element.ElementName}\" (ID {element.IdText})");
            WpfGrid.SetColumn(selectButton, 4);

            grid.Children.Add(checkBox);
            grid.Children.Add(categoryText);
            grid.Children.Add(nameText);
            grid.Children.Add(idText);
            grid.Children.Add(selectButton);

            grid.MouseLeftButtonDown += ElementRow_MouseLeftButtonDown;
            grid.Cursor = Cursors.Hand;
            return grid;
        }

        private void RequestSelect(List<ElementId> ids, string label)
        {
            _handler.PendingSelectIds = ids;
            _event.Raise();
            StatusText.Text = $"선택 요청: {label}";
        }

        private List<ElementId> GetCheckedElementIds() =>
            _elementCheckboxes.Where(t => t.CheckBox.IsChecked == true).Select(t => t.Element.ElementId).ToList();

        // 요소 체크박스뿐 아니라 종류·발생 건 체크박스까지 전부 같은 값으로 맞춘다 - 요소만 바꾸면 상위
        // 체크박스가 실제 상태와 어긋난 채(예: 전체 해제했는데 상위는 계속 체크됨) 남는 문제가 있었다.
        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox checkBox in _allCheckboxes) checkBox.IsChecked = true;
        }

        private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox checkBox in _allCheckboxes) checkBox.IsChecked = false;
        }

        private void SelectCheckedButton_Click(object sender, RoutedEventArgs e)
        {
            List<ElementId> ids = GetCheckedElementIds();
            if (ids.Count == 0)
            {
                StatusText.Text = "체크된 요소가 없습니다.";
                return;
            }
            RequestSelect(ids, $"{ids.Count}개 요소 (체크 목록)");
        }

        private void SectionBoxButton_Click(object sender, RoutedEventArgs e)
        {
            List<ElementId> ids = GetCheckedElementIds();
            if (ids.Count == 0)
            {
                StatusText.Text = "체크된 요소가 없습니다.";
                return;
            }
            _handler.PendingSectionBoxIds = ids;
            _event.Raise();
            StatusText.Text = $"단면상자 요청: {ids.Count}개 요소 (3D 뷰가 활성화되어 있어야 합니다)";
        }

        private void IsolateButton_Click(object sender, RoutedEventArgs e)
        {
            List<ElementId> ids = GetCheckedElementIds();
            if (ids.Count == 0)
            {
                StatusText.Text = "체크된 요소가 없습니다.";
                return;
            }
            _handler.PendingIsolateIds = ids;
            _event.Raise();
            StatusText.Text = $"격리 요청: {ids.Count}개 요소만 표시";
        }

        private void ResetIsolateButton_Click(object sender, RoutedEventArgs e)
        {
            _handler.PendingResetIsolate = true;
            _event.Raise();
            StatusText.Text = "격리 해제 요청";
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RenderTypeGroups(FilterTypeGroups(_allTypeGroups, FilterBox.Text));

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _handler.PendingRefresh = true;
            _event.Raise();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ===================== 요소 행: 클릭/드래그/쉬프트 범위 체크 =====================

        // 클릭한 행의 반대 상태를 "드래그 목표 상태"로 삼아 드래그가 지나가는 모든 행에 그대로 적용한다
        // (토글이 아니라 절대값 지정이라 같은 행을 두 번 지나가도 깜빡이지 않음) - NamerWindow와 같은 패턴.
        // 쉬프트를 누른 채 클릭하면 마지막 클릭 위치부터 지금 위치까지 범위 전체를 체크한다.
        private void ElementRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinButton(e.OriginalSource as DependencyObject)) return; // "선택" 버튼 클릭은 그대로 통과시킴
            if (sender is not FrameworkElement fe || fe.Tag is not WarningPickElement element) return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastClickedElement != null)
            {
                ApplyRangeCheck(_lastClickedElement, element);
                _lastClickedElement = element;
                e.Handled = true;
                return;
            }

            CheckBox? checkBox = FindCheckBoxFor(element);
            if (checkBox == null) return;

            _dragging = true;
            _dragTargetChecked = checkBox.IsChecked != true;
            checkBox.IsChecked = _dragTargetChecked;
            _lastDragElement = element;
            _lastClickedElement = element;

            GroupsPanel.CaptureMouse();
            e.Handled = true;
        }

        private void GroupsPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            System.Windows.Point pos = e.GetPosition(GroupsPanel);
            HitTestResult hit = VisualTreeHelper.HitTest(GroupsPanel, pos);
            if (hit == null) return;

            WarningPickElement? element = FindElementFromVisual(hit.VisualHit);
            if (element == null || element == _lastDragElement) return;

            CheckBox? checkBox = FindCheckBoxFor(element);
            if (checkBox == null) return;

            checkBox.IsChecked = _dragTargetChecked;
            _lastDragElement = element;
        }

        private void GroupsPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

        private void GroupsPanel_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

        private void EndDrag()
        {
            _dragging = false;
            _lastDragElement = null;
            if (GroupsPanel.IsMouseCaptured) GroupsPanel.ReleaseMouseCapture();
        }

        // 쉬프트 범위 선택 - 화면에 그려진 순서(_elementCheckboxes, 위→아래) 기준으로 두 요소 사이를
        // 전부 체크한다. 접혀 있는 발생 건 안의 요소도 인덱스 순서상 포함되면 같이 체크된다(펼치지
        // 않아도 범위에 들어간 요소는 체크되는 게 자연스럽다 - 탐색기의 쉬프트 선택과 같은 감각).
        private void ApplyRangeCheck(WarningPickElement from, WarningPickElement to)
        {
            int i1 = _elementCheckboxes.FindIndex(t => ReferenceEquals(t.Element, from));
            int i2 = _elementCheckboxes.FindIndex(t => ReferenceEquals(t.Element, to));
            if (i1 < 0 || i2 < 0) return;

            int lo = Math.Min(i1, i2), hi = Math.Max(i1, i2);
            for (int i = lo; i <= hi; i++) _elementCheckboxes[i].CheckBox.IsChecked = true;
        }

        private CheckBox? FindCheckBoxFor(WarningPickElement element)
        {
            foreach ((CheckBox checkBox, WarningPickElement candidate) in _elementCheckboxes)
                if (ReferenceEquals(candidate, element)) return checkBox;
            return null;
        }

        private static WarningPickElement? FindElementFromVisual(DependencyObject visual)
        {
            DependencyObject? current = visual;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag is WarningPickElement element) return element;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static bool IsWithinButton(DependencyObject? visual)
        {
            DependencyObject? current = visual;
            while (current != null)
            {
                if (current is Button) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }
    }
}
