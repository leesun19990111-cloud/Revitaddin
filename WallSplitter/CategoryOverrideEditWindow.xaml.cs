using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 프리셋의 "카테고리(V/G)" 탭에서 카테고리 하나를 "편집" 눌렀을 때 여는 창 - Revit V/G 대화상자가
    // 카테고리 한 줄에서 접근할 수 있는 항목(표시/하프톤/상세수준/투영선/절단선/표면 패턴/절단 패턴/투명도)을
    // 그대로 옮겼다(2026-07-29 요청: "V/G 편집창에 있는 것들을 그대로 다 옮겨 넣어서"). Revit 실제
    // 대화상자는 선 재정의/패턴 재정의를 별도 하위 대화상자로 나누지만, 카테고리마다 매번 여러 창을 열게
    // 하는 대신 이 창 하나에 전부 담아 스크롤로 보게 했다 - 이 코드베이스가 커스텀 컨트롤을 최소화하는
    // 관례와도 맞는다.
    //
    // 모든 필드가 nullable인 이유: "재정의 안 함"(null)과 "이 값으로 재정의함"을 구분해야 하기 때문 -
    // 각 컨트롤은 맨 앞에 "재정의 안 함" 항목을 두거나(콤보박스), 3상태 체크박스(IsThreeState)의
    // Indeterminate를 null로 쓰거나, 별도 "재정의" 체크박스로 활성/비활성을 gating한다(투명도).
    public partial class CategoryOverrideEditWindow : Window
    {
        public CategoryOverrideConfig? Result { get; private set; }

        private readonly Category _category;
        private readonly CategoryOverrideConfig _editable;
        private readonly List<string> _linePatternNames;
        private readonly List<string> _fillPatternNames;
        private readonly bool _immediateMode;

        private static readonly (string Label, string? Value)[] DetailLevelOptions =
        {
            ("재정의 안 함", null),
            ("거침", "Coarse"),
            ("중간", "Medium"),
            ("정밀", "Fine"),
        };

        public CategoryOverrideEditWindow(Document doc, Category category, CategoryOverrideConfig editable, bool immediateMode = false)
        {
            InitializeComponent();
            _category = category;
            _editable = editable;
            _immediateMode = immediateMode;
            _linePatternNames = QuickToggleService.AllLinePatternNames(doc);
            _fillPatternNames = QuickToggleService.AllFillPatternNames(doc);
            if (_immediateMode) Title = "그래픽 화면표시 편집";
            BuildContent();
        }

        private void BuildContent()
        {
            RootPanel.Children.Clear();

            RootPanel.Children.Add(new TextBlock
            {
                Text = $"'{_category.Name}' 재정의",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8),
            });
            RootPanel.Children.Add(new TextBlock
            {
                Text = _immediateMode
                    ? "바꿀 항목만 지정하세요. '변경 안 함'으로 둔 항목은 현재 설정을 그대로 유지하며, 확인을 누르면 현재 활성 뷰에 즉시 적용됩니다."
                    : "항목마다 '재정의 안 함'을 고르면 이 프리셋은 그 속성을 건드리지 않습니다. 이 편집 내용은 " +
                      "프리셋이 켜질 때 적용되고, 꺼지면 표시로 되돌아가며 모든 재정의가 지워집니다.",
                Foreground = Theme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            RootPanel.Children.Add(BuildTriStateCheck(_immediateMode
                    ? "가시성 (클릭할 때마다 변경 안 함 → 표시 → 숨김 순으로 순환)"
                    : "표시 (클릭할 때마다 재정의 안 함 → 표시 → 숨김 순으로 순환)",
                _editable.Visible, v => _editable.Visible = v));
            RootPanel.Children.Add(BuildTriStateCheck(_immediateMode ? "중간색 (하프톤)" : "하프톤",
                _editable.Halftone, v => _editable.Halftone = v));

            StackPanel detailRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
            detailRow.Children.Add(new TextBlock { Text = "상세수준", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            detailRow.Children.Add(BuildDetailLevelCombo(_editable.DetailLevel, v => _editable.DetailLevel = v));
            RootPanel.Children.Add(detailRow);

            RootPanel.Children.Add(BuildTransparencyRow());

            RootPanel.Children.Add(Separator("투영선 (Projection Lines)"));
            RootPanel.Children.Add(BuildLineOverrideRow(
                _editable.ProjectionLineWeight, w => _editable.ProjectionLineWeight = w,
                _editable.ProjectionLineColor, c => _editable.ProjectionLineColor = c,
                _editable.ProjectionLinePatternName, p => _editable.ProjectionLinePatternName = p));

            RootPanel.Children.Add(Separator("절단선 (Cut Lines)"));
            RootPanel.Children.Add(BuildLineOverrideRow(
                _editable.CutLineWeight, w => _editable.CutLineWeight = w,
                _editable.CutLineColor, c => _editable.CutLineColor = c,
                _editable.CutLinePatternName, p => _editable.CutLinePatternName = p));

            RootPanel.Children.Add(Separator("표면 패턴 (Surface Patterns)"));
            RootPanel.Children.Add(BuildFillOverrideRow("전경",
                _editable.SurfaceForegroundVisible, v => _editable.SurfaceForegroundVisible = v,
                _editable.SurfaceForegroundPatternName, p => _editable.SurfaceForegroundPatternName = p,
                _editable.SurfaceForegroundColor, c => _editable.SurfaceForegroundColor = c));
            RootPanel.Children.Add(BuildFillOverrideRow("배경",
                _editable.SurfaceBackgroundVisible, v => _editable.SurfaceBackgroundVisible = v,
                _editable.SurfaceBackgroundPatternName, p => _editable.SurfaceBackgroundPatternName = p,
                _editable.SurfaceBackgroundColor, c => _editable.SurfaceBackgroundColor = c));

            RootPanel.Children.Add(Separator("절단 패턴 (Cut Patterns)"));
            RootPanel.Children.Add(BuildFillOverrideRow("전경",
                _editable.CutForegroundVisible, v => _editable.CutForegroundVisible = v,
                _editable.CutForegroundPatternName, p => _editable.CutForegroundPatternName = p,
                _editable.CutForegroundColor, c => _editable.CutForegroundColor = c));
            RootPanel.Children.Add(BuildFillOverrideRow("배경",
                _editable.CutBackgroundVisible, v => _editable.CutBackgroundVisible = v,
                _editable.CutBackgroundPatternName, p => _editable.CutBackgroundPatternName = p,
                _editable.CutBackgroundColor, c => _editable.CutBackgroundColor = c));

            StackPanel buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            Button okButton = new Button { Content = "확인", Padding = new Thickness(18, 5, 18, 5), Style = (Style)FindResource("PrimaryButtonStyle") };
            okButton.Click += (s, e) => { Result = _editable; DialogResult = true; };
            Button cancelButton = new Button { Content = "취소", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            buttonRow.Children.Add(okButton);
            buttonRow.Children.Add(cancelButton);
            RootPanel.Children.Add(buttonRow);
        }

        private static TextBlock Separator(string text) => new TextBlock { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) };

        // IsThreeState 체크박스의 Indeterminate = null(재정의 안 함) - Click 시점엔 이미 WPF가 IsChecked를
        // 다음 상태로 넘긴 뒤라 그 값을 그대로 읽으면 된다.
        private static CheckBox BuildTriStateCheck(string label, bool? current, Action<bool?> onChanged)
        {
            CheckBox cb = new CheckBox { Content = label, IsThreeState = true, IsChecked = current, Margin = new Thickness(0, 2, 0, 2) };
            cb.Click += (s, e) => onChanged(cb.IsChecked);
            return cb;
        }

        private static ComboBox BuildWeightCombo(int? current, Action<int?> onChanged)
        {
            ComboBox combo = new ComboBox { Width = 120, Margin = new Thickness(4, 0, 4, 0) };
            combo.Items.Add("재정의 안 함");
            for (int w = 1; w <= 16; w++) combo.Items.Add(w.ToString());
            combo.SelectedIndex = current.HasValue && current.Value >= 1 && current.Value <= 16 ? current.Value : 0;
            combo.SelectionChanged += (s, e) => onChanged(combo.SelectedIndex <= 0 ? (int?)null : combo.SelectedIndex);
            return combo;
        }

        private ComboBox BuildPatternCombo(List<string> patternNames, string? current, Action<string?> onChanged)
        {
            ComboBox combo = new ComboBox { Width = 160, Margin = new Thickness(4, 0, 4, 0) };
            combo.Items.Add("재정의 안 함");
            foreach (string n in patternNames) combo.Items.Add(n);
            int idx = current == null ? -1 : patternNames.IndexOf(current);
            combo.SelectedIndex = idx < 0 ? 0 : idx + 1;
            combo.SelectionChanged += (s, e) => onChanged(combo.SelectedIndex <= 0 ? null : patternNames[combo.SelectedIndex - 1]);
            return combo;
        }

        private static ComboBox BuildDetailLevelCombo(string? current, Action<string?> onChanged)
        {
            ComboBox combo = new ComboBox { Width = 120 };
            foreach ((string label, string? _) in DetailLevelOptions) combo.Items.Add(label);
            int idx = Array.FindIndex(DetailLevelOptions, o => o.Value == current);
            combo.SelectedIndex = idx < 0 ? 0 : idx;
            combo.SelectionChanged += (s, e) => onChanged(DetailLevelOptions[combo.SelectedIndex].Value);
            return combo;
        }

        private Button BuildColorButton(int? currentColor, Action<int?> onChanged)
        {
            Button btn = new Button { Width = 32, Height = 22, Padding = new Thickness(0), Margin = new Thickness(4, 0, 4, 0) };

            void Refresh(int? c)
            {
                btn.Background = c.HasValue ? new SolidColorBrush(RgbToWpfColor(c.Value)) : Brushes.Transparent;
                btn.ToolTip = c.HasValue ? "#" + c.Value.ToString("X6") : "재정의 안 함 (클릭해서 색상 지정)";
            }

            Refresh(currentColor);
            int? current = currentColor;
            btn.Click += (s, e) =>
            {
                ColorPickerPopupWindow picker = new ColorPickerPopupWindow(current) { Owner = this };
                if (picker.ShowDialog() == true)
                {
                    current = picker.ResultColor;
                    Refresh(current);
                    onChanged(current);
                }
            };
            return btn;
        }

        private static System.Windows.Media.Color RgbToWpfColor(int rgb) =>
            System.Windows.Media.Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        private FrameworkElement BuildLineOverrideRow(int? weight, Action<int?> setWeight, int? color, Action<int?> setColor, string? patternName, Action<string?> setPattern)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = "두께", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextSecondary });
            row.Children.Add(BuildWeightCombo(weight, setWeight));
            row.Children.Add(new TextBlock { Text = "색상", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextSecondary, Margin = new Thickness(8, 0, 0, 0) });
            row.Children.Add(BuildColorButton(color, setColor));
            row.Children.Add(new TextBlock { Text = "패턴", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextSecondary, Margin = new Thickness(8, 0, 0, 0) });
            row.Children.Add(BuildPatternCombo(_linePatternNames, patternName, setPattern));
            return row;
        }

        private FrameworkElement BuildFillOverrideRow(string label, bool? visible, Action<bool?> setVisible, string? patternName, Action<string?> setPattern, int? color, Action<int?> setColor)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(BuildTriStateCheck(label, visible, setVisible));
            row.Children[0].SetValue(FrameworkElement.WidthProperty, 70.0);
            row.Children.Add(new TextBlock { Text = "패턴", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextSecondary, Margin = new Thickness(8, 0, 0, 0) });
            row.Children.Add(BuildPatternCombo(_fillPatternNames, patternName, setPattern));
            row.Children.Add(new TextBlock { Text = "색상", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextSecondary, Margin = new Thickness(8, 0, 0, 0) });
            row.Children.Add(BuildColorButton(color, setColor));
            return row;
        }

        private FrameworkElement BuildTransparencyRow()
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center };
            CheckBox enableBox = new CheckBox { Content = "투명도 재정의", VerticalAlignment = VerticalAlignment.Center, IsChecked = _editable.Transparency.HasValue, Margin = new Thickness(0, 0, 8, 0), Width = 90 };
            Slider slider = new Slider { Minimum = 0, Maximum = 100, Width = 150, VerticalAlignment = VerticalAlignment.Center, Value = _editable.Transparency ?? 0, IsEnabled = _editable.Transparency.HasValue };
            TextBlock valueLabel = new TextBlock { Text = (_editable.Transparency ?? 0) + "%", Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

            slider.ValueChanged += (s, e) =>
            {
                int v = (int)Math.Round(slider.Value);
                valueLabel.Text = v + "%";
                if (enableBox.IsChecked == true) _editable.Transparency = v;
            };
            enableBox.Checked += (s, e) => { slider.IsEnabled = true; _editable.Transparency = (int)Math.Round(slider.Value); };
            enableBox.Unchecked += (s, e) => { slider.IsEnabled = false; _editable.Transparency = null; };

            row.Children.Add(enableBox);
            row.Children.Add(slider);
            row.Children.Add(valueLabel);
            return row;
        }
    }
}
