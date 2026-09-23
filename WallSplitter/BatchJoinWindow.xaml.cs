using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
// Autodesk.Revit.DB와 System.Windows를 같이 쓰면 Grid/Control/Binding/Line/Point 같은 이름이 겹친다 -
// 이 프로젝트의 관례대로 겹치는 것만 별칭으로 구분한다(루트 CLAUDE.md 참고).
using RevitDocument = Autodesk.Revit.DB.Document;
using RevitView = Autodesk.Revit.DB.View;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // "일괄결합" 창. Revit 조회/변경은 전부 BatchJoinService가 한다(RoomBoundingWindow와 같은 구조).
    public partial class BatchJoinWindow : Window
    {
        private readonly RevitDocument _doc;
        private readonly RevitView? _activeView;

        // 창이 열릴 때의 선택. 모달이라 창이 떠 있는 동안 선택이 바뀔 수 없으므로 그대로 들고 있는다.
        private readonly List<ElementId> _selectionIds;
        private readonly int _selectionSupportedCount;

        private List<BatchJoinService.TypeState> _types = new List<BatchJoinService.TypeState>();

        // 생성자에서 RadioButton.IsChecked를 세팅하는 것만으로도 Checked 핸들러가 불린다 - 그때는 목록이
        // 아직 없으므로(IsInitialized는 InitializeComponent 직후 이미 true라 방어가 안 된다) 이 플래그로 막는다.
        private readonly bool _ready;

        // 체크 상태는 화면이 아니라 여기에 둔다 - 걸러내기나 적용 후 목록을 다시 그려도 고른 것이 풀리지
        // 않게 하기 위함(RoomBoundingWindow와 같은 이유).
        private readonly HashSet<int> _checkedTypeIds = new HashSet<int>();

        public BatchJoinWindow(RevitDocument doc, ICollection<ElementId> selectionIds, RevitView? activeView)
        {
            InitializeComponent();
            _doc = doc;
            _activeView = activeView;
            _selectionIds = selectionIds.ToList();
            _selectionSupportedCount = SelectedElements().Count;

            SelectionModeRadio.Content = _selectionSupportedCount > 0
                ? "지금 선택한 요소 (벽·보·가새 " + _selectionSupportedCount + "개)"
                : "지금 선택한 요소 (없음 - 창을 닫고 먼저 선택하세요)";
            SelectionModeRadio.IsEnabled = _selectionSupportedCount > 0;

            // 고른 것이 없으면 유형 모드로 시작한다 - 아무것도 못 하는 모드를 기본으로 두지 않는다.
            if (_selectionSupportedCount > 0) SelectionModeRadio.IsChecked = true;
            else TypeModeRadio.IsChecked = true;

            _types = BatchJoinService.CollectTypes(_doc, ViewFilter());
            BuildTypeList();
            UpdateState();
            _ready = true;
        }

        // "현재 뷰에 보이는 요소만"이 켜져 있을 때만 뷰를 넘긴다(null이면 모델 전체).
        private RevitView? ViewFilter() =>
            ActiveViewOnlyCheck.IsChecked == true ? _activeView : null;

        private bool TypeMode => TypeModeRadio.IsChecked == true;

        private List<Element> SelectedElements()
        {
            List<Element> found = new List<Element>();
            foreach (ElementId id in _selectionIds)
            {
                Element? element = _doc.GetElement(id);
                if (element != null && BatchJoinService.IsSupported(element)) found.Add(element);
            }
            return found;
        }

        private BatchJoinService.EndChoice EndChoice()
        {
            if (StartEndRadio.IsChecked == true) return BatchJoinService.EndChoice.Start;
            if (EndEndRadio.IsChecked == true) return BatchJoinService.EndChoice.End;
            return BatchJoinService.EndChoice.Both;
        }

        // ===== 목록 =====

        private void BuildTypeList()
        {
            TypeListPanel.Children.Clear();

            string filter = FilterBox.Text?.Trim() ?? "";
            List<BatchJoinService.TypeState> shown = string.IsNullOrEmpty(filter)
                ? _types
                : _types.Where(t => t.Matches(filter)).ToList();

            if (shown.Count == 0)
            {
                TypeListPanel.Children.Add(new TextBlock
                {
                    Text = _types.Count == 0
                        ? "끝 결합을 설정할 수 있는 요소(벽·보·가새)를 찾지 못했습니다."
                        : "걸러낸 조건에 맞는 유형이 없습니다.",
                    Foreground = Theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8),
                });
                return;
            }

            foreach (BatchJoinService.TypeState type in shown)
                TypeListPanel.Children.Add(BuildRow(type));
        }

        private UIElement BuildRow(BatchJoinService.TypeState type)
        {
            WpfCheckBox check = new WpfCheckBox
            {
                IsChecked = _checkedTypeIds.Contains(type.TypeId),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            check.Checked += (s, e) => { _checkedTypeIds.Add(type.TypeId); UpdateState(); };
            check.Unchecked += (s, e) => { _checkedTypeIds.Remove(type.TypeId); UpdateState(); };

            StackPanel info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = type.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock
            {
                Text = type.Summary(),
                FontSize = 11,
                // 이미 전부 결합 금지인 유형은 눈에 띄게 - 다시 걸 필요가 없다는 뜻이다.
                Foreground = type.AllowedEnds == 0 && type.DisallowedEnds > 0 ? Theme.WarningText : Theme.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            WpfGrid grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            WpfGrid.SetColumn(check, 0);
            WpfGrid.SetColumn(info, 1);
            grid.Children.Add(check);
            grid.Children.Add(info);

            Border row = new Border
            {
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = grid,
            };
            row.MouseLeftButtonDown += (s, e) => check.IsChecked = !(check.IsChecked ?? false);
            return row;
        }

        private void UpdateState()
        {
            bool typeMode = TypeMode;
            FilterBox.IsEnabled = typeMode;
            SelectAllButton.IsEnabled = typeMode;
            ClearAllButton.IsEnabled = typeMode;
            TypeListPanel.IsEnabled = typeMode;

            int targets = typeMode
                ? _types.Where(t => _checkedTypeIds.Contains(t.TypeId)).Sum(t => t.Instances)
                : _selectionSupportedCount;

            bool any = targets > 0;
            AllowButton.IsEnabled = any;
            DisallowButton.IsEnabled = any;

            if (!any)
            {
                StatusText.Text = typeMode
                    ? "유형을 하나 이상 골라주세요."
                    : "선택된 벽·보·가새가 없습니다.";
                return;
            }

            StatusText.Text = "대상 " + targets + "개 요소";
        }

        // ===== 이벤트 =====

        private void ScopeRadio_Changed(object sender, RoutedEventArgs e)
        {
            // 생성자에서 IsChecked를 세팅할 때도 불리므로, 목록이 다 만들어지기 전에는 무시한다.
            if (!_ready) return;
            UpdateState();
        }

        private void EndRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            UpdateState();
        }

        private void ActiveViewOnlyCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            ReloadTypes();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            BuildTypeList();
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            string filter = FilterBox.Text?.Trim() ?? "";
            // 걸러내기가 걸려 있으면 "지금 보이는 것"만 고른다 - 안 보이는 유형까지 몰래 고르면 위험하다.
            foreach (BatchJoinService.TypeState type in _types)
            {
                if (string.IsNullOrEmpty(filter) || type.Matches(filter)) _checkedTypeIds.Add(type.TypeId);
            }
            BuildTypeList();
            UpdateState();
        }

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            _checkedTypeIds.Clear();
            BuildTypeList();
            UpdateState();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void AllowButton_Click(object sender, RoutedEventArgs e) => Apply(true);

        private void DisallowButton_Click(object sender, RoutedEventArgs e) => Apply(false);

        private void ReloadTypes()
        {
            _types = BatchJoinService.CollectTypes(_doc, ViewFilter());
            BuildTypeList();
            UpdateState();
        }

        private void Apply(bool allow)
        {
            List<Element> targets = TypeMode
                ? BatchJoinService.CollectByTypes(_doc, ViewFilter(), new HashSet<int>(_checkedTypeIds))
                : SelectedElements();

            if (targets.Count == 0)
            {
                UpdateState();
                return;
            }

            BatchJoinService.ApplyResult result;
            using (Transaction tx = new Transaction(_doc, allow ? "일괄결합 - 결합 허용" : "일괄결합 - 결합 금지"))
            {
                tx.Start();
                result = BatchJoinService.Apply(targets, EndChoice(), allow);
                tx.Commit();
            }

            // 창을 닫지 않고 목록을 다시 읽어 바뀐 상태를 그 자리에서 보여준다 - 금지/허용을 번갈아 해 보는
            // 도구라, 매번 닫았다 여는 것보다 이 편이 낫다(고른 유형은 그대로 유지된다).
            _types = BatchJoinService.CollectTypes(_doc, ViewFilter());
            BuildTypeList();
            UpdateState();

            StatusText.Text = result.Summary(allow);
        }
    }
}
