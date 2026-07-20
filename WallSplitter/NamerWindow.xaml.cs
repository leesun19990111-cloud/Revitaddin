using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    public partial class NamerWindow : Window
    {
        private enum NamerCategory { View, Sheet, Family, Type, Legend, Schedule, Material }
        private enum NamerMode { Replace, Insert, Swap, DeleteRange }

        private sealed class RenameRow
        {
            public ElementId ElementId = ElementId.InvalidElementId;
            public string OriginalName = "";
            public CheckBox CheckBox = null!;
            public TextBlock OldNameText = null!;
            public TextBlock NewNameText = null!;
        }

        private readonly Document _doc;
        private readonly HashSet<ElementId> _preSelectedIds;
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
        private List<Element> _filteredElements = new();
        private int _renderedCount;
        private Button? _loadMoreButton;

        public List<(ElementId Id, string NewName)>? Result { get; private set; }

        // 재료 이름 변경 시 새 이름이 이미 다른 재료가 쓰고 있으면 어떻게 할지 - true면 그 기존 재료로
        // 병합(사용처를 옮기고 원래 재료는 삭제), false면 숫자를 붙여 별개 재료로 저장. NamerCommand가
        // 커밋 시점에 실제 충돌 여부를 판단하므로, 여기서는 사용자가 마지막으로 고른 정책만 전달한다.
        public bool MergeDuplicateMaterials { get; private set; } = true;

        public NamerWindow(Document doc, List<ElementId> preSelectedIds)
        {
            InitializeComponent();
            _doc = doc;
            _preSelectedIds = new HashSet<ElementId>(preSelectedIds);

            NamerCategory initial = DetectInitialCategory(doc, preSelectedIds);
            SetCategoryRadio(initial);
            LoadCategory(initial);
        }

        private static NamerCategory DetectInitialCategory(Document doc, List<ElementId> ids)
        {
            foreach (ElementId id in ids)
            {
                Element? el = doc.GetElement(id);
                if (el is ViewSheet) return NamerCategory.Sheet;
                if (el is ViewSchedule) return NamerCategory.Schedule;
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
            }
        }

        // ===================== 대상 분류 =====================

        private void CategoryRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (CategoryViewRadio == null) return; // InitializeComponent 도중 발생하는 초기 Checked 이벤트 방지

            NamerCategory category =
                CategoryViewRadio.IsChecked == true ? NamerCategory.View :
                CategorySheetRadio.IsChecked == true ? NamerCategory.Sheet :
                CategoryFamilyRadio.IsChecked == true ? NamerCategory.Family :
                CategoryTypeRadio.IsChecked == true ? NamerCategory.Type :
                CategoryLegendRadio.IsChecked == true ? NamerCategory.Legend :
                CategoryScheduleRadio.IsChecked == true ? NamerCategory.Schedule :
                NamerCategory.Material;

            LoadCategory(category);
        }

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

        private static List<Element> CollectCandidates(Document doc, NamerCategory category)
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
            _workingNames.TryGetValue(el.Id, out string? name) ? name : (el.Name ?? "");

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ===================== 목록 렌더링 =====================

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RenderRows();

        // ComboBoxItem의 SelectedIndex="0" 기본값도 RadioButton의 IsChecked="True"처럼 InitializeComponent
        // 도중 SelectionChanged를 먼저 발생시킨다 - 이 시점엔 Row4의 ItemsPanel이 아직 연결 전이라
        // RenderRows()가 바로 NullReferenceException을 낸다(ModeRadio_Checked의 ReplacePanel 가드와 동일한 이유).
        private void FilterModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ItemsPanel == null) return;
            RenderRows();
        }

        private void RenderRows()
        {
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
            UpdatePendingChangesText();
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
                row.OldNameText.Width = OldNameColumn.ActualWidth;
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

                var oldNameText = new TextBlock
                {
                    Text = oldName,
                    Width = OldNameColumn.ActualWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.OldNameText = oldNameText;
                rowPanel.Children.Add(oldNameText);

                rowPanel.Children.Add(new TextBlock
                {
                    Text = " → ",
                    Foreground = Brushes.Gray,
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
            row.NewNameText.Foreground = isChecked && newName != row.OriginalName ? Brushes.Black : Brushes.Gray;
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
        private void ApplyButton_Click(object sender, RoutedEventArgs e)
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
                    _trueOriginalNames[el.Id] = el.Name ?? "";
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
            RenderRows();
            UpdatePendingChangesText();
        }

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
        private void FinalApplyButton_Click(object sender, RoutedEventArgs e)
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

            MergeDuplicateMaterials = DuplicateMergeRadio?.IsChecked == true;
            Result = result;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

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
