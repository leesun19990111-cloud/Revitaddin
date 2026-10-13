using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WallSplitter
{
    // 현재 모델 전용 NAMER 안에 놓는 순수 값 편집기. Revit 쓰기는 기존 ExternalEvent가 담당한다.
    internal sealed class ProjectInformationPanel : Grid
    {
        private readonly List<UnifiedRow> _rows;
        private readonly Action _changed;
        private readonly TextBox _search = new(), _value = new();
        private readonly StackPanel _items = new();
        private readonly TextBlock _status = new();
        private readonly Grid _header = Columns();
        private readonly List<Grid> _rowGrids = new();
        private List<UnifiedRow> VisibleRows => _rows.Where(r => UnifiedEditLogic.Matches(r.Family, _search.Text, 0)
            || UnifiedEditLogic.Matches(r.Display(r.Working), _search.Text, 0)).ToList();
        internal ProjectInformationPanel(List<UnifiedRow> rows, Action changed)
        {
            _rows = rows; _changed = changed;
            for (int i = 0; i < 5; i++) RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var hint = new TextBlock { Text = "프로젝트 정보 · 항목 이름과 값 검색 · 빈 값은 ‘없음’\n변경할 값에 직접 입력하거나 여러 항목을 체크해 같은 값을 지정하세요. 입력만으로 모델이 바뀌지 않습니다.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4,8,4,8), Foreground = Theme.Accent };
            Put(hint, 0);
            var tools = new WrapPanel(); Put(tools, 1);
            tools.Children.Add(Cell("검색")); _search.Width = 150; tools.Children.Add(_search);
            tools.Children.Add(Button("전체 선택", () => { foreach (var r in VisibleRows.Where(r => r.Editable)) r.Selected = true; Render(); }));
            tools.Children.Add(Button("선택 해제", () => { foreach (var r in _rows) r.Selected = false; Render(); }));
            tools.Children.Add(Cell("지정할 값")); _value.Width = 150; _value.ToolTip = "빈 문자열을 적용하면 선택한 텍스트 값을 지웁니다."; tools.Children.Add(_value);
            tools.Children.Add(Button("선택 값 지정", Stage));
            Add(_header, Cell("선택"), 0); Add(_header, Cell("항목명 / 상태"), 1);
            Add(_header, Cell("현재 값"), 2); Add(_header, Cell("변경할 값"), 3);
            for (int col = 1; col < 3; col++)
            {
                var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
                    ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.CurrentAndNext, Background = Theme.Divider };
                Add(_header, splitter, col); splitter.DragDelta += (_, _) => Guard(SyncWidths);
            }
            _header.Background = Theme.Surface; _header.Margin = new Thickness(0,6,17,0); Put(_header, 2);
            Put(new Border { BorderBrush = Theme.Divider, BorderThickness = new Thickness(1), Background = Brushes.White,
                Child = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Visible } }, 3);
            _status.Margin = new Thickness(4); _status.TextWrapping = TextWrapping.Wrap; Put(_status, 4);
            _search.TextChanged += (_, _) => Guard(() => { var visible = new HashSet<UnifiedRow>(VisibleRows); foreach (var r in _rows.Where(r => !visible.Contains(r))) r.Selected = false; Render(); });
            SizeChanged += (_, _) => Guard(SyncWidths);
            Render();
        }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { _status.Text = ex.GetBaseException().Message; } }
        private Button Button(string text, Action click) { var b = new Button { Content = text, Margin = new Thickness(3) }; b.Click += (_, _) => Guard(click); return b; }
        private static TextBlock Cell(string text) => new() { Text = text, ToolTip = text, Margin = new Thickness(5), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private static Grid Columns()
        {
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 80 });
            return g;
        }
        private void Put(UIElement element, int row) { SetRow(element, row); Children.Add(element); }
        private static void Add(Grid g, UIElement element, int column) { SetColumn(element, column); g.Children.Add(element); }
        private void SyncWidths()
        {
            _header.UpdateLayout();
            foreach (var g in _rowGrids) for (int i = 0; i < 4; i++)
                if (_header.ColumnDefinitions[i].ActualWidth > 0) g.ColumnDefinitions[i].Width = new GridLength(_header.ColumnDefinitions[i].ActualWidth);
        }
        internal void Render()
        {
            _items.Children.Clear(); _rowGrids.Clear();
            foreach (var row in VisibleRows)
            {
                var g = Columns(); _rowGrids.Add(g);
                var check = new CheckBox { IsChecked = row.Selected, IsEnabled = row.Editable, Margin = new Thickness(8) };
                check.Click += (_, _) => Guard(() => { row.Selected = check.IsChecked == true; }); Add(g, check, 0);
                var label = Cell(row.Family + (row.Editable ? "" : " [읽기 전용]")); label.ToolTip = row.Family + "\n" + row.Detail + "\n" + row.ProjectValue!.Identity; Add(g, label, 1);
                Add(g, Cell(row.Display(row.Original)), 2);
                var host = new Grid(); Add(g, host, 3);
                var box = new TextBox { Text = row.Working, IsReadOnly = !row.Editable, ToolTip = row.Detail, Margin = new Thickness(3),
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 75, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var empty = Cell("없음"); empty.IsHitTestVisible = false; empty.Foreground = Theme.TextSecondary; empty.Margin = new Thickness(9,3,3,3);
                host.Children.Add(box); host.Children.Add(empty);
                void Update() { empty.Visibility = box.Text.Length == 0 && !box.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
                    box.Foreground = row.Changed ? Theme.Accent : Theme.TextPrimary; }
                box.TextChanged += (_, _) => Guard(() => { row.Working = box.Text; Update(); _changed(); UpdateStatus(); });
                box.IsKeyboardFocusWithinChanged += (_, _) => Guard(Update); Update();
                _items.Children.Add(new Border { Child = g, BorderBrush = Theme.Border, BorderThickness = new Thickness(0,0,0,1) });
            }
            UpdateStatus();
            SyncWidths();
        }
        private void UpdateStatus() => _status.Text = "표시 " + VisibleRows.Count + "개 · 프로젝트 정보 변경 " + _rows.Count(r => r.Changed) + "개 · ‘최종 적용’ 전까지 모델에 반영되지 않습니다.";
        internal void Stage()
        {
            foreach (var row in VisibleRows.Where(r => r.Selected && r.Editable)) row.Working = _value.Text;
            _changed(); Render();
        }
    }
}
