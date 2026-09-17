using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
// Autodesk.Revit.DB와 System.Windows를 같이 쓰면 Grid/Control/Binding/Line/Point 같은 이름이 겹친다 -
// 이 프로젝트의 관례대로 겹치는 것만 별칭으로 구분한다(루트 CLAUDE.md 참고).
using RevitDocument = Autodesk.Revit.DB.Document;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // "룸 경계 ON/OFF" 창. Revit 조회/변경은 전부 RoomBoundingService가 한다(RoomSeparatorWindow와 같은 구조).
    public partial class RoomBoundingWindow : Window
    {
        private readonly RevitDocument _doc;
        private List<RoomBoundingService.CategoryState> _categories;

        // 체크 상태는 화면이 아니라 여기에 둔다 - 적용 후 목록을 다시 읽어도 고른 것이 풀리지 않게 하기 위함.
        private readonly HashSet<int> _checked = new HashSet<int>();

        public RoomBoundingWindow(RevitDocument doc)
        {
            InitializeComponent();
            _doc = doc;
            _categories = RoomBoundingService.Collect(doc);
            BuildList();
            UpdateCounts();
        }

        private void BuildList()
        {
            CategoryListPanel.Children.Clear();
            if (_categories.Count == 0)
            {
                CategoryListPanel.Children.Add(new TextBlock
                {
                    Text = "이 모델에서 룸 경계 속성을 가진 요소를 찾지 못했습니다.",
                    Foreground = Theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8),
                });
                return;
            }

            foreach (RoomBoundingService.CategoryState category in _categories)
                CategoryListPanel.Children.Add(BuildRow(category));
        }

        private UIElement BuildRow(RoomBoundingService.CategoryState category)
        {
            WpfCheckBox check = new WpfCheckBox
            {
                IsChecked = _checked.Contains(category.CategoryId),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            check.Checked += (s, e) => { _checked.Add(category.CategoryId); UpdateCounts(); };
            check.Unchecked += (s, e) => { _checked.Remove(category.CategoryId); UpdateCounts(); };

            StackPanel info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = category.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock
            {
                Text = category.Summary(),
                FontSize = 11,
                // 전부 꺼져 있는 카테고리는 눈에 띄게 - "왜 방이 안 나뉘지?"의 원인인 경우가 많다.
                Foreground = category.OnCount == 0 && category.OffCount > 0 ? Theme.WarningText : Theme.TextSecondary,
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

        private void UpdateCounts()
        {
            int elements = _categories.Where(c => _checked.Contains(c.CategoryId)).Sum(c => c.Total);
            CountText.Text = _categories.Count == 0
                ? ""
                : "선택됨 " + _checked.Count + "개 카테고리 · 요소 " + elements + "개";
            bool any = _checked.Count > 0;
            TurnOnButton.IsEnabled = any;
            TurnOffButton.IsEnabled = any;
            StatusText.Text = any ? "" : "카테고리를 하나 이상 골라주세요.";
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (RoomBoundingService.CategoryState c in _categories) _checked.Add(c.CategoryId);
            BuildList();
            UpdateCounts();
        }

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            _checked.Clear();
            BuildList();
            UpdateCounts();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void TurnOnButton_Click(object sender, RoutedEventArgs e) => Apply(true);

        private void TurnOffButton_Click(object sender, RoutedEventArgs e) => Apply(false);

        private void Apply(bool on)
        {
            if (_checked.Count == 0) return;

            RoomBoundingService.ApplyResult result;
            using (Transaction tx = new Transaction(_doc, on ? "룸 경계 켜기" : "룸 경계 끄기"))
            {
                tx.Start();
                result = RoomBoundingService.Apply(_doc, new HashSet<int>(_checked), on);
                tx.Commit();
            }

            // 창을 닫지 않고 목록을 다시 읽어 바뀐 상태를 그 자리에서 보여준다 - 켜고 끄기를 번갈아 해 보는
            // 도구라, 매번 닫았다 여는 것보다 이 편이 낫다(고른 카테고리는 그대로 유지된다).
            _categories = RoomBoundingService.Collect(_doc);
            BuildList();
            UpdateCounts();

            List<string> lines = new List<string>
            {
                (on ? "룸 경계를 켰습니다" : "룸 경계를 껐습니다") + " - 요소 " + result.Changed + "개를 바꿨습니다.",
            };
            if (result.AlreadySet > 0) lines.Add("이미 그 상태였던 요소 " + result.AlreadySet + "개는 그대로 뒀습니다.");
            if (result.Locked > 0) lines.Add("읽기 전용이라 바꿀 수 없는 요소 " + result.Locked + "개는 건너뛰었습니다.");
            if (result.Failed > 0) lines.Add("바꾸지 못한 요소 " + result.Failed + "개가 있습니다.");

            StatusText.Text = string.Join(" ", lines);
        }
    }
}
