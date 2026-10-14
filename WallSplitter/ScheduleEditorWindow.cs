using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WallSplitter
{
    internal sealed class ScheduleEditorWindow : Window
    {
        private readonly ScheduleEditorSnapshot _data;
        private readonly ComboBox _field = new();
        private readonly TextBox _range = new(), _value = new();
        private readonly TextBlock _status = new();
        private readonly TextBlock _editorHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) };
        private readonly ComboBox _boolean = new() { Width = 140, ItemsSource = new[] { "예", "아니요" }, SelectedIndex = -1 };
        private readonly StackPanel _referenceEditors = new();
        private readonly Dictionary<string, string> _referenceChoices = new();
        private Button _stage = null!;
        private string _editorField = "";
        private readonly StackPanel _rows = new();
        private readonly Grid _header = Columns();
        private readonly List<Grid> _grids = new();
        private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        private readonly Dictionary<int, CheckBox> _checks = new();
        private readonly HashSet<int> _selected = new();
        private readonly DispatcherTimer _dragTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
        private bool _syncingRange, _dragging, _dragValue;
        private int _anchor, _lastDrag;
        private string _rangeError = "";
        private int _limit = 200;
        internal ScheduleEditorWindow(ScheduleEditorSnapshot data)
        {
            _data = data; Title = "Sunny Tools · Schedule Editor — " + data.Name;
            Width = 1180; Height = 780; MinWidth = 950; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WallSplitter;component/Resources/Theme.xaml", UriKind.Relative) });
            var root = new Grid { Margin = new Thickness(14), Background = Theme.WindowBackground }; Content = root;
            for (int i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Put(root, new TextBlock { Text = data.Name + "\n원래 정렬 순서의 데이터 행만 1부터 번호를 붙입니다. 제목·열 제목·소계·합계·빈 구분행은 제외됩니다.\n행 클릭·드래그 / Shift + 클릭으로 범위 체크·해제합니다. 행 번호 직접 입력도 가능합니다.\n묶인 행은 확인된 요소 전체에 적용합니다. 유형 값 변경은 선택 밖의 같은 유형에도 영향을 줍니다.",
                Foreground = Theme.Accent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) }, 0);
            var inputArea = new StackPanel { Margin = new Thickness(0,0,0,8) }; Put(root, inputArea, 1);
            var tools = new WrapPanel(); inputArea.Children.Add(tools);
            inputArea.Children.Add(_editorHint);
            inputArea.Children.Add(new ScrollViewer { Content = _referenceEditors, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            tools.Children.Add(Cell("필드")); _field.Width = 290; _field.ItemsSource = data.Fields; _field.SelectedIndex = 0; tools.Children.Add(_field);
            tools.Children.Add(Cell("행 (예: 1-10,12)")); _range.Width = 160; tools.Children.Add(_range);
            tools.Children.Add(Cell("지정할 값")); _value.Width = 220; _value.ToolTip = "텍스트는 비우면 삭제 · 숫자는 대상 모델의 프로젝트 단위로 입력"; tools.Children.Add(_value);
            tools.Children.Add(_boolean);
            tools.Children.Add(Button("전체 선택", () => { _range.Text = data.Rows.Count == 0 ? "" : "1-" + data.Rows.Count; }));
            tools.Children.Add(Button("선택 해제", () => _range.Clear()));
            _stage = Button("미리보기에 적용", () =>
            {
                if (_field.SelectedItem is not ScheduleEditorField f) return;
                var numbers = ScheduleEditorLogic.ParseRows(_range.Text, data.Rows.Count);
                if (f.Editor == ScheduleEditorKind.ReadOnly) throw new InvalidOperationException(f.Hint);
                if (f.Editor == ScheduleEditorKind.Reference) ScheduleEditorLogic.StageReferences(data, f, numbers, _referenceChoices);
                else
                {
                    if (f.Editor == ScheduleEditorKind.Boolean && _boolean.SelectedItem == null) throw new InvalidOperationException("예 또는 아니요를 선택하세요.");
                    ScheduleEditorLogic.Stage(data, f, numbers, f.Editor == ScheduleEditorKind.Boolean ? (string)_boolean.SelectedItem : _value.Text);
                }
                Render();
            }); tools.Children.Add(_stage);
            string[] labels = { "선택 / 행", "소속 모델", "요소 수", "현재 값", "변경 후 값 · 미리보기", "상태" };
            for (int i = 0; i < labels.Length; i++) Add(_header, Cell(labels[i]), i);
            for (int i = 1; i < 5; i++)
            {
                var splitter = new GridSplitter { Width = 5, Background = Theme.Divider, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.CurrentAndNext };
                splitter.DragDelta += (_, _) => Guard(Sync); Add(_header, splitter, i);
            }
            _header.Margin = new Thickness(0,0,17,0); _header.Background = Theme.Surface; Put(root, _header, 2);
            _scroll.Content = _rows;
            Put(root, new Border { Background = Brushes.White, BorderBrush = Theme.Divider, BorderThickness = new Thickness(1), Child = _scroll }, 3);
            _status.Margin = new Thickness(0,8,0,8); _status.TextWrapping = TextWrapping.Wrap; Put(root, _status, 4);
            var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; Put(root, footer, 5);
            footer.Children.Add(Button("미리보기 전체 초기화", () =>
            {
                if (MessageBox.Show(this, "누적된 변경을 모두 초기화할까요?", "초기화", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                foreach (var target in data.Rows.SelectMany(r => r.Targets.Values).SelectMany(r => r).Distinct())
                {
                    target.Working = target.Original;
                    if (target.ProjectValue != null) target.ProjectValue.SelectedReference = target.ProjectValue.OriginalReference;
                }
                data.Pending.Clear(); Render();
            }));
            footer.Children.Add(Button("닫기", Close));
            var apply = Button("최종 적용 · 링크 중앙 동기화", () =>
            {
                if (data.Pending.Count == 0) { _status.Text = "먼저 행/필드/값을 지정하고 ‘미리보기에 적용’을 누르세요."; return; }
                DialogResult = true;
            }); apply.Style = (Style)FindResource("PrimaryButtonStyle"); footer.Children.Add(apply);
            _field.SelectionChanged += (_, _) => Guard(Render); _range.TextChanged += (_, _) => Guard(ReadRange);
            _rows.MouseMove += (_, e) => Guard(() =>
            {
                if (!_dragging) return;
                UpdateDrag(e.GetPosition(_scroll), e.LeftButton == MouseButtonState.Pressed); e.Handled = true;
            });
            _rows.MouseLeftButtonUp += (_, _) => Guard(EndDrag);
            _rows.LostMouseCapture += (_, e) => Guard(() => { if (ReferenceEquals(e.OriginalSource, _rows)) EndDrag(); });
            _dragTimer.Tick += (_, _) => Guard(() => UpdateDrag(Mouse.GetPosition(_scroll), Mouse.LeftButton == MouseButtonState.Pressed));
            Deactivated += (_, _) => Guard(EndDrag); Closed += (_, _) => Guard(EndDrag);
            SizeChanged += (_, _) => Guard(Sync); _header.SizeChanged += (_, _) => Guard(Sync); Render();
        }
        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                _dragging = false; _dragTimer.Stop();
                try { if (_rows.IsMouseCaptured) _rows.ReleaseMouseCapture(); } catch { }
                _status.Text = "적용하지 않았습니다: " + ex.GetBaseException().Message;
            }
        }
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
            EndDrag();
            if (_field.SelectedItem is not ScheduleEditorField field) return;
            ConfigureEditor(field);
            double offset = _scroll.VerticalOffset;
            _rows.Children.Clear(); _grids.Clear(); _checks.Clear();
            foreach (var row in _data.Rows.Take(_limit))
            {
                var grid = Columns(); _grids.Add(grid); grid.Tag = row.Number;
                var check = new CheckBox { Content = row.Number + "행", Margin = new Thickness(5), IsHitTestVisible = false };
                _checks.Add(row.Number, check); Add(grid, check, 0);
                grid.MouseLeftButtonDown += (_, e) => Guard(() =>
                {
                    check.Focus(); BeginDrag(row.Number, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                    if (_rows.CaptureMouse()) _dragTimer.Start(); else EndDrag();
                    e.Handled = true;
                });
                // 체크박스의 키보드/접근성 클릭도 같은 선택 규칙을 사용한다.
                check.Click += (_, _) => Guard(() => { BeginDrag(row.Number, (Keyboard.Modifiers & ModifierKeys.Shift) != 0); EndDrag(); });
                Add(grid, Cell(row.Models), 1); Add(grid, Cell(row.ElementCount.ToString()), 2);
                string original = row.Cells.TryGetValue(field.Id, out var cell) ? cell : "";
                Add(grid, Cell(original.Length == 0 ? "없음" : original), 3);
                var targets = row.Targets.TryGetValue(field.Id, out var list) ? list : new List<UnifiedRow>();
                bool changed = targets.Any(t => t.Changed);
                var values = targets.Select(t => t.Working).Distinct().ToList();
                string preview = changed ? values.Count == 1 ? (values[0].Length == 0 ? "없음" : values[0]) : "<다양함>" : original.Length == 0 ? "없음" : original;
                var after = Cell(preview); after.Foreground = changed ? Theme.Accent : Theme.TextSecondary; Add(grid, after, 4);
                string reason = row.Reason.Length > 0 ? row.Reason : field.Reason.Length > 0 ? field.Reason : row.Errors.TryGetValue(field.Id, out var error) ? error :
                    targets.Any(t => !t.Editable) ? string.Join(" / ", targets.Where(t => !t.Editable).Select(t => t.Source.BlockReason + t.ProjectValue?.ReadOnlyReason).Distinct()) :
                    field.IsType ? "유형 전체 변경" : row.ElementCount > 1 ? "묶인 요소 모두 변경" : "편집 가능";
                Add(grid, Cell(reason), 5);
                _rows.Children.Add(new Border { Child = grid, BorderBrush = Theme.Border, BorderThickness = new Thickness(0,0,0,1) });
            }
            if (_data.Rows.Count > _limit) _rows.Children.Add(Button("200행 더 보기", () => { _limit += 200; Render(); }));
            RefreshSelection(); Sync(); _scroll.UpdateLayout(); _scroll.ScrollToVerticalOffset(offset);
        }
        private void ReadRange()
        {
            if (_syncingRange) return;
            EndDrag(); _anchor = 0; _selected.Clear(); _rangeError = "";
            try
            {
                if (_range.Text.Trim().Length > 0) _selected.UnionWith(ScheduleEditorLogic.ParseRows(_range.Text, _data.Rows.Count));
            }
            catch (Exception ex) { _rangeError = ex.Message; }
            RefreshSelection();
        }
        private void ConfigureEditor(ScheduleEditorField field)
        {
            if (_editorField == field.Id) return;
            _editorField = field.Id; _value.Clear(); _boolean.SelectedIndex = -1; _referenceChoices.Clear(); _referenceEditors.Children.Clear();
            _editorHint.Text = field.Hint;
            _value.Visibility = field.Editor == ScheduleEditorKind.Text || field.Editor == ScheduleEditorKind.Integer || field.Editor == ScheduleEditorKind.Number ? Visibility.Visible : Visibility.Collapsed;
            _boolean.Visibility = field.Editor == ScheduleEditorKind.Boolean ? Visibility.Visible : Visibility.Collapsed;
            _stage.IsEnabled = field.Editor != ScheduleEditorKind.ReadOnly;
            if (field.Editor != ScheduleEditorKind.Reference) return;
            var targets = _data.Rows.SelectMany(r => r.Targets.TryGetValue(field.Id, out var list) ? list : new List<UnifiedRow>()).Where(t => t.Editable).Distinct().ToList();
            foreach (var model in targets.GroupBy(t => t.Source.Key))
            {
                var line = new WrapPanel(); _referenceEditors.Children.Add(line);
                line.Children.Add(Cell(model.First().Source.Name));
                line.Children.Add(new TextBlock { Text = "이름 검색", Margin = new Thickness(5), VerticalAlignment = VerticalAlignment.Center });
                var search = new TextBox { Width = 160, Margin = new Thickness(3), ToolTip = "이름 검색 · 검색만으로 지정하지 않습니다." }; line.Children.Add(search);
                var common = model.First().ProjectValue!.ReferenceChoices.Where(p => model.All(t => t.ProjectValue!.ReferenceChoices.ContainsKey(p.Key)))
                    .OrderBy(p => p.Value).ToList();
                var combo = new ComboBox { Width = 340, Margin = new Thickness(3), DisplayMemberPath = "Value", SelectedValuePath = "Key", ItemsSource = common }; line.Children.Add(combo);
                combo.SelectionChanged += (_, _) => Guard(() =>
                {
                    if (combo.SelectedValue is string id) _referenceChoices[model.Key] = id; else _referenceChoices.Remove(model.Key);
                });
                search.TextChanged += (_, _) => Guard(() =>
                {
                    string? old = combo.SelectedValue as string;
                    combo.ItemsSource = common.Where(p => p.Value.IndexOf(search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                    combo.SelectedValue = old;
                });
            }
        }
        private void RefreshSelection()
        {
            // 드래그 중 행을 다시 만들면 캡처·스크롤·키보드 포커스가 끊긴다.
            foreach (var grid in _grids)
            {
                int number = (int)grid.Tag; bool selected = _selected.Contains(number);
                _checks[number].IsChecked = selected;
                grid.Background = selected ? Theme.SelectionHighlight : Brushes.Transparent;
            }
            _status.Text = _rangeError.Length > 0 ? _rangeError : "총 " + _data.Rows.Count + "행 · 선택 " + _selected.Count + "행 · 누적 변경 " + _data.Pending.Count + "개 매개변수 / " + _data.Pending.Values.Select(t => t.Source.Key).Distinct().Count() + "개 모델\n동일 요소·유형은 한 번만 변경합니다. 다른 행에서도 같은 유형/매개변수를 선택하면 마지막 지정값이 함께 표시됩니다.";
        }
        private void BeginDrag(int number, bool shift)
        {
            if (number < 1 || number > _data.Rows.Count) return;
            _dragValue = !_selected.Contains(number); _dragging = true;
            SetRange(shift && _anchor > 0 ? _anchor : number, number);
            _anchor = number; _lastDrag = number; SelectionChanged();
        }
        private void ContinueDrag(int number)
        {
            if (!_dragging || number == _lastDrag || number < 1 || number > _data.Rows.Count) return;
            SetRange(_lastDrag, number); _lastDrag = number; SelectionChanged();
        }
        private void SetRange(int first, int last)
        {
            // 사이를 빠르게 건너뛰거나 되짚어도 시작 시 정한 체크/해제 상태를 유지한다.
            for (int n = Math.Min(first, last); n <= Math.Max(first, last); n++)
                if (_dragValue) _selected.Add(n); else _selected.Remove(n);
        }
        private void SelectionChanged()
        {
            _rangeError = ""; _syncingRange = true;
            try { _range.Text = FormatRange(_selected); }
            finally { _syncingRange = false; }
            RefreshSelection();
        }
        private static string FormatRange(IEnumerable<int> numbers)
        {
            var sorted = numbers.OrderBy(n => n).ToList(); var parts = new List<string>();
            for (int i = 0; i < sorted.Count; i++)
            {
                int first = sorted[i], last = first;
                while (i + 1 < sorted.Count && sorted[i + 1] == last + 1) last = sorted[++i];
                parts.Add(first == last ? first.ToString() : first + "-" + last);
            }
            return string.Join(",", parts);
        }
        private void UpdateDrag(Point point, bool pressed)
        {
            if (!_dragging) return;
            if (!pressed) { EndDrag(); return; }
            if (_scroll.ViewportHeight <= 0 || point.X < 0 || point.X >= _rows.ActualWidth) return;
            if (point.Y < 24) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 22);
            else if (point.Y > _scroll.ViewportHeight - 24) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 22);
            _scroll.UpdateLayout();
            // 화면 밖으로 끌어도 보이는 끝 행까지만 선택하고 타이머로 스크롤을 이어 간다.
            point.Y = Math.Max(0, Math.Min(_scroll.ViewportHeight - 1, point.Y));
            var hit = _rows.InputHitTest(_scroll.TranslatePoint(point, _rows)) as DependencyObject;
            while (hit != null && !ReferenceEquals(hit, _rows))
            {
                if (hit is Grid grid && grid.Tag is int number) { ContinueDrag(number); break; }
                hit = VisualTreeHelper.GetParent(hit);
            }
        }
        private void EndDrag()
        {
            _dragging = false; _lastDrag = 0; _dragTimer.Stop();
            if (_rows.IsMouseCaptured) _rows.ReleaseMouseCapture();
        }
    }
}
