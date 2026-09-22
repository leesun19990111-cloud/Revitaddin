using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
// Autodesk.Revit.DB와 System.Windows를 같이 쓰면 Grid/Binding/Control 같은 이름이 겹친다 -
// 이 프로젝트의 관례대로 겹치는 것만 별칭으로 구분한다(루트 CLAUDE.md 참고).
using RevitDocument = Autodesk.Revit.DB.Document;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfGrid = System.Windows.Controls.Grid;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace WallSplitter
{
    // "자동 결합" 설정 창. Revit 조회/계산은 전부 ParamCombineEngine이 하고, 여기서는 규칙을 편집하고
    // 지금 모델에 적용하면 어떻게 되는지를 보여주기만 한다(RoomBoundingWindow와 같은 구조).
    public partial class ParamCombineWindow : Window
    {
        private const int PreviewRowLimit = 40;

        private readonly RevitDocument _doc;

        // 창 안에서는 항상 사본을 고친다 - "닫기"로 나가면 저장된 설정은 건드리지 않은 것이 되어야 한다.
        private ParamCombineSettings _settings;
        private CombineRule? _rule;

        private List<KeyValuePair<int, string>> _categories = new List<KeyValuePair<int, string>>();
        private List<string> _parameterNames = new List<string>();

        // 코드로 컨트롤을 채우는 동안 발생하는 SelectionChanged/TextChanged를 "사용자 입력"으로 오인하지 않기 위한 표시.
        private bool _loading;
        private bool _dirty;

        public ParamCombineWindow(RevitDocument doc)
        {
            InitializeComponent();
            _doc = doc;
            _settings = ParamCombineSettings.Current.Clone();
            if (_settings.Rules.Count == 0) _settings.Rules.Add(CombineRule.DefaultRoomNumberRule());

            _categories = ParamCombineEngine.BindableCategories(doc);

            _loading = true;
            AutoUpdateCheck.IsChecked = _settings.AutoUpdate;
            _loading = false;

            RebuildRuleCombo(0);
        }

        // ===== 규칙 목록 =====

        private void RebuildRuleCombo(int selectedIndex)
        {
            _loading = true;
            RuleCombo.Items.Clear();
            foreach (CombineRule rule in _settings.Rules)
                RuleCombo.Items.Add(rule.Enabled ? rule.Name : rule.Name + "  (꺼짐)");

            if (_settings.Rules.Count > 0)
                RuleCombo.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, _settings.Rules.Count - 1));
            _loading = false;

            DeleteRuleButton.IsEnabled = _settings.Rules.Count > 1;
            LoadRule(RuleCombo.SelectedIndex);
        }

        private void LoadRule(int index)
        {
            _rule = index >= 0 && index < _settings.Rules.Count ? _settings.Rules[index] : null;
            if (_rule == null) return;

            _loading = true;

            RuleNameBox.Text = _rule.Name;
            RuleEnabledCheck.IsChecked = _rule.Enabled;
            SkipEmptyCheck.IsChecked = _rule.SkipWhenAnySourceEmpty;

            FillCategoryCombo();
            ReloadParameterNames();
            FillTargetCombo();

            _loading = false;

            BuildSourceList();
            RefreshPreview();
        }

        private void FillCategoryCombo()
        {
            CategoryCombo.Items.Clear();
            bool found = false;
            foreach (KeyValuePair<int, string> category in _categories)
            {
                CategoryCombo.Items.Add(category.Value);
                if (category.Key == _rule!.CategoryId) found = true;
            }

            // 이 모델에 없는 카테고리가 설정에 저장돼 있을 수 있다(다른 프로젝트에서 만든 규칙) -
            // 조용히 다른 카테고리로 바뀌지 않도록 그 id를 그대로 보여준다.
            if (!found)
            {
                _categories.Insert(0, new KeyValuePair<int, string>(_rule!.CategoryId, "(이 모델에 없는 카테고리 " + _rule.CategoryId + ")"));
                CategoryCombo.Items.Insert(0, _categories[0].Value);
            }

            CategoryCombo.SelectedIndex = _categories.FindIndex(c => c.Key == _rule!.CategoryId);
        }

        private void ReloadParameterNames()
        {
            _parameterNames = ParamCombineEngine.ParameterNames(_doc, _rule!.CategoryId);
        }

        private void FillTargetCombo()
        {
            TargetCombo.Items.Clear();
            List<string> names = new List<string>(_parameterNames);
            if (!string.IsNullOrWhiteSpace(_rule!.TargetParameterName) && !names.Contains(_rule.TargetParameterName))
                names.Insert(0, _rule.TargetParameterName);

            foreach (string name in names) TargetCombo.Items.Add(name);
            TargetCombo.SelectedIndex = names.IndexOf(_rule.TargetParameterName ?? "");
        }

        // ===== 소스 목록 =====

        private static void AddSourceColumns(WpfGrid grid)
        {
            double[] widths = { 0, 54, 46, 64, 0, 56, 0, 0, 0 };
            foreach (double width in widths)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = width > 0 ? new GridLength(width) : GridLength.Auto,
                });
            }
            // 첫 칸(매개변수 이름)만 남는 폭을 전부 가져간다.
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        }

        private void BuildSourceList()
        {
            SourceListPanel.Children.Clear();
            if (_rule == null) return;

            SourceListPanel.Children.Add(BuildSourceHeader());

            if (_rule.Sources.Count == 0)
            {
                SourceListPanel.Children.Add(new TextBlock
                {
                    Text = "합칠 매개변수가 없습니다. 오른쪽 위의 '매개변수 추가'를 눌러 순서대로 추가하세요.",
                    Foreground = Theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8),
                });
                return;
            }

            for (int i = 0; i < _rule.Sources.Count; i++)
                SourceListPanel.Children.Add(BuildSourceRow(_rule.Sources[i], i));
        }

        private UIElement BuildSourceHeader()
        {
            WpfGrid grid = new WpfGrid();
            AddSourceColumns(grid);

            void Label(int column, string text, string tooltip)
            {
                TextBlock block = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = Theme.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(column == 0 ? 0 : 4, 0, 4, 0),
                    ToolTip = tooltip,
                };
                WpfGrid.SetColumn(block, column);
                grid.Children.Add(block);
            }

            Label(0, "매개변수", "Revit에 실제로 있는 매개변수 이름만 고를 수 있습니다.");
            Label(1, "자리수", "0이면 자리수를 맞추지 않고 값을 그대로 씁니다.");
            Label(2, "채움", "자리수가 모자랄 때 채워 넣을 한 글자(보통 0).");
            Label(3, "채울 위치", "앞 = 값 앞에 채움(숫자용), 뒤 = 값 뒤에 채움(코드용).");
            Label(4, "자르기", "값이 자리수보다 길 때 잘라낼지 여부.");
            Label(5, "구분자", "이 값 다음에 끼워 넣을 문자(마지막 줄의 것은 쓰이지 않습니다).");

            return new Border
            {
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 5, 8, 5),
                Child = grid,
            };
        }

        private UIElement BuildSourceRow(CombineSourceField field, int index)
        {
            WpfGrid grid = new WpfGrid();
            AddSourceColumns(grid);

            // 매개변수 이름 - 오타를 막기 위해 모델에 실제로 있는 이름 중에서만 고르게 한다
            // (ChatGPT 대화에서도 "한 글자까지 동일해야 한다"가 이 기능의 첫 번째 함정으로 지적됐다).
            List<string> names = new List<string>(_parameterNames);
            if (!string.IsNullOrWhiteSpace(field.ParameterName) && !names.Contains(field.ParameterName))
                names.Insert(0, field.ParameterName);

            WpfComboBox nameCombo = new WpfComboBox
            {
                MinHeight = 24,
                IsTextSearchEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            foreach (string name in names) nameCombo.Items.Add(name);
            nameCombo.SelectedIndex = names.IndexOf(field.ParameterName ?? "");
            nameCombo.SelectionChanged += (s, e) =>
            {
                if (_loading || nameCombo.SelectedItem == null) return;
                field.ParameterName = nameCombo.SelectedItem.ToString() ?? "";
                MarkDirty();
                RefreshPreview();
            };
            WpfGrid.SetColumn(nameCombo, 0);
            grid.Children.Add(nameCombo);

            WpfTextBox widthBox = new WpfTextBox
            {
                Text = field.Width.ToString(CultureInfo.InvariantCulture),
                MinHeight = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                TextAlignment = TextAlignment.Center,
            };
            widthBox.TextChanged += (s, e) =>
            {
                if (_loading) return;
                field.Width = ParseWidth(widthBox.Text);
                MarkDirty();
                RefreshPreview();
            };
            WpfGrid.SetColumn(widthBox, 1);
            grid.Children.Add(widthBox);

            WpfTextBox padBox = new WpfTextBox
            {
                Text = field.PadChar ?? "",
                MaxLength = 1,
                MinHeight = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                TextAlignment = TextAlignment.Center,
            };
            padBox.TextChanged += (s, e) =>
            {
                if (_loading) return;
                field.PadChar = padBox.Text;
                MarkDirty();
                RefreshPreview();
            };
            WpfGrid.SetColumn(padBox, 2);
            grid.Children.Add(padBox);

            WpfComboBox sideCombo = new WpfComboBox
            {
                MinHeight = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
            };
            sideCombo.Items.Add("앞");
            sideCombo.Items.Add("뒤");
            sideCombo.SelectedIndex = field.Pad == PadSide.Left ? 0 : 1;
            sideCombo.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                field.Pad = sideCombo.SelectedIndex == 1 ? PadSide.Right : PadSide.Left;
                MarkDirty();
                RefreshPreview();
            };
            WpfGrid.SetColumn(sideCombo, 3);
            grid.Children.Add(sideCombo);

            WpfCheckBox truncateCheck = new WpfCheckBox
            {
                IsChecked = field.Truncate,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
            };
            truncateCheck.Checked += (s, e) => { if (!_loading) { field.Truncate = true; MarkDirty(); RefreshPreview(); } };
            truncateCheck.Unchecked += (s, e) => { if (!_loading) { field.Truncate = false; MarkDirty(); RefreshPreview(); } };
            WpfGrid.SetColumn(truncateCheck, 4);
            grid.Children.Add(truncateCheck);

            WpfTextBox separatorBox = new WpfTextBox
            {
                Text = field.SeparatorAfter ?? "",
                MaxLength = 4,
                MinHeight = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                TextAlignment = TextAlignment.Center,
                // 마지막 줄의 구분자는 결과에 쓰이지 않으므로 비활성화해 "왜 안 붙지?"를 미리 없앤다.
                IsEnabled = index < _rule!.Sources.Count - 1,
            };
            separatorBox.TextChanged += (s, e) =>
            {
                if (_loading) return;
                field.SeparatorAfter = separatorBox.Text;
                MarkDirty();
                RefreshPreview();
            };
            WpfGrid.SetColumn(separatorBox, 5);
            grid.Children.Add(separatorBox);

            Button upButton = SmallButton("▲", "한 칸 위로");
            upButton.IsEnabled = index > 0;
            upButton.Click += (s, e) => MoveSource(index, -1);
            WpfGrid.SetColumn(upButton, 6);
            grid.Children.Add(upButton);

            Button downButton = SmallButton("▼", "한 칸 아래로");
            downButton.IsEnabled = index < _rule.Sources.Count - 1;
            downButton.Click += (s, e) => MoveSource(index, 1);
            WpfGrid.SetColumn(downButton, 7);
            grid.Children.Add(downButton);

            Button deleteButton = SmallButton("✕", "이 매개변수를 결합에서 뺍니다");
            deleteButton.Foreground = Theme.DangerText;
            deleteButton.Click += (s, e) =>
            {
                _rule.Sources.RemoveAt(index);
                MarkDirty();
                BuildSourceList();
                RefreshPreview();
            };
            WpfGrid.SetColumn(deleteButton, 8);
            grid.Children.Add(deleteButton);

            return new Border
            {
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 5, 8, 5),
                Child = grid,
            };
        }

        private static Button SmallButton(string text, string tooltip) => new Button
        {
            Content = text,
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };

        private static int ParseWidth(string text)
        {
            if (!int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 0;
            return Math.Max(0, Math.Min(64, value));
        }

        private void MoveSource(int index, int delta)
        {
            if (_rule == null) return;
            int target = index + delta;
            if (target < 0 || target >= _rule.Sources.Count) return;

            CombineSourceField field = _rule.Sources[index];
            _rule.Sources.RemoveAt(index);
            _rule.Sources.Insert(target, field);
            MarkDirty();
            BuildSourceList();
            RefreshPreview();
        }

        // ===== 미리보기 =====

        private void RefreshPreview()
        {
            if (_rule == null) return;

            FormatText.Text = DescribeFormat(_rule);

            PreviewListPanel.Children.Clear();

            if (_rule.Sources.Count == 0 || string.IsNullOrWhiteSpace(_rule.TargetParameterName))
            {
                ProjectionText.Text = "합칠 매개변수와 결과를 쓸 매개변수를 먼저 정해주세요.";
                ApplyNowButton.IsEnabled = false;
                return;
            }

            ApplyNowButton.IsEnabled = true;

            List<CombinePreviewRow> rows = ParamCombineEngine.Preview(_doc, _rule, PreviewRowLimit, out CombineRunResult projected);
            if (projected.Total == 0)
            {
                ProjectionText.Text = "이 모델에 해당 카테고리 요소가 없습니다.";
            }
            else if (projected.TargetMissing == projected.Total)
            {
                // 사용자가 고른 대상 매개변수가 이 모델에 아예 없는 경우 - 애드인은 공유 매개변수를 대신
                // 만들지 않으므로(회사 표준 공유 매개변수 파일을 건드리지 않기 위함) 여기서 분명히 알린다.
                ProjectionText.Text = "이 카테고리의 요소 어디에도 '" + _rule.TargetParameterName +
                    "' 매개변수가 없습니다. Revit에서 그 매개변수를 만들어 이 카테고리에 인스턴스·문자 형식으로 붙인 뒤 다시 시도하세요.";
                ProjectionText.Foreground = Theme.WarningText;
                return;
            }
            else
            {
                ProjectionText.Text = "지금 적용하면 → " + projected.Summary(true);
            }

            ProjectionText.Foreground = Theme.TextSecondary;

            if (rows.Count == 0)
            {
                PreviewListPanel.Children.Add(new TextBlock
                {
                    Text = "미리 볼 요소가 없습니다.",
                    Foreground = Theme.TextSecondary,
                    Margin = new Thickness(8),
                });
                return;
            }

            PreviewListPanel.Children.Add(BuildPreviewRow("요소", "현재값", "적용 후", null, true));
            foreach (CombinePreviewRow row in rows)
            {
                PreviewListPanel.Children.Add(BuildPreviewRow(
                    row.ElementLabel,
                    string.IsNullOrEmpty(row.CurrentValue) ? "(비어 있음)" : row.CurrentValue,
                    DescribeOutcome(row),
                    row.WillChange ? Theme.Accent : Theme.TextSecondary,
                    false));
            }

            if (projected.Total > rows.Count)
            {
                PreviewListPanel.Children.Add(new TextBlock
                {
                    Text = "… 외 " + (projected.Total - rows.Count) + "개 (앞의 " + rows.Count + "개만 미리 보여줍니다)",
                    Foreground = Theme.TextSecondary,
                    FontSize = 11,
                    Margin = new Thickness(8, 4, 8, 4),
                });
            }
        }

        private static string DescribeOutcome(CombinePreviewRow row) => row.Outcome switch
        {
            CombineOutcome.Changed => row.NewValue,
            CombineOutcome.AlreadySame => "(이미 같음)",
            CombineOutcome.TargetMissing => "(대상 매개변수 없음)",
            CombineOutcome.TargetNotText => "(대상이 문자 형식이 아님)",
            CombineOutcome.TargetReadOnly => "(대상이 읽기 전용)",
            CombineOutcome.SkippedEmptySource => "(소스가 비어 건너뜀)",
            _ => "(쓸 수 없음)",
        };

        private UIElement BuildPreviewRow(string label, string current, string next, System.Windows.Media.Brush? nextBrush, bool header)
        {
            WpfGrid grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            void Cell(int column, string text, System.Windows.Media.Brush? brush)
            {
                TextBlock block = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                    FontFamily = header || column == 0 ? new System.Windows.Media.FontFamily("Segoe UI") : new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = brush ?? (header ? Theme.TextSecondary : Theme.TextPrimary),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(column == 0 ? 0 : 6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                WpfGrid.SetColumn(block, column);
                grid.Children.Add(block);
            }

            Cell(0, label, header ? Theme.TextSecondary : Theme.TextPrimary);
            Cell(1, current, Theme.TextSecondary);
            Cell(2, next, nextBrush);

            return new Border
            {
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(0, 0, 0, header ? 1 : 0),
                Padding = new Thickness(8, 3, 8, 3),
                Child = grid,
            };
        }

        // "용도[1] + 지상지하[1] + 층[2,0] + 번호[3,0]  →  최대 7자리" 같은 한 줄 요약.
        private static string DescribeFormat(CombineRule rule)
        {
            if (rule.Sources.Count == 0) return "합칠 매개변수가 없습니다.";

            StringBuilder sb = new StringBuilder();
            int fixedLength = 0;
            bool variable = false;

            for (int i = 0; i < rule.Sources.Count; i++)
            {
                CombineSourceField field = rule.Sources[i];
                string name = string.IsNullOrWhiteSpace(field.ParameterName) ? "(미지정)" : field.ParameterName;
                sb.Append(name);
                if (field.Width > 0)
                {
                    sb.Append('[').Append(field.Width).Append("자리");
                    if (!string.IsNullOrEmpty(field.PadChar))
                        sb.Append(", ").Append(field.Pad == PadSide.Left ? "앞" : "뒤").Append(field.PadChar).Append("채움");
                    sb.Append(']');
                    fixedLength += field.Width;
                }
                else
                {
                    variable = true;
                }

                if (i < rule.Sources.Count - 1)
                {
                    fixedLength += (field.SeparatorAfter ?? "").Length;
                    sb.Append(string.IsNullOrEmpty(field.SeparatorAfter)
                        ? " + "
                        : " + \"" + field.SeparatorAfter + "\" + ");
                }
            }

            sb.Append("  →  ").Append(variable ? "총 " + fixedLength + "자리 + 가변" : "총 " + fixedLength + "자리");
            return sb.ToString();
        }

        // ===== 이벤트 =====

        private void MarkDirty()
        {
            _dirty = true;
            StatusText.Text = "저장하지 않은 변경이 있습니다.";
        }

        private void RuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            LoadRule(RuleCombo.SelectedIndex);
        }

        private void RuleNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _rule == null) return;
            _rule.Name = RuleNameBox.Text;
            MarkDirty();
        }

        private void RuleEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _rule == null) return;
            _rule.Enabled = RuleEnabledCheck.IsChecked == true;
            MarkDirty();
        }

        private void SkipEmptyCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _rule == null) return;
            _rule.SkipWhenAnySourceEmpty = SkipEmptyCheck.IsChecked == true;
            MarkDirty();
            RefreshPreview();
        }

        private void AutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
            MarkDirty();
        }

        private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _rule == null) return;
            int index = CategoryCombo.SelectedIndex;
            if (index < 0 || index >= _categories.Count) return;

            _rule.CategoryId = _categories[index].Key;
            MarkDirty();

            // 카테고리가 바뀌면 고를 수 있는 매개변수 목록 자체가 달라진다 - 기존에 고른 이름은 그대로 두고
            // (그 이름이 새 카테고리에도 있을 수 있다) 목록만 다시 채운다.
            _loading = true;
            ReloadParameterNames();
            FillTargetCombo();
            _loading = false;

            BuildSourceList();
            RefreshPreview();
        }

        private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _rule == null || TargetCombo.SelectedItem == null) return;
            _rule.TargetParameterName = TargetCombo.SelectedItem.ToString() ?? "";
            MarkDirty();
            RefreshPreview();
        }

        private void AddSourceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rule == null) return;
            _rule.Sources.Add(new CombineSourceField { PadChar = "0", Pad = PadSide.Left });
            MarkDirty();
            BuildSourceList();
            RefreshPreview();
        }

        private void AddRuleButton_Click(object sender, RoutedEventArgs e)
        {
            CombineRule rule = new CombineRule
            {
                Name = "규칙 " + (_settings.Rules.Count + 1),
                CategoryId = _rule?.CategoryId ?? BuiltInCategoryIds.Rooms,
                TargetParameterName = "",
                Sources = new List<CombineSourceField>(),
            };
            _settings.Rules.Add(rule);
            MarkDirty();
            RebuildRuleCombo(_settings.Rules.Count - 1);
        }

        private void DeleteRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rule == null || _settings.Rules.Count <= 1) return;

            MessageBoxResult answer = MessageBox.Show(
                "규칙 '" + _rule.Name + "'을 삭제할까요?\n이미 모델에 기입된 값은 그대로 남습니다.",
                "자동 결합", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            int index = _settings.Rules.IndexOf(_rule);
            _settings.Rules.RemoveAt(index);
            MarkDirty();
            RebuildRuleCombo(Math.Max(0, index - 1));
        }

        private void ApplyNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveSettings()) return;

            CombineRunResult result;
            using (Transaction tx = new Transaction(_doc, "자동 결합 - 전체 적용"))
            {
                tx.Start();
                result = ParamCombineEngine.RunAll(_doc, _settings);
                tx.Commit();
            }

            RefreshPreview();
            StatusText.Text = result.Summary();
        }

        private bool SaveSettings()
        {
            if (_rule != null && _rule.Sources.Any(s => string.IsNullOrWhiteSpace(s.ParameterName)))
            {
                MessageBox.Show("매개변수를 고르지 않은 줄이 있습니다.", "자동 결합", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 결과 매개변수를 소스로도 쓰면 자기 값을 자기가 덮어쓰며 계속 흔들린다 - 저장 전에 막는다.
            foreach (CombineRule rule in _settings.Rules)
            {
                if (rule.Sources.Any(s => string.Equals(s.ParameterName, rule.TargetParameterName, StringComparison.Ordinal)))
                {
                    MessageBox.Show("규칙 '" + rule.Name + "'에서 결과 매개변수(" + rule.TargetParameterName +
                                    ")를 합칠 매개변수로도 쓰고 있습니다. 둘은 서로 달라야 합니다.",
                        "자동 결합", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            _settings.Save();
            _dirty = false;
            StatusText.Text = "저장했습니다.";
            RebuildRuleComboLabelsOnly();
            return true;
        }

        // 규칙의 "(꺼짐)" 표시만 최신화한다 - LoadRule까지 다시 돌면 편집 중이던 포커스가 날아간다.
        private void RebuildRuleComboLabelsOnly()
        {
            _loading = true;
            int selected = RuleCombo.SelectedIndex;
            RuleCombo.Items.Clear();
            foreach (CombineRule rule in _settings.Rules)
                RuleCombo.Items.Add(rule.Enabled ? rule.Name : rule.Name + "  (꺼짐)");
            RuleCombo.SelectedIndex = Math.Max(0, Math.Min(selected, _settings.Rules.Count - 1));
            _loading = false;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveSettings();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_dirty)
            {
                MessageBoxResult answer = MessageBox.Show(
                    "저장하지 않은 변경이 있습니다. 저장할까요?",
                    "자동 결합", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                if (answer == MessageBoxResult.Yes && !SaveSettings()) { e.Cancel = true; return; }
            }

            base.OnClosing(e);
        }
    }
}
