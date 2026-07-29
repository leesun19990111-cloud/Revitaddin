using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WallSplitter
{
    // 카테고리 그래픽 재정의(선/패턴 색상)를 위한 간단한 색상 선택 창 - 이 코드베이스는 커스텀 컨트롤을
    // 안 쓰는 관례라(CLAUDE.md 참고) 전체 색상환 대신 자주 쓰는 색 팔레트 + 정확한 값이 필요할 때를
    // 위한 16진수 직접 입력으로 구성했다. 팔레트를 클릭하면 바로 적용되어 닫힌다(QuickToggleSettingsWindow의
    // 버튼 켜짐 색상 선택과 같은 방식).
    public partial class ColorPickerPopupWindow : Window
    {
        public int? ResultColor { get; private set; }

        private static readonly (string Hex, string Name)[] Palette =
        {
            ("#000000", "검정"), ("#FFFFFF", "흰색"), ("#808080", "회색"), ("#A6A6A6", "밝은 회색"),
            ("#FF0000", "빨강"), ("#00A030", "초록"), ("#0000FF", "파랑"), ("#FFFF00", "노랑"),
            ("#00FFFF", "시안"), ("#FF00FF", "마젠타"), ("#A6595D", "적갈"), ("#3D8F5C", "짙은 초록"),
            ("#5980A6", "스틸블루"), ("#A67B3D", "호박"), ("#6B5DA6", "보라"), ("#8F6B3D", "갈색"),
        };

        public ColorPickerPopupWindow(int? currentColor)
        {
            InitializeComponent();
            Build(currentColor);
        }

        private void Build(int? currentColor)
        {
            RootPanel.Children.Add(new TextBlock { Text = "자주 쓰는 색", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });

            WrapPanel grid = new WrapPanel();
            foreach ((string hex, string name) in Palette)
            {
                Color c = (Color)ColorConverter.ConvertFromString(hex);
                int rgb = (c.R << 16) | (c.G << 8) | c.B;
                Border swatch = new Border
                {
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(0, 0, 4, 4),
                    BorderThickness = new Thickness(currentColor == rgb ? 2 : 1),
                    BorderBrush = currentColor == rgb ? Theme.TextPrimary : Theme.Border,
                    Background = new SolidColorBrush(c),
                    Cursor = Cursors.Hand,
                    ToolTip = name,
                };
                swatch.MouseLeftButtonDown += (s, e) => { ResultColor = rgb; DialogResult = true; };
                grid.Children.Add(swatch);
            }
            RootPanel.Children.Add(grid);

            RootPanel.Children.Add(new TextBlock { Text = "직접 입력 (16진수, 예: FF8800)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) });
            StackPanel hexRow = new StackPanel { Orientation = Orientation.Horizontal };
            TextBox hexBox = new TextBox { Width = 120, Text = currentColor.HasValue ? currentColor.Value.ToString("X6") : "" };
            Button applyButton = new Button { Content = "적용", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            applyButton.Click += (s, e) =>
            {
                string text = hexBox.Text.Trim().TrimStart('#');
                if (int.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb))
                {
                    ResultColor = rgb;
                    DialogResult = true;
                }
                else
                {
                    MessageBox.Show("16진수 색상 코드를 정확히 입력하세요 (예: FF8800).", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            hexRow.Children.Add(hexBox);
            hexRow.Children.Add(applyButton);
            RootPanel.Children.Add(hexRow);

            StackPanel bottomRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button clearButton = new Button { Content = "재정의 해제", Padding = new Thickness(10, 4, 10, 4) };
            clearButton.Click += (s, e) => { ResultColor = null; DialogResult = true; };
            Button cancelButton = new Button { Content = "취소", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
            bottomRow.Children.Add(clearButton);
            bottomRow.Children.Add(cancelButton);
            RootPanel.Children.Add(bottomRow);
        }
    }
}
