using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WallSplitter
{
    internal sealed class ScheduleEditorWindow : Window
    {
        private readonly ScheduleEditorSnapshot _data;
        private readonly ComboBox _field = new();
        private readonly TextBox _range = new(), _value = new();
        private readonly TextBlock _status = new();
        private readonly StackPanel _rows = new();
        private readonly Grid _header = Columns();
        private readonly List<Grid> _grids = new();
        private int _limit = 200;
        internal ScheduleEditorWindow(ScheduleEditorSnapshot data)
        {
            _data = data; Title = "Sunny Tools · Schedule Editor — " + data.Name;
            Width = 1180; Height = 780; MinWidth = 950; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WallSplitter;component/Resources/Theme.xaml", UriKind.Relative) });
            var root = new Grid { Margin = new Thickness(14), Background = Theme.WindowBackground }; Content = root;
            for (int i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Put(root, new TextBlock { Text = data.Name + "\n원래 정렬 순서의 데이터 행만 1부터 번호를 붙입니다. 제목·열 제목·소계·합계·빈 구분행은 제외됩니다.\n묶인 행은 확인된 요소 전체에 적용합니다. 유형 값 변경은 선택 밖의 같은 유형에도 영향을 줍니다.",
                Foreground = Theme.Accent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) }, 0);
            var tools = new WrapPanel { Margin = new Thickness(0,0,0,8) }; Put(root, tools, 1);
            tools.Children.Add(Cell("필드")); _field.Width = 290; _field.ItemsSource = data.Fields; _field.SelectedIndex = 0; tools.Children.Add(_field);
            tools.Children.Add(Cell("행 (예: 1-10,12)")); _range.Width = 160; tools.Children.Add(_range);
            tools.Children.Add(Cell("지정할 값")); _value.Width = 220; _value.ToolTip = "텍스트는 비우면 삭제 · 숫자는 대상 모델의 프로젝트 단위로 입력"; tools.Children.Add(_value);
            tools.Children.Add(Button("선택 행 모두", () => { _range.Text = "1-" + data.Rows.Count; }));
            tools.Children.Add(Button("선택 해제", () => _range.Clear()));
            tools.Children.Add(Button("미리보기에 적용", () =>
            {
                if (_field.SelectedItem is not ScheduleEditorField f) return;
                ScheduleEditorLogic.Stage(data, f, ScheduleEditorLogic.ParseRows(_range.Text, data.Rows.Count), _value.Text); Render();
            }));
            string[] labels = { "선택 / 행", "소속 모델", "요소 수", "현재 값", "변경 후 값 · 미리보기", "상태" };
            for (int i = 0; i < labels.Length; i++) Add(_header, Cell(labels[i]), i);
            for (int i = 1; i < 5; i++)
            {
                var splitter = new GridSplitter { Width = 5, Background = Theme.Divider, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.CurrentAndNext };
                splitter.DragDelta += (_, _) => Guard(Sync); Add(_header, splitter, i);
            }
            _header.Margin = new Thickness(0,0,17,0); _header.Background = Theme.Surface; Put(root, _header, 2);
            Put(root, new Border { Background = Brushes.White, BorderBrush = Theme.Divider, BorderThickness = new Thickness(1),
                Child = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }, 3);
            _status.Margin = new Thickness(0,8,0,8); _status.TextWrapping = TextWrapping.Wrap; Put(root, _status, 4);
            var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; Put(root, footer, 5);
            footer.Children.Add(Button("미리보기 전체 초기화", () =>
            {
                if (MessageBox.Show(this, "누적된 변경을 모두 초기화할까요?", "초기화", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                foreach (var target in data.Rows.SelectMany(r => r.Targets.Values).SelectMany(r => r).Distinct()) target.Working = target.Original;
                data.Pending.Clear(); Render();
            }));
            footer.Children.Add(Button("닫기", Close));
            var apply = Button("최종 적용 · 링크 중앙 동기화", () =>
            {
                if (data.Pending.Count == 0) { _status.Text = "먼저 행/필드/값을 지정하고 ‘미리보기에 적용’을 누르세요."; return; }
                DialogResult = true;
            }); apply.Style = (Style)FindResource("PrimaryButtonStyle"); footer.Children.Add(apply);
            _field.SelectionChanged += (_, _) => Guard(Render); _range.TextChanged += (_, _) => Guard(Render);
            SizeChanged += (_, _) => Guard(Sync); _header.SizeChanged += (_, _) => Guard(Sync); Render();
        }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { _status.Text = "적용하지 않았습니다: " + ex.GetBaseException().Message; } }
        private Button Button(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(3) }; b.Click += (_, _) => Guard(action); return b; }
        private static TextBlock Cell(string text) => new() { Text = text, ToolTip = text, Margin = new Thickness(5), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private static void Put(Grid grid, UIElement e, int row) { Grid.SetRow(e, row); grid.Children.Add(e); }
        private static void Add(Grid grid, UIElement e, int col) { Grid.SetColumn(e, col); grid.Children.Add(e); }
        private static Grid Columns()
        {
            var grid = new Grid();
            foreach (double width in new[] { 85d, 1.1, 70, 1.2, 1.2, 1.3 }) grid.ColumnDefinitions.Add(new ColumnDefinition {
                MinWidth = width > 10 ? width : 75, Width = new GridLength(width, width > 10 ? GridUnitType.Pixel : GridUnitType.Star) });
            return grid;
        }
        private void Sync()
        {
            _header.UpdateLayout();
            foreach (var grid in _grids) for (int i = 0; i < 6; i++) if (_header.ColumnDefinitions[i].ActualWidth > 0) grid.ColumnDefinitions[i].Width = new GridLength(_header.ColumnDefinitions[i].ActualWidth);
        }
        private void Render()
        {
            if (_field.SelectedItem is not ScheduleEditorField field) return;
            HashSet<int> selected = new(); string rangeError = "";
            if (_range.Text.Trim().Length > 0) try { selected = ScheduleEditorLogic.ParseRows(_range.Text, _data.Rows.Count); } catch (Exception ex) { rangeError = ex.Message; }
            _rows.Children.Clear(); _grids.Clear();
            foreach (var row in _data.Rows.Take(_limit))
            {
                var grid = Columns(); _grids.Add(grid); grid.Background = selected.Contains(row.Number) ? Theme.SelectionHighlight : Brushes.Transparent;
                var check = new CheckBox { Content = row.Number + "행", IsChecked = selected.Contains(row.Number), Margin = new Thickness(5) };
                check.Click += (_, _) => Guard(() => { if (!selected.Add(row.Number)) selected.Remove(row.Number); _range.Text = string.Join(",", selected.OrderBy(n => n)); }); Add(grid, check, 0);
                Add(grid, Cell(row.Models), 1); Add(grid, Cell(row.ElementCount.ToString()), 2);
                string original = row.Cells.TryGetValue(field.Id, out var cell) ? cell : "";
                Add(grid, Cell(original.Length == 0 ? "없음" : original), 3);
                var targets = row.Targets.TryGetValue(field.Id, out var list) ? list : new List<UnifiedRow>();
                bool changed = targets.Any(t => t.Changed);
                var values = targets.Select(t => t.Working).Distinct().ToList();
                string preview = changed ? values.Count == 1 ? (values[0].Length == 0 ? "없음" : values[0]) : "<다양함>" : original.Length == 0 ? "없음" : original;
                var after = Cell(preview); after.Foreground = changed ? Theme.Accent : Theme.TextSecondary; Add(grid, after, 4);
                string reason = field.Reason.Length > 0 ? field.Reason : row.Errors.TryGetValue(field.Id, out var error) ? error :
                    targets.Any(t => !t.Editable) ? string.Join(" / ", targets.Where(t => !t.Editable).Select(t => t.Source.BlockReason + t.ProjectValue?.ReadOnlyReason).Distinct()) :
                    field.IsType ? "유형 전체 변경" : row.ElementCount > 1 ? "묶인 요소 모두 변경" : "편집 가능";
                Add(grid, Cell(reason), 5);
                _rows.Children.Add(new Border { Child = grid, BorderBrush = Theme.Border, BorderThickness = new Thickness(0,0,0,1) });
            }
            if (_data.Rows.Count > _limit) _rows.Children.Add(Button("200행 더 보기", () => { _limit += 200; Render(); }));
            _status.Text = rangeError.Length > 0 ? rangeError : "총 " + _data.Rows.Count + "행 · 선택 " + selected.Count + "행 · 누적 변경 " + _data.Pending.Count + "개 매개변수 / " + _data.Pending.Values.Select(t => t.Source.Key).Distinct().Count() + "개 모델\n동일 요소·유형은 한 번만 변경합니다. 다른 행에서도 같은 유형/매개변수를 선택하면 마지막 지정값이 함께 표시됩니다.";
            Sync();
        }
    }
}
