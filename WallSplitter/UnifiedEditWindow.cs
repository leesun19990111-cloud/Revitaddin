using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Grid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // 링크 해제 전에 읽은 순수 값만 표시한다. 창 안에서는 Revit API를 호출하지 않는다.
    internal sealed class UnifiedEditWindow : Window
    {
        private readonly List<UnifiedSource> _sources;
        private readonly bool _materials;
        private readonly ComboBox _model = new(), _category = new(), _filterMode = new(), _mode = new();
        private readonly TextBox _search = new(), _first = new(), _second = new();
        private readonly TextBlock _status = new(), _hint = new();
        private readonly StackPanel _items = new(), _materialChoices = new();
        private readonly CheckBox _merge = new() { Content = "같은 이름의 재료 병합", IsChecked = false };
        private readonly Dictionary<string, string> _choices = new();
        private List<UnifiedRow> _filtered = new(), _display = new();
        private int _limit = 200;
        private bool _ready;
        internal bool MergeMaterials => _merge.IsChecked == true;
        internal bool CurrentOnly { get; private set; }

        internal UnifiedEditWindow(List<UnifiedSource> sources, bool materials)
        {
            _sources = sources; _materials = materials;
            Title = materials ? "Sunny Tools · 재료 지정 — 모든 모델" : "Sunny Tools · NAMER — 모든 모델";
            Width = 1240; Height = 800; MinWidth = 1040; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WallSplitter;component/Resources/Theme.xaml", UriKind.Relative) });
            Background = Theme.WindowBackground;
            var root = new Grid { Margin = new Thickness(14), Background = Theme.WindowBackground };
            for (int i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Content = root;
            var info = new TextBlock { Text = "현재 모델 + 링크 모델 통합 편집 · 같은 링크의 반복 배치는 한 번만 표시\n창 안의 변경은 미리보기입니다. 최종 적용 후 링크 중앙모델에 반영하며, 편집 중 다른 Revit 작업은 잠시 제한됩니다.",
                TextWrapping = TextWrapping.Wrap, Foreground = Theme.Accent, Margin = new Thickness(0,0,0,10) };
            root.Children.Add(info);
            var filters = new WrapPanel(); Put(root, filters, 1);
            filters.Children.Add(Label("모델"));
            _model.Width = 240; _model.Items.Add("전체 모델"); foreach (var source in sources) _model.Items.Add(source);
            _model.SelectedIndex = 0; filters.Children.Add(_model);
            _category.Width = 135;
            foreach (string label in materials ? new[] { "유형/패밀리", "현재 재료", "재료 없음" }
                : new[] { "뷰", "시트", "패밀리", "유형", "범례", "일람표", "재료", "뷰 템플릿" }) _category.Items.Add(label);
            _category.SelectedIndex = materials ? 0 : 2; filters.Children.Add(_category);
            _search.Width = 230; _search.ToolTip = "모든 모델의 검색 결과를 함께 표시합니다."; filters.Children.Add(_search);
            _filterMode.Width = 125;
            foreach (string label in new[] { "포함됨", "포함하지 않음", "일치함", "일치하지 않음", "시작함", "끝남" }) _filterMode.Items.Add(label);
            _filterMode.SelectedIndex = 0; filters.Children.Add(_filterMode);
            filters.Children.Add(Button("전체 선택", () => { foreach (var row in _display.Where(r => r.Source.Editable)) row.Selected = true; Render(); }));
            filters.Children.Add(Button("전체 해제", () => { foreach (var row in _filtered) row.Selected = false; Render(); }));

            var editor = new StackPanel { Margin = new Thickness(0,8,0,8) }; Put(root, editor, 2);
            if (!materials)
            {
                var line = new WrapPanel(); editor.Children.Add(line);
                _mode.Width = 180;
                foreach (string label in new[] { "문자열 치환", "위치에 삽입", "구분자 기준 자리바꾸기", "구간 삭제" }) _mode.Items.Add(label);
                _mode.SelectedIndex = 0; line.Children.Add(_mode);
                _first.Width = 210; _second.Width = 210; line.Children.Add(_first); line.Children.Add(_second);
                line.Children.Add(Button("선택 항목에 적용 (창 안)", Stage));
                line.Children.Add(_merge); editor.Children.Add(_hint); UpdateHint();
                _mode.SelectionChanged += (_, _) => Guard(UpdateHint);
            }
            else
            {
                editor.Children.Add(new TextBlock { Text = "모델마다 지정할 재료를 직접 선택하세요. 같은 이름이어도 다른 모델의 재료를 자동 연결하지 않습니다.", TextWrapping = TextWrapping.Wrap });
                var scroll = new ScrollViewer { Content = _materialChoices, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                editor.Children.Add(scroll);
                foreach (var source in sources.Where(s => s.Editable && s.Materials.Count > 0))
                {
                    var line = new WrapPanel(); _materialChoices.Children.Add(line);
                    line.Children.Add(new TextBlock { Text = source.Name, Width = 320, TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = source.Path, VerticalAlignment = VerticalAlignment.Center });
                    var combo = new ComboBox { Width = 540, IsEditable = true, IsTextSearchEnabled = true,
                        ItemsSource = source.Materials.OrderBy(p => p.Value).Select(p => new MaterialChoice(p.Key, p.Value)).ToList() };
                    combo.SelectionChanged += (_, _) => Guard(() =>
                    {
                        if (combo.SelectedItem is MaterialChoice choice) _choices[source.Key] = choice.Id;
                        else _choices.Remove(source.Key);
                    });
                    // 자유 입력은 선택으로 간주하지 않는다. 실제 항목과 정확히 일치할 때만 적용한다.
                    combo.LostKeyboardFocus += (_, _) => Guard(() =>
                    {
                        if (combo.SelectedItem is not MaterialChoice choice || combo.Text != choice.Name) _choices.Remove(source.Key);
                    });
                    line.Children.Add(combo);
                }
                editor.Children.Add(Button("선택 항목에 재료 적용 (창 안)", Stage));
            }
            var header = Columns();
            Add(header, Label("선택"), 0); Add(header, Label("소속 모델"), 1); Add(header, Label("패밀리 / 대상"), 2);
            Add(header, Label(materials ? "현재 재료" : "기존 이름"), 3);
            Add(header, Label(materials ? "변경 후 재료" : "변경 후 이름 (직접 입력 가능)"), 4);
            Put(root, header, 3);
            Put(root, new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }, 4);
            _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(0,8,0,8); Put(root, _status, 5);
            var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; Put(root, footer, 6);
            footer.Children.Add(Button("모델 상태 / 제외 사유", () => MessageBox.Show(this, string.Join("\n\n", sources.Select(s =>
                s.Name + "\n" + s.Path + "\n" + (s.Editable ? "편집 가능 (적용 시 중앙 연결/소유권 재검사)" : s.BlockReason))), "모델 상태")));
            footer.Children.Add(Button("현재 모델 전용 도구", () =>
            {
                if (sources.SelectMany(s => s.Rows).Any(r => r.Changed) && MessageBox.Show(this,
                    "통합 창의 미적용 변경을 버리고 현재 모델 전용 도구로 이동할까요?", "작업 확인", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                CurrentOnly = true; DialogResult = false;
            }));
            footer.Children.Add(Button("변경 초기화", () =>
            {
                if (MessageBox.Show(this, "모든 모델의 창 안 변경을 초기화할까요?", "초기화", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                foreach (var row in sources.SelectMany(s => s.Rows)) { row.Working = materials ? row.Working : row.Original; row.NewMaterial = ""; }
                Refresh();
            }));
            footer.Children.Add(Button("닫기", Close));
            var apply = Button("최종 적용 · 모든 모델", () =>
            {
                Keyboard.ClearFocus();
                var changed = sources.SelectMany(s => s.Rows).Where(r => r.Changed).ToList();
                if (changed.Count == 0) { _status.Text = "변경된 항목이 없습니다."; return; }
                if (changed.Any(r => !r.Source.Editable || (!_materials && string.IsNullOrWhiteSpace(r.Working))))
                { _status.Text = "변경할 수 없는 항목 또는 빈 이름을 확인해 주세요."; return; }
                DialogResult = true;
            });
            apply.Style = (Style)FindResource("PrimaryButtonStyle"); footer.Children.Add(apply);
            _model.SelectionChanged += (_, _) => Guard(Refresh);
            _category.SelectionChanged += (_, _) => Guard(Refresh);
            _filterMode.SelectionChanged += (_, _) => Guard(Refresh);
            _search.TextChanged += (_, _) => Guard(Refresh);
            _ready = true; Refresh();
        }

        private sealed class MaterialChoice
        {
            internal string Id, Name;
            internal MaterialChoice(string id, string name) { Id = id; Name = name; }
            public override string ToString() => Name;
        }
        private void Guard(Action action)
        {
            try { action(); } catch (Exception ex) { _status.Text = "처리하지 못했습니다: " + ex.GetBaseException().Message; }
        }
        private Button Button(string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(9,5,9,5) };
            button.Click += (_, _) => Guard(action); return button;
        }
        private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(4,5,4,5), VerticalAlignment = VerticalAlignment.Center };
        private static void Put(Grid grid, UIElement item, int row) { Grid.SetRow(item, row); grid.Children.Add(item); }
        private static void Add(Grid grid, UIElement item, int column) { Grid.SetColumn(item, column); grid.Children.Add(item); }
        private static Grid Columns()
        {
            var grid = new Grid { MinWidth = 960 };
            foreach (double size in new[] { 74d, 1.1, 1.2, 1.25, 1.25 })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = size > 10 ? new GridLength(size) : new GridLength(size, GridUnitType.Star) });
            return grid;
        }
        private void UpdateHint()
        {
            _hint.Text = new[] { "첫 칸: 찾을 문자열 / 둘째 칸: 바꿀 문자열", "첫 칸: 삽입 위치 (1부터) / 둘째 칸: 넣을 문자열",
                "첫 칸: 구분자 (첫 번째 구분자 기준) / 둘째 칸: 사용 안 함", "첫 칸: 시작 위치 / 둘째 칸: 끝 위치 (1부터)" }[Math.Max(0,_mode.SelectedIndex)];
            _second.IsEnabled = _mode.SelectedIndex != 2;
        }
        private void Refresh()
        {
            if (!_ready) return;
            _limit = 200;
            var all = _sources.SelectMany(s => s.Rows).ToList();
            var categoryRows = all.Where(r => _materials || r.Category == _category.SelectedIndex).ToList();
            string query = _search.Text.Trim(); int mode = _filterMode.SelectedIndex;
            var parents = categoryRows.Where(r => (_model.SelectedItem is not UnifiedSource source || r.Source == source) &&
                (_materials
                    ? (_category.SelectedIndex == 1 ? UnifiedEditLogic.MatchesMaterials(r, query, mode)
                        : _category.SelectedIndex == 2 ? r.NewMaterial.Length == 0 && r.OriginalMaterials.Values.Any(v => v.Length == 0)
                        : MatchName(r.Family, r.Working, query, mode))
                    : UnifiedEditLogic.Matches(r.Working, query, mode)))
                .OrderBy(r => r.Source.IsHost ? 0 : 1).ThenBy(r => r.Source.Name).ThenBy(r => r.Family).ThenBy(r => r.Working).ToList();
            _filtered = new List<UnifiedRow>(); _display = new List<UnifiedRow>();
            foreach (var row in parents)
            {
                _filtered.Add(row); _display.Add(row);
                if (!_materials && row.IsFamily)
                {
                    var children = row.Source.Rows.Where(r => r.ParentId == row.UniqueId).OrderBy(r => r.Working).ToList();
                    _filtered.AddRange(children); if (row.Expanded) _display.AddRange(children);
                }
            }
            var keep = new HashSet<UnifiedRow>(_filtered);
            foreach (var row in categoryRows.Concat(!_materials && _category.SelectedIndex == 2 ? all.Where(r => r.ParentId.Length > 0) : Enumerable.Empty<UnifiedRow>()))
                if (!keep.Contains(row)) row.Selected = false;
            Render();
        }
        private static bool MatchName(string family, string name, string query, int mode) =>
            mode == 1 || mode == 3 ? UnifiedEditLogic.Matches(family, query, mode) && UnifiedEditLogic.Matches(name, query, mode)
            : UnifiedEditLogic.Matches(family, query, mode) || UnifiedEditLogic.Matches(name, query, mode);

        private void Render()
        {
            _items.Children.Clear();
            foreach (var row in _display.Take(_limit))
            {
                var grid = Columns(); grid.Background = row.Selected ? Theme.SelectionHighlight : Brushes.Transparent;
                grid.ToolTip = row.Source.Path + (row.Source.Editable ? "" : "\n" + row.Source.BlockReason);
                var selection = new StackPanel { Orientation = Orientation.Horizontal };
                var check = new CheckBox { IsChecked = row.Selected, IsEnabled = row.Source.Editable, Margin = new Thickness(5) };
                check.Click += (_, _) => Guard(() => { row.Selected = check.IsChecked == true; Render(); });
                selection.Children.Add(check);
                if (!_materials && row.IsFamily)
                {
                    var expand = Button(row.Expanded ? "▾" : "▸", () => { row.Expanded = !row.Expanded; Refresh(); });
                    expand.Padding = new Thickness(2); selection.Children.Add(expand);
                }
                Add(grid, selection, 0);
                Add(grid, Cell(row.Source.Name), 1);
                Add(grid, Cell(_materials ? row.Family + " / " + row.Working + "\n" + row.Detail : row.Family), 2);
                Add(grid, Cell(row.Original), 3);
                if (_materials) Add(grid, Cell(row.Value), 4);
                else
                {
                    var value = new TextBox { Text = row.Working, IsReadOnly = !row.Source.Editable, Margin = new Thickness(3),
                        Foreground = row.Changed ? Theme.Accent : Theme.TextPrimary };
                    value.LostKeyboardFocus += (_, _) => Guard(() => { row.Working = value.Text; UpdateStatus(); });
                    value.KeyDown += (_, e) => Guard(() =>
                    {
                        if (e.Key == Key.Enter) { row.Working = value.Text; Keyboard.ClearFocus(); UpdateStatus(); e.Handled = true; }
                        else if (e.Key == Key.Escape) { value.Text = row.Working; Keyboard.ClearFocus(); e.Handled = true; }
                    });
                    Add(grid, value, 4);
                }
                _items.Children.Add(new Border { Child = grid, BorderBrush = Theme.Border, BorderThickness = new Thickness(0,0,0,1) });
                if (!_materials && row.IsFamily && row.Expanded)
                {
                    var children = row.Source.Rows.Where(r => r.ParentId == row.UniqueId).ToList();
                    var childCheck = new CheckBox { Content = "하위 유형만 전체 선택 (" + children.Count(r => r.Selected) + "/" + children.Count + ")",
                        Margin = new Thickness(80,4,0,4), IsEnabled = row.Source.Editable && children.Count > 0,
                        IsThreeState = true, IsChecked = children.All(r => r.Selected) ? true : children.Any(r => r.Selected) ? (bool?)null : false };
                    childCheck.Click += (_, _) => Guard(() => { bool select = !children.All(r => r.Selected); foreach (var child in children) child.Selected = select; Render(); });
                    _items.Children.Add(childCheck);
                }
            }
            if (_display.Count > _limit) _items.Children.Add(Button("더 보기 (" + (_display.Count - _limit) + "개)", () => { _limit += 200; Render(); }));
            UpdateStatus();
        }
        private static TextBlock Cell(string value) => new() { Text = value, ToolTip = value, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(4,6,4,6), VerticalAlignment = VerticalAlignment.Center };
        private void UpdateStatus()
        {
            var changed = _sources.SelectMany(s => s.Rows).Where(r => r.Changed).ToList();
            _status.Text = _sources.Count + "개 모델 · 표시 " + Math.Min(_limit,_display.Count) + "/" + _display.Count +
                "개 · 선택 " + _filtered.Count(r => r.Selected) + "개 (접힌 유형 포함) · 누적 변경 " + changed.Count +
                "개 / " + changed.Select(r => r.Source.Key).Distinct().Count() + "개 모델\n" +
                (_sources.Any(s => !s.Editable) ? "편집 제외 " + _sources.Count(s => !s.Editable) + "개 모델 — ‘모델 상태 / 제외 사유’에서 확인 · " : "") +
                "전체 선택은 펼친 검색 결과, 전체 해제는 접힌 유형까지 적용됩니다.";
        }
        private void Stage()
        {
            Keyboard.ClearFocus();
            if (_materials)
            {
                if (!UnifiedEditLogic.StageMaterials(_filtered, _choices, out string error)) { _status.Text = error; return; }
            }
            else
            {
                foreach (var row in _filtered.Where(r => r.Selected && r.Source.Editable))
                    row.Working = UnifiedEditLogic.Rename(row.Working, _mode.SelectedIndex, _first.Text, _second.Text);
                _first.Clear(); _second.Clear();
            }
            foreach (var row in _filtered) row.Selected = false;
            Render();
        }
    }
}
