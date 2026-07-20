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
    // NAMER(NamerWindow)와 같은 UI 패턴(필터+모드 콤보, 체크박스 드래그, 2단계 적용)을 그대로 재사용해
    // "여러 유형을 한 번에 선택해서 재료를 일괄 지정"하는 창. NAMER가 문자열 이름을 바꾸는 것과 달리
    // 이 창은 MaterialSlotFinder가 찾아낸 "현재 재료" ElementId를 바꾼다.
    public partial class MaterialAssignWindow : Window
    {
        private sealed class MaterialCandidate
        {
            public ElementType Type = null!;
            public MaterialSlot Slot;
        }

        private sealed class MaterialRow
        {
            public ElementId TypeId = ElementId.InvalidElementId;
            public string TypeName = "";
            public CheckBox CheckBox = null!;
            public TextBlock TypeNameText = null!;
            public TextBlock CurrentMaterialText = null!;
            public TextBlock NewMaterialText = null!;
        }

        // "재료 삭제" 탭의 행 - 유형 재료 지정과 달리 미리보기할 "새 값"이 없으므로 체크박스+이름 하나뿐이다.
        private sealed class DeleteRow
        {
            public ElementId MaterialId = ElementId.InvalidElementId;
            public CheckBox CheckBox = null!;
        }

        // MaterialCombo(지정할 재료 콤보)에 "(지정된 유형 없음)" 안내를 같이 보여주기 위한 표시용 래퍼.
        // DisplayMemberPath는 프로퍼티 이름만 바인딩할 수 있어 Material.Name을 그대로 못 쓰고 감싸야 한다.
        // 확인된 라이브 버그: 처음에 Material/DisplayName을 필드로 선언했었는데, WPF의 DisplayMemberPath는
        // 필드가 아니라 프로퍼티만 바인딩할 수 있어(리플렉션 시 PropertyDescriptor만 찾음) 값을 못 찾고
        // 조용히 빈 문자열을 보여줬다 - 콤보 자체엔 항목이 다 들어있지만 전부 빈 줄로 보여서 "재료가 하나도
        // 안 보이는" 것처럼 보였다. 반드시 { get; set; } 프로퍼티여야 한다.
        private sealed class MaterialOption
        {
            public Material Material { get; set; } = null!;
            public string DisplayName { get; set; } = "";
        }

        private readonly Document _doc;
        private readonly HashSet<ElementId> _checkedIds = new();
        private readonly Dictionary<ElementId, MaterialCandidate> _candidatesById = new();

        // "적용"은 이 두 딕셔너리만 갱신하고 모델은 건드리지 않는다 - NamerWindow의 2단계 적용
        // (_trueOriginalNames/_workingNames)과 동일한 설계이며, 최종 적용도 체크 여부와 무관하게
        // _workingMaterialIds를 기준으로 반영한다(NamerWindow의 최근 수정과 동일한 이유).
        private readonly Dictionary<ElementId, ElementId> _trueOriginalMaterialIds = new();
        private readonly Dictionary<ElementId, ElementId> _workingMaterialIds = new();

        private List<MaterialCandidate> _allCandidates = new();
        private List<MaterialCandidate> _filteredCandidates = new();
        private readonly List<MaterialRow> _rows = new();
        private List<Material> _allMaterials = new();
        private List<MaterialOption> _materialOptions = new();
        // 문서 안의 어떤 유형(_allCandidates)이든 현재 재료로 참조하고 있는 재료 id 집합 - "패밀리/유형이
        // 지정 안 된 재료"를 가려내기 위해 LoadCandidates()에서 채운다(LoadMaterials()보다 먼저 호출돼야 함).
        private HashSet<ElementId> _usedMaterialIds = new();
        private ElementId _selectedMaterialId = ElementId.InvalidElementId;

        private bool _dragging;
        private bool _dragTargetChecked;
        private MaterialRow? _lastDragRow;

        private const int PageSize = 200;
        private int _renderedCount;
        private Button? _loadMoreButton;

        // ===================== "클래스/설명 변경" 탭 상태 =====================
        // "재료 지정" 탭과 동일한 2단계 적용 설계(_trueOriginal.../_working...)를 클래스/설명 두 필드에
        // 각각 독립적으로 적용한 것 - 한 세션 안에서 어떤 재료는 클래스만, 어떤 재료는 설명만, 또는 둘 다
        // 바꿀 수 있어야 하므로 필드별로 딕셔너리를 분리했다.
        private sealed class IdentityRow
        {
            public ElementId MaterialId = ElementId.InvalidElementId;
            public CheckBox CheckBox = null!;
            public TextBlock NameText = null!;
            public TextBlock ClassText = null!;
            public TextBlock DescriptionText = null!;
        }

        private readonly HashSet<ElementId> _idCheckedIds = new();
        private readonly Dictionary<ElementId, string> _trueOriginalClass = new();
        private readonly Dictionary<ElementId, string> _workingClass = new();
        private readonly Dictionary<ElementId, string> _trueOriginalDescription = new();
        private readonly Dictionary<ElementId, string> _workingDescription = new();
        private List<Material> _filteredIdentityMaterials = new();
        private readonly List<IdentityRow> _identityRows = new();
        private bool _idDragging;
        private bool _idDragTargetChecked;
        private IdentityRow? _idLastDragRow;

        public List<(ElementId MaterialId, string? NewClass, string? NewDescription)>? IdentityResult { get; private set; }

        // ===================== "재료 삭제" 탭 상태 =====================
        // 재료 개수는 보통 "유형" 개수보다 훨씬 적으므로(수십~수백 개 수준), 재료 지정 탭의 유형 목록과
        // 달리 페이지네이션 없이 필터된 전체를 한 번에 그린다.
        private readonly HashSet<ElementId> _deleteCheckedIds = new();
        private List<Material> _filteredDeleteMaterials = new();
        private readonly List<DeleteRow> _deleteRows = new();
        private bool _delDragging;
        private bool _delDragTargetChecked;
        private DeleteRow? _delLastDragRow;

        public List<(ElementId TypeId, ElementId NewMaterialId)>? Result { get; private set; }
        public List<ElementId>? DeleteResult { get; private set; }

        public MaterialAssignWindow(Document doc, List<ElementId> preSelectedIds)
        {
            InitializeComponent();
            _doc = doc;

            // LoadCandidates()가 _usedMaterialIds(어떤 재료가 실제로 어떤 유형에 쓰이고 있는지)를 먼저
            // 채워야, LoadMaterials()가 "(지정된 유형 없음)" 표시 여부를 판단할 수 있다.
            LoadCandidates();
            LoadMaterials();

            HashSet<ElementId> resolvedPreSelection = ResolvePreSelection(preSelectedIds);
            foreach (ElementId id in resolvedPreSelection)
            {
                if (_candidatesById.ContainsKey(id)) _checkedIds.Add(id);
            }

            RenderRows();
            UpdatePendingChangesText();
            DelRenderRows();
            IdRenderRows();
        }

        private void LoadMaterials()
        {
            _allMaterials = new FilteredElementCollector(_doc).OfClass(typeof(Material))
                .Cast<Material>()
                .OrderBy(m => m.Name)
                .ToList();

            _materialOptions = _allMaterials
                .Select(m => new MaterialOption { Material = m, DisplayName = MaterialDisplayName(m) })
                .ToList();
            MaterialCombo.ItemsSource = _materialOptions;
        }

        // 재료 하나가 지금 어떤 유형에도 쓰이고 있지 않으면(_usedMaterialIds에 없으면) "(지정된 유형 없음)"을
        // 덧붙여, 재료 콤보/삭제 탭 어디서든 "가족/유형이 지정 안 된 재료"를 바로 알아볼 수 있게 한다.
        private string MaterialDisplayName(Material m)
        {
            string name = m.Name ?? "";
            return _usedMaterialIds.Contains(m.Id) ? name : $"{name} (지정된 유형 없음)";
        }

        // 문서의 모든 ElementType 중 MaterialSlotFinder가 "현재 재료" 슬롯을 찾을 수 있는 것만 후보로 삼는다.
        private void LoadCandidates()
        {
            var allTypes = new FilteredElementCollector(_doc).WhereElementIsElementType().ToElements();
            _allCandidates = new List<MaterialCandidate>();
            foreach (Element el in allTypes)
            {
                if (el is not ElementType type) continue;
                MaterialSlot? slot = MaterialSlotFinder.Find(type);
                if (slot == null) continue;

                var candidate = new MaterialCandidate { Type = type, Slot = slot.Value };
                _allCandidates.Add(candidate);
                _candidatesById[type.Id] = candidate;
            }
            _allCandidates = _allCandidates.OrderBy(c => c.Type.Name).ToList();

            _usedMaterialIds = new HashSet<ElementId>(_allCandidates
                .Select(c => c.Slot.MaterialId)
                .Where(id => id != ElementId.InvalidElementId));
        }

        // NamerWindow.ResolvePreSelectionForCategory의 Type 케이스와 동일한 이유로 인스턴스→유형 변환이
        // 필요하다: Revit에서 미리 선택하는 건 거의 항상 벽/바닥 같은 모델 인스턴스이지 유형 자체가 아니다.
        private HashSet<ElementId> ResolvePreSelection(List<ElementId> preSelectedIds)
        {
            var resolved = new HashSet<ElementId>();
            foreach (ElementId id in preSelectedIds)
            {
                Element? el = _doc.GetElement(id);
                if (el == null) continue;

                if (el is ElementType) resolved.Add(el.Id);
                else
                {
                    ElementId typeId = el.GetTypeId();
                    if (typeId != ElementId.InvalidElementId) resolved.Add(typeId);
                }
            }
            return resolved;
        }

        private ElementId CurrentMaterialId(MaterialCandidate candidate) =>
            _workingMaterialIds.TryGetValue(candidate.Type.Id, out ElementId workingId) ? workingId : candidate.Slot.MaterialId;

        private string CurrentMaterialName(MaterialCandidate candidate) => MaterialNameOf(CurrentMaterialId(candidate));

        private string MaterialNameOf(ElementId id)
        {
            if (id == ElementId.InvalidElementId) return "지정되지않음";
            return (_doc.GetElement(id) as Material)?.Name ?? "지정되지않음";
        }

        // ===================== 유형 필터/렌더링 (NamerWindow와 동일한 구조) =====================

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RenderRows();

        private void FilterModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ItemsPanel == null) return;
            RenderRows();
        }

        // FilterModeCombo와 같은 이유(ComboBoxItem의 SelectedIndex="0" 기본값이 InitializeComponent 도중
        // SelectionChanged를 먼저 발생시킴)로 ItemsPanel==null 가드가 필요하다.
        // "재료 없음"(index 2)은 문자열 검색이 아니라 "현재 재료가 지정 안 됐는지" 여부만 보는 조건이라
        // 필터 텍스트박스/모드 콤보 둘 다 쓸 데가 없으므로 비활성화한다.
        private void FilterTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ItemsPanel == null) return;

            bool noMaterialMode = FilterTargetCombo.SelectedIndex == 2;
            FilterBox.IsEnabled = !noMaterialMode;
            FilterModeCombo.IsEnabled = !noMaterialMode;
            RenderRows();
        }

        private void RenderRows()
        {
            ItemsPanel.Children.Clear();
            _rows.Clear();
            _renderedCount = 0;
            _loadMoreButton = null;

            int filterTarget = FilterTargetCombo?.SelectedIndex ?? 0;

            if (filterTarget == 2)
            {
                _filteredCandidates = _allCandidates.Where(c => CurrentMaterialId(c) == ElementId.InvalidElementId).ToList();
            }
            else
            {
                string filter = FilterBox?.Text ?? "";
                int filterMode = FilterModeCombo?.SelectedIndex ?? 0;
                bool filterByMaterial = filterTarget == 1;

                if (string.IsNullOrEmpty(filter))
                {
                    _filteredCandidates = _allCandidates;
                }
                else
                {
                    _filteredCandidates = _allCandidates.Where(c =>
                    {
                        string name = filterByMaterial ? CurrentMaterialName(c) : (c.Type.Name ?? "");
                        bool contains = name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
                        bool exact = string.Equals(name, filter, StringComparison.CurrentCultureIgnoreCase);
                        bool startsWith = name.StartsWith(filter, StringComparison.CurrentCultureIgnoreCase);
                        bool endsWith = name.EndsWith(filter, StringComparison.CurrentCultureIgnoreCase);
                        return filterMode switch
                        {
                            0 => contains,
                            1 => !contains,
                            2 => exact,
                            3 => !exact,
                            4 => startsWith,
                            5 => endsWith,
                            _ => contains,
                        };
                    }).ToList();
                }
            }

            var allIds = new HashSet<ElementId>(_allCandidates.Select(c => c.Type.Id));
            var visibleIds = new HashSet<ElementId>(_filteredCandidates.Select(c => c.Type.Id));
            _checkedIds.RemoveWhere(id => allIds.Contains(id) && !visibleIds.Contains(id));

            RenderMoreRows();
            UpdatePendingChangesText();
        }

        // NamerWindow의 이름 열 너비 조절과 같은 방식(자세한 이유는 NamerWindow.xaml.cs의
        // NameColumnSplitter_DragDelta 참고) - 헤더 GridSplitter를 드래그하는 동안 계속 발생하며,
        // 레이아웃을 강제로 갱신한 뒤 이미 그려진 모든 행의 세 TextBlock 너비를 다시 써서 맞춘다.
        private void ColumnSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            UpdateLayout();
            foreach (MaterialRow row in _rows)
            {
                row.TypeNameText.Width = TypeNameColumn.ActualWidth;
                row.CurrentMaterialText.Width = CurrentMaterialColumn.ActualWidth;
                row.NewMaterialText.Width = NewMaterialColumn.ActualWidth;
            }
        }

        private void RenderMoreRows()
        {
            if (_loadMoreButton != null)
            {
                ItemsPanel.Children.Remove(_loadMoreButton);
                _loadMoreButton = null;
            }

            int start = _renderedCount;
            int end = Math.Min(start + PageSize, _filteredCandidates.Count);

            for (int i = start; i < end; i++)
            {
                MaterialCandidate candidate = _filteredCandidates[i];
                var row = new MaterialRow { TypeId = candidate.Type.Id, TypeName = candidate.Type.Name ?? "" };

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
                    IsChecked = _checkedIds.Contains(row.TypeId),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsHitTestVisible = false
                };
                checkBox.Checked += (_, _) => { _checkedIds.Add(row.TypeId); UpdateRowPreview(row); UpdatePendingChangesText(); };
                checkBox.Unchecked += (_, _) => { _checkedIds.Remove(row.TypeId); UpdateRowPreview(row); UpdatePendingChangesText(); };
                row.CheckBox = checkBox;
                rowPanel.Children.Add(checkBox);

                var typeNameText = new TextBlock
                {
                    Text = row.TypeName,
                    Width = TypeNameColumn.ActualWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.TypeNameText = typeNameText;
                rowPanel.Children.Add(typeNameText);

                var currentMaterialText = new TextBlock
                {
                    Width = CurrentMaterialColumn.ActualWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.CurrentMaterialText = currentMaterialText;
                rowPanel.Children.Add(currentMaterialText);

                rowPanel.Children.Add(new TextBlock
                {
                    Text = " → ",
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var newMaterialText = new TextBlock
                {
                    Width = NewMaterialColumn.ActualWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold
                };
                row.NewMaterialText = newMaterialText;
                rowPanel.Children.Add(newMaterialText);

                _rows.Add(row);
                ItemsPanel.Children.Add(rowPanel);
                UpdateRowPreview(row);
            }

            _renderedCount = end;

            if (_renderedCount < _filteredCandidates.Count)
            {
                int remaining = _filteredCandidates.Count - _renderedCount;
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

        // 체크된 항목만 "적용하면 이렇게 바뀝니다" 미리보기를 보여준다. 현재 재료 칸은 체크 여부와 무관하게
        // 항상 최신 작업중 상태(이전 "적용"들의 누적 결과)를 보여준다 - NamerWindow의 WorkingNameOf와 동일한 이유.
        private void UpdateRowPreview(MaterialRow row)
        {
            if (!_candidatesById.TryGetValue(row.TypeId, out MaterialCandidate? candidate)) return;

            string currentName = CurrentMaterialName(candidate);
            row.CurrentMaterialText.Text = currentName;

            bool isChecked = _checkedIds.Contains(row.TypeId);
            if (isChecked && _selectedMaterialId != ElementId.InvalidElementId)
            {
                string newName = MaterialNameOf(_selectedMaterialId);
                row.NewMaterialText.Text = newName;
                row.NewMaterialText.Foreground = newName != currentName ? Brushes.Black : Brushes.Gray;
            }
            else
            {
                row.NewMaterialText.Text = currentName;
                row.NewMaterialText.Foreground = Brushes.Gray;
            }
        }

        private void RefreshPreview()
        {
            foreach (MaterialRow row in _rows) UpdateRowPreview(row);
        }

        // ===================== 지정할 재료 선택 =====================

        private void MaterialFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = MaterialFilterBox.Text ?? "";
            List<MaterialOption> filtered = string.IsNullOrEmpty(filter)
                ? _materialOptions
                : _materialOptions.Where(o => (o.Material.Name ?? "").IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            MaterialCombo.ItemsSource = filtered;
            MaterialCombo.IsDropDownOpen = filtered.Count > 0;
        }

        private void MaterialCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MaterialCombo.SelectedItem is MaterialOption option)
            {
                _selectedMaterialId = option.Material.Id;
                SelectedMaterialText.Text = $"선택됨: {option.Material.Name}";
            }
            else
            {
                _selectedMaterialId = ElementId.InvalidElementId;
                SelectedMaterialText.Text = "";
            }
            RefreshPreview();
        }

        // ===================== 드래그로 여러 행 체크/해제 (NamerWindow와 동일) =====================

        private void RowPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not MaterialRow row) return;

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

            MaterialRow? row = FindRowFromVisual(hit.VisualHit);
            if (row == null || row == _lastDragRow) return;

            ApplyDragState(row);
            _lastDragRow = row;
        }

        private void ItemsPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

        private void ItemsPanel_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

        private void EndDrag()
        {
            _dragging = false;
            _lastDragRow = null;
            if (ItemsPanel.IsMouseCaptured) ItemsPanel.ReleaseMouseCapture();
        }

        private void ApplyDragState(MaterialRow row)
        {
            row.CheckBox.IsChecked = _dragTargetChecked;
            UpdateCountText();
        }

        private static MaterialRow? FindRowFromVisual(DependencyObject visual)
        {
            DependencyObject? current = visual;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag is MaterialRow row) return row;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void UpdateCountText()
        {
            int checkedInFiltered = _filteredCandidates.Count(c => _checkedIds.Contains(c.Type.Id));
            CountText.Text = $"{_renderedCount} / {_filteredCandidates.Count}개 표시 중 (전체 {_allCandidates.Count}개), 선택됨 {checkedInFiltered}개";
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (MaterialCandidate c in _filteredCandidates) _checkedIds.Add(c.Type.Id);
            foreach (MaterialRow row in _rows) row.CheckBox.IsChecked = true;
            UpdateCountText();
            UpdatePendingChangesText();
        }

        private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (MaterialCandidate c in _filteredCandidates) _checkedIds.Remove(c.Type.Id);
            foreach (MaterialRow row in _rows) row.CheckBox.IsChecked = false;
            UpdateCountText();
            UpdatePendingChangesText();
        }

        // ===================== 적용(창 안에서만)/최종 적용(모델에 반영)/취소 =====================

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMaterialId == ElementId.InvalidElementId)
            {
                MessageBox.Show("먼저 지정할 재료를 선택하세요.", "재료 지정", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var appliedIds = new List<ElementId>();
            foreach (MaterialCandidate candidate in _allCandidates)
            {
                if (!_checkedIds.Contains(candidate.Type.Id)) continue;

                ElementId current = _workingMaterialIds.TryGetValue(candidate.Type.Id, out ElementId w) ? w : candidate.Slot.MaterialId;
                if (_selectedMaterialId == current) continue;

                // 이 유형이 세션 중 처음으로 실제 바뀌는 순간에만 "진짜 원래 재료"를 기록한다.
                if (!_trueOriginalMaterialIds.ContainsKey(candidate.Type.Id))
                    _trueOriginalMaterialIds[candidate.Type.Id] = candidate.Slot.MaterialId;
                _workingMaterialIds[candidate.Type.Id] = _selectedMaterialId;
                appliedIds.Add(candidate.Type.Id);
            }

            if (appliedIds.Count == 0)
            {
                MessageBox.Show("변경될 항목이 없습니다.", "재료 지정", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 적용된 항목은 체크를 자동으로 해제한다 - 그대로 두면 다음에 다른 재료를 골라 "적용"을 또
            // 누를 때 이전에 이미 처리한 항목까지 체크된 채로 남아있어서, 의도치 않게 그 항목들까지 새
            // 재료로 다시 덮어써진다("체크했던 게 캐시처럼 계속 남아있으면 안 된다"는 라이브 피드백으로 수정).
            foreach (ElementId id in appliedIds) _checkedIds.Remove(id);

            RenderRows();
            UpdatePendingChangesText();
        }

        // NamerWindow.FinalApplyButton_Click과 동일하게, 체크 여부와 무관하게 _workingMaterialIds에 누적된
        // (=지난 "적용" 클릭들로 확정된) 변경 사항만 그대로 모델에 옮겨 쓴다.
        private void FinalApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var result = new List<(ElementId, ElementId)>();
            foreach (KeyValuePair<ElementId, ElementId> kvp in _workingMaterialIds)
            {
                ElementId trueOriginal = _trueOriginalMaterialIds.TryGetValue(kvp.Key, out ElementId orig) ? orig : kvp.Value;
                if (kvp.Value != trueOriginal) result.Add((kvp.Key, kvp.Value));
            }

            if (result.Count == 0)
            {
                MessageBox.Show("모델에 적용할 변경 사항이 없습니다. 먼저 유형을 선택하고 재료를 지정한 뒤 '적용'을 눌러보세요.", "재료 지정", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Result = result;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void UpdatePendingChangesText()
        {
            int pending = _workingMaterialIds.Count(kvp =>
                _trueOriginalMaterialIds.TryGetValue(kvp.Key, out ElementId orig) && kvp.Value != orig);
            PendingChangesText.Text = pending == 0
                ? "모델에 아직 반영되지 않은 변경 사항이 없습니다."
                : $"아직 모델에 반영되지 않은 변경 사항 {pending}개 — '최종 적용'을 눌러야 실제로 저장됩니다.";
        }

        // ===================== "재료 삭제" 탭 =====================

        private void DelFilterBox_TextChanged(object sender, TextChangedEventArgs e) => DelRenderRows();

        private void DelFilterModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DelItemsPanel == null) return;
            DelRenderRows();
        }

        private void DelRenderRows()
        {
            DelItemsPanel.Children.Clear();
            _deleteRows.Clear();

            string filter = DelFilterBox?.Text ?? "";
            int filterMode = DelFilterModeCombo?.SelectedIndex ?? 0;

            if (string.IsNullOrEmpty(filter))
            {
                _filteredDeleteMaterials = _allMaterials;
            }
            else
            {
                _filteredDeleteMaterials = _allMaterials.Where(m =>
                {
                    string name = m.Name ?? "";
                    bool contains = name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
                    bool exact = string.Equals(name, filter, StringComparison.CurrentCultureIgnoreCase);
                    bool startsWith = name.StartsWith(filter, StringComparison.CurrentCultureIgnoreCase);
                    bool endsWith = name.EndsWith(filter, StringComparison.CurrentCultureIgnoreCase);
                    return filterMode switch
                    {
                        0 => contains,
                        1 => !contains,
                        2 => exact,
                        3 => !exact,
                        4 => startsWith,
                        5 => endsWith,
                        _ => contains,
                    };
                }).ToList();
            }

            var allIds = new HashSet<ElementId>(_allMaterials.Select(m => m.Id));
            var visibleIds = new HashSet<ElementId>(_filteredDeleteMaterials.Select(m => m.Id));
            _deleteCheckedIds.RemoveWhere(id => allIds.Contains(id) && !visibleIds.Contains(id));

            foreach (Material mat in _filteredDeleteMaterials)
            {
                var row = new DeleteRow { MaterialId = mat.Id };

                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(2, 3, 2, 3),
                    Background = Brushes.Transparent,
                    Tag = row
                };
                rowPanel.MouseLeftButtonDown += DelRowPanel_MouseLeftButtonDown;

                var checkBox = new CheckBox
                {
                    IsChecked = _deleteCheckedIds.Contains(row.MaterialId),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsHitTestVisible = false
                };
                checkBox.Checked += (_, _) => { _deleteCheckedIds.Add(row.MaterialId); DelUpdateCountText(); };
                checkBox.Unchecked += (_, _) => { _deleteCheckedIds.Remove(row.MaterialId); DelUpdateCountText(); };
                row.CheckBox = checkBox;
                rowPanel.Children.Add(checkBox);

                rowPanel.Children.Add(new TextBlock
                {
                    Text = MaterialDisplayName(mat),
                    Width = 300,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });

                _deleteRows.Add(row);
                DelItemsPanel.Children.Add(rowPanel);
            }

            DelUpdateCountText();
        }

        private void DelRowPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not DeleteRow row) return;

            _delDragging = true;
            _delDragTargetChecked = row.CheckBox.IsChecked != true;
            DelApplyDragState(row);
            _delLastDragRow = row;

            DelItemsPanel.CaptureMouse();
            e.Handled = true;
        }

        private void DelItemsPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_delDragging) return;

            System.Windows.Point pos = e.GetPosition(DelItemsPanel);
            HitTestResult hit = VisualTreeHelper.HitTest(DelItemsPanel, pos);
            if (hit == null) return;

            DeleteRow? row = FindDeleteRowFromVisual(hit.VisualHit);
            if (row == null || row == _delLastDragRow) return;

            DelApplyDragState(row);
            _delLastDragRow = row;
        }

        private void DelItemsPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => DelEndDrag();

        private void DelItemsPanel_LostMouseCapture(object sender, MouseEventArgs e) => DelEndDrag();

        private void DelEndDrag()
        {
            _delDragging = false;
            _delLastDragRow = null;
            if (DelItemsPanel.IsMouseCaptured) DelItemsPanel.ReleaseMouseCapture();
        }

        private void DelApplyDragState(DeleteRow row)
        {
            row.CheckBox.IsChecked = _delDragTargetChecked;
            DelUpdateCountText();
        }

        private static DeleteRow? FindDeleteRowFromVisual(DependencyObject visual)
        {
            DependencyObject? current = visual;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag is DeleteRow row) return row;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void DelUpdateCountText()
        {
            int checkedInFiltered = _filteredDeleteMaterials.Count(m => _deleteCheckedIds.Contains(m.Id));
            DelCountText.Text = $"{_filteredDeleteMaterials.Count}개 표시 중 (전체 {_allMaterials.Count}개), 선택됨 {checkedInFiltered}개";
        }

        private void DelSelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (Material m in _filteredDeleteMaterials) _deleteCheckedIds.Add(m.Id);
            foreach (DeleteRow row in _deleteRows) row.CheckBox.IsChecked = true;
            DelUpdateCountText();
        }

        private void DelSelectNoneButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (Material m in _filteredDeleteMaterials) _deleteCheckedIds.Remove(m.Id);
            foreach (DeleteRow row in _deleteRows) row.CheckBox.IsChecked = false;
            DelUpdateCountText();
        }

        // 재료 지정 탭과 달리 "적용 미리보기" 단계가 없다 - 삭제는 미리 보여줄 "새 값"이 없는 이진 동작이라,
        // 체크한 것을 바로 확인 대화상자 하나로 확정한다(단, 실제 반영은 여전히 창을 닫은 뒤 Command의
        // Transaction 안에서 이뤄진다 - 이 창 자체는 모델을 건드리지 않는다).
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_deleteCheckedIds.Count == 0)
            {
                MessageBox.Show("삭제할 재료를 먼저 선택하세요.", "재료 삭제", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<Material> toDelete = _allMaterials.Where(m => _deleteCheckedIds.Contains(m.Id)).ToList();
            string preview = string.Join(", ", toDelete.Take(10).Select(m => m.Name));
            if (toDelete.Count > 10) preview += " 등";

            MessageBoxResult confirm = MessageBox.Show(
                $"선택한 재료 {toDelete.Count}개를 삭제하시겠습니까?\n({preview})\n\n다른 유형/부재가 사용 중이던 재료라도 삭제되며, 그 유형/부재의 재료 지정은 해제된 상태로 남습니다.",
                "재료 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            DeleteResult = toDelete.Select(m => m.Id).ToList();
            DialogResult = true;
            Close();
        }

        // ===================== "클래스/설명 변경" 탭 =====================
        // 재료의 '클래스'는 Material.MaterialClass에 직접 읽고 쓸 수 있지만(2026 RevitAPI를
        // MetadataLoadContext로 리플렉션해 확인), '설명'은 Material 클래스 자체엔 전용 프로퍼티가 없고
        // 아이덴티티 데이터 탭의 다른 요소들과 공유하는 범용 파라미터인 BuiltInParameter.ALL_MODEL_DESCRIPTION을
        // 통해 읽고 쓴다 - 리플렉션으로는 어떤 파라미터가 실제로 재료 인스턴스에 붙는지까지는 확인할 수 없으므로,
        // 라이브 테스트로 재확인 전까지는 최선의 추정이다.

        private static string CurrentClassOf(Material m) => m.MaterialClass ?? "";

        private static string CurrentDescriptionOf(Material m) =>
            m.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? "";

        private string WorkingClassOf(Material m) =>
            _workingClass.TryGetValue(m.Id, out string? w) ? w : CurrentClassOf(m);

        private string WorkingDescriptionOf(Material m) =>
            _workingDescription.TryGetValue(m.Id, out string? w) ? w : CurrentDescriptionOf(m);

        private void IdFilterBox_TextChanged(object sender, TextChangedEventArgs e) => IdRenderRows();

        private void IdFilterModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IdItemsPanel == null) return;
            IdRenderRows();
        }

        private void IdRenderRows()
        {
            IdItemsPanel.Children.Clear();
            _identityRows.Clear();

            string filter = IdFilterBox?.Text ?? "";
            int filterMode = IdFilterModeCombo?.SelectedIndex ?? 0;

            if (string.IsNullOrEmpty(filter))
            {
                _filteredIdentityMaterials = _allMaterials;
            }
            else
            {
                _filteredIdentityMaterials = _allMaterials.Where(m =>
                {
                    string name = m.Name ?? "";
                    bool contains = name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
                    bool exact = string.Equals(name, filter, StringComparison.CurrentCultureIgnoreCase);
                    bool startsWith = name.StartsWith(filter, StringComparison.CurrentCultureIgnoreCase);
                    bool endsWith = name.EndsWith(filter, StringComparison.CurrentCultureIgnoreCase);
                    return filterMode switch
                    {
                        0 => contains,
                        1 => !contains,
                        2 => exact,
                        3 => !exact,
                        4 => startsWith,
                        5 => endsWith,
                        _ => contains,
                    };
                }).ToList();
            }

            var allIds = new HashSet<ElementId>(_allMaterials.Select(m => m.Id));
            var visibleIds = new HashSet<ElementId>(_filteredIdentityMaterials.Select(m => m.Id));
            _idCheckedIds.RemoveWhere(id => allIds.Contains(id) && !visibleIds.Contains(id));

            foreach (Material mat in _filteredIdentityMaterials)
            {
                var row = new IdentityRow { MaterialId = mat.Id };

                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(2, 3, 2, 3),
                    Background = Brushes.Transparent,
                    Tag = row
                };
                rowPanel.MouseLeftButtonDown += IdRowPanel_MouseLeftButtonDown;

                var checkBox = new CheckBox
                {
                    IsChecked = _idCheckedIds.Contains(row.MaterialId),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    IsHitTestVisible = false
                };
                checkBox.Checked += (_, _) => { _idCheckedIds.Add(row.MaterialId); IdUpdateCountText(); };
                checkBox.Unchecked += (_, _) => { _idCheckedIds.Remove(row.MaterialId); IdUpdateCountText(); };
                row.CheckBox = checkBox;
                rowPanel.Children.Add(checkBox);

                var nameText = new TextBlock
                {
                    Text = MaterialDisplayName(mat),
                    Width = IdNameColumn.ActualWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.NameText = nameText;
                rowPanel.Children.Add(nameText);

                var classText = new TextBlock
                {
                    Width = IdClassColumn.ActualWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.ClassText = classText;
                rowPanel.Children.Add(classText);

                var descriptionText = new TextBlock
                {
                    Width = IdDescriptionColumn.ActualWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.DescriptionText = descriptionText;
                rowPanel.Children.Add(descriptionText);

                _identityRows.Add(row);
                IdItemsPanel.Children.Add(rowPanel);
                IdUpdateRowPreview(row, mat);
            }

            IdUpdateCountText();
            IdUpdatePendingChangesText();
        }

        // 다른 열 너비 조절과 같은 방식(ColumnSplitter_DragDelta 참고) - 드래그하는 동안 계속 발생하며,
        // 레이아웃을 강제로 갱신한 뒤 이미 그려진 모든 행의 세 TextBlock 너비를 다시 써서 맞춘다.
        private void IdColumnSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            UpdateLayout();
            foreach (IdentityRow row in _identityRows)
            {
                row.NameText.Width = IdNameColumn.ActualWidth;
                row.ClassText.Width = IdClassColumn.ActualWidth;
                row.DescriptionText.Width = IdDescriptionColumn.ActualWidth;
            }
        }

        // 클래스/설명 칸은 각각 현재(=이전 "적용"들의 누적) 작업중 값을 항상 보여주고, 체크된 채로 "적용"을
        // 누르면 실제로 바뀔 필드만 검은색 굵게, 나머지는 회색으로 표시한다 - "재료 지정" 탭의
        // UpdateRowPreview와 같은 방식이다.
        private void IdUpdateRowPreview(IdentityRow row, Material mat)
        {
            string workingClass = WorkingClassOf(mat);
            string workingDescription = WorkingDescriptionOf(mat);

            bool isChecked = _idCheckedIds.Contains(row.MaterialId);
            int targetField = IdTargetFieldCombo?.SelectedIndex ?? 0;
            string newValue = IdNewValueBox?.Text ?? "";

            if (isChecked && targetField == 0 && newValue != workingClass)
            {
                row.ClassText.Text = $"{workingClass} → {newValue}";
                row.ClassText.Foreground = Brushes.Black;
                row.ClassText.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                row.ClassText.Text = workingClass;
                row.ClassText.Foreground = Brushes.Gray;
                row.ClassText.FontWeight = FontWeights.Normal;
            }

            if (isChecked && targetField == 1 && newValue != workingDescription)
            {
                row.DescriptionText.Text = $"{workingDescription} → {newValue}";
                row.DescriptionText.Foreground = Brushes.Black;
                row.DescriptionText.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                row.DescriptionText.Text = workingDescription;
                row.DescriptionText.Foreground = Brushes.Gray;
                row.DescriptionText.FontWeight = FontWeights.Normal;
            }
        }

        // ItemsPanel==null 가드와 같은 이유(SelectedIndex="0" 기본값이 InitializeComponent 도중
        // SelectionChanged를 먼저 발생시킴)로 _identityRows가 아직 없을 수 있어 IdRefreshPreview가
        // 빈 리스트를 대상으로 안전하게 아무 일도 하지 않는다.
        private void IdTargetFieldCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => IdRefreshPreview();

        private void IdNewValueBox_TextChanged(object sender, TextChangedEventArgs e) => IdRefreshPreview();

        private void IdRefreshPreview()
        {
            foreach (IdentityRow row in _identityRows)
            {
                if (_doc.GetElement(row.MaterialId) is Material mat) IdUpdateRowPreview(row, mat);
            }
        }

        private void IdRowPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not IdentityRow row) return;

            _idDragging = true;
            _idDragTargetChecked = row.CheckBox.IsChecked != true;
            IdApplyDragState(row);
            _idLastDragRow = row;

            IdItemsPanel.CaptureMouse();
            e.Handled = true;
        }

        private void IdItemsPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_idDragging) return;

            System.Windows.Point pos = e.GetPosition(IdItemsPanel);
            HitTestResult hit = VisualTreeHelper.HitTest(IdItemsPanel, pos);
            if (hit == null) return;

            IdentityRow? row = FindIdentityRowFromVisual(hit.VisualHit);
            if (row == null || row == _idLastDragRow) return;

            IdApplyDragState(row);
            _idLastDragRow = row;
        }

        private void IdItemsPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => IdEndDrag();

        private void IdItemsPanel_LostMouseCapture(object sender, MouseEventArgs e) => IdEndDrag();

        private void IdEndDrag()
        {
            _idDragging = false;
            _idLastDragRow = null;
            if (IdItemsPanel.IsMouseCaptured) IdItemsPanel.ReleaseMouseCapture();
        }

        private void IdApplyDragState(IdentityRow row)
        {
            row.CheckBox.IsChecked = _idDragTargetChecked;
            if (_doc.GetElement(row.MaterialId) is Material mat) IdUpdateRowPreview(row, mat);
            IdUpdateCountText();
        }

        private static IdentityRow? FindIdentityRowFromVisual(DependencyObject visual)
        {
            DependencyObject? current = visual;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag is IdentityRow row) return row;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void IdUpdateCountText()
        {
            int checkedInFiltered = _filteredIdentityMaterials.Count(m => _idCheckedIds.Contains(m.Id));
            IdCountText.Text = $"{_filteredIdentityMaterials.Count}개 표시 중 (전체 {_allMaterials.Count}개), 선택됨 {checkedInFiltered}개";
        }

        private void IdSelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (Material m in _filteredIdentityMaterials) _idCheckedIds.Add(m.Id);
            foreach (IdentityRow row in _identityRows) row.CheckBox.IsChecked = true;
            IdRefreshPreview();
            IdUpdateCountText();
        }

        private void IdSelectNoneButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (Material m in _filteredIdentityMaterials) _idCheckedIds.Remove(m.Id);
            foreach (IdentityRow row in _identityRows) row.CheckBox.IsChecked = false;
            IdRefreshPreview();
            IdUpdateCountText();
        }

        private void IdApplyButton_Click(object sender, RoutedEventArgs e)
        {
            int targetField = IdTargetFieldCombo.SelectedIndex;
            string newValue = IdNewValueBox.Text ?? "";

            var appliedIds = new List<ElementId>();
            foreach (Material mat in _allMaterials)
            {
                if (!_idCheckedIds.Contains(mat.Id)) continue;

                if (targetField == 0)
                {
                    string current = WorkingClassOf(mat);
                    if (newValue == current) continue;

                    if (!_trueOriginalClass.ContainsKey(mat.Id)) _trueOriginalClass[mat.Id] = CurrentClassOf(mat);
                    _workingClass[mat.Id] = newValue;
                }
                else
                {
                    string current = WorkingDescriptionOf(mat);
                    if (newValue == current) continue;

                    if (!_trueOriginalDescription.ContainsKey(mat.Id)) _trueOriginalDescription[mat.Id] = CurrentDescriptionOf(mat);
                    _workingDescription[mat.Id] = newValue;
                }
                appliedIds.Add(mat.Id);
            }

            if (appliedIds.Count == 0)
            {
                MessageBox.Show("변경될 항목이 없습니다.", "클래스/설명 변경", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // "재료 지정" 탭과 같은 이유("체크했던 게 캐시처럼 계속 남아있으면 안 된다")로 적용된 항목은
            // 체크를 자동 해제한다.
            foreach (ElementId id in appliedIds) _idCheckedIds.Remove(id);

            IdRenderRows();
        }

        // NamerWindow.FinalApplyButton_Click과 동일하게, 체크 여부와 무관하게 지난 "적용" 클릭들로
        // _workingClass/_workingDescription에 누적된 변경 사항만 그대로 모델에 옮겨 쓴다. 한 재료가
        // 클래스만, 설명만, 또는 둘 다 바뀌었을 수 있으므로 필드별로 독립적으로 비교해서 null(=이 필드는
        // 변경 없음)/새 값을 구분해 넘긴다.
        private void IdFinalApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var affectedIds = new HashSet<ElementId>(_workingClass.Keys.Concat(_workingDescription.Keys));
            var result = new List<(ElementId, string?, string?)>();

            foreach (ElementId id in affectedIds)
            {
                string? newClass = null;
                if (_workingClass.TryGetValue(id, out string? wc))
                {
                    string orig = _trueOriginalClass.TryGetValue(id, out string? o) ? o : wc;
                    if (wc != orig) newClass = wc;
                }

                string? newDescription = null;
                if (_workingDescription.TryGetValue(id, out string? wd))
                {
                    string orig = _trueOriginalDescription.TryGetValue(id, out string? o) ? o : wd;
                    if (wd != orig) newDescription = wd;
                }

                if (newClass != null || newDescription != null) result.Add((id, newClass, newDescription));
            }

            if (result.Count == 0)
            {
                MessageBox.Show("모델에 적용할 변경 사항이 없습니다. 먼저 재료를 선택하고 새 값을 입력한 뒤 '적용'을 눌러보세요.", "클래스/설명 변경", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IdentityResult = result;
            DialogResult = true;
            Close();
        }

        private void IdUpdatePendingChangesText()
        {
            int pending = new HashSet<ElementId>(_workingClass.Keys.Concat(_workingDescription.Keys)).Count(id =>
            {
                bool classChanged = _workingClass.TryGetValue(id, out string? wc) &&
                    _trueOriginalClass.TryGetValue(id, out string? oc) && wc != oc;
                bool descriptionChanged = _workingDescription.TryGetValue(id, out string? wd) &&
                    _trueOriginalDescription.TryGetValue(id, out string? od) && wd != od;
                return classChanged || descriptionChanged;
            });
            IdPendingChangesText.Text = pending == 0
                ? "모델에 아직 반영되지 않은 변경 사항이 없습니다."
                : $"아직 모델에 반영되지 않은 변경 사항 {pending}개 — '최종 적용'을 눌러야 실제로 저장됩니다.";
        }
    }
}
