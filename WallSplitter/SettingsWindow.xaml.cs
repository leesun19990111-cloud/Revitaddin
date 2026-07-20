using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WallSplitter
{
    public partial class SettingsWindow : Window
    {
        private static readonly (string Label, string Sep)[] SeparatorOptions =
        {
            ("(없음)", ""),
            ("-", "-"),
            ("_", "_"),
        };

        private static readonly (TokenKind Kind, string Label)[] KindOptions =
        {
            (TokenKind.ProjectName, "프로젝트명"),
            (TokenKind.BuildingName, "건물명"),
            (TokenKind.Material, "재료명"),
            (TokenKind.Thickness, "두께"),
            (TokenKind.Custom, "기타"),
        };

        private List<NamingToken> _tokens;
        private readonly NamingSettings _settings;

        public SettingsWindow()
        {
            InitializeComponent();

            AddKindCombo.ItemsSource = KindOptions.Select(k => k.Label).ToList();
            AddKindCombo.SelectedIndex = 0;

            _settings = NamingSettings.Load();
            _tokens = _settings.Tokens
                .Select(t => new NamingToken { Kind = t.Kind, Value = t.Value, SeparatorAfter = t.SeparatorAfter })
                .ToList();

            ModeTabControl.SelectedIndex = _settings.Mode == NamingMode.DirectType ? 1 : 0;

            RefreshList();
        }

        private static string KindLabel(TokenKind kind) => KindOptions.First(k => k.Kind == kind).Label;

        private static bool IsEditable(TokenKind kind) =>
            kind == TokenKind.ProjectName || kind == TokenKind.BuildingName || kind == TokenKind.Custom;

        // 위/아래 이동 버튼 아이콘. "▲"/"▼" 같은 텍스트 글리프는 폰트에 없으면 안 보일 수 있어서
        // (실제로 Revit에서 안 보인다는 보고가 있었음) 직접 그린 삼각형으로 대체 - 폰트와 무관하게 항상 렌더링된다.
        private static Polygon CreateTriangle(bool pointingUp)
        {
            PointCollection points = pointingUp
                ? new PointCollection { new Point(5, 0), new Point(10, 8), new Point(0, 8) }
                : new PointCollection { new Point(0, 0), new Point(10, 0), new Point(5, 8) };
            return new Polygon
            {
                Points = points,
                Fill = Brushes.Black,
                Width = 10,
                Height = 8,
            };
        }

        // 삭제 버튼 아이콘. "✕" 텍스트 글리프 대신 대각선 두 개로 직접 그린 X - 이유는 CreateTriangle과 동일.
        private static UIElement CreateXMark()
        {
            Canvas canvas = new Canvas { Width = 10, Height = 10 };
            canvas.Children.Add(new Line { X1 = 0, Y1 = 0, X2 = 10, Y2 = 10, Stroke = Brushes.Black, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = 0, Y1 = 10, X2 = 10, Y2 = 0, Stroke = Brushes.Black, StrokeThickness = 1.5 });
            return canvas;
        }

        private void RefreshList()
        {
            TokenListPanel.Children.Clear();

            for (int i = 0; i < _tokens.Count; i++)
            {
                int index = i; // 클로저 캡처용 (RefreshList 재호출 시마다 새로 만들어지므로 안전)
                NamingToken token = _tokens[i];

                Border row = new Border
                {
                    BorderBrush = Brushes.LightGray,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(4, 6, 4, 6),
                };

                StackPanel stack = new StackPanel { Orientation = Orientation.Horizontal };
                row.Child = stack;

                Button upButton = new Button { Content = CreateTriangle(pointingUp: true), Width = 24, Margin = new Thickness(0, 0, 2, 0), IsEnabled = index > 0 };
                upButton.Click += (s, e) =>
                {
                    (_tokens[index - 1], _tokens[index]) = (_tokens[index], _tokens[index - 1]);
                    RefreshList();
                };
                stack.Children.Add(upButton);

                Button downButton = new Button { Content = CreateTriangle(pointingUp: false), Width = 24, Margin = new Thickness(0, 0, 8, 0), IsEnabled = index < _tokens.Count - 1 };
                downButton.Click += (s, e) =>
                {
                    (_tokens[index + 1], _tokens[index]) = (_tokens[index], _tokens[index + 1]);
                    RefreshList();
                };
                stack.Children.Add(downButton);

                TextBlock kindLabel = new TextBlock
                {
                    Text = KindLabel(token.Kind) + (IsEditable(token.Kind) ? "" : " (자동)"),
                    Width = 90,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = IsEditable(token.Kind) ? FontWeights.Normal : FontWeights.SemiBold,
                };
                stack.Children.Add(kindLabel);

                if (IsEditable(token.Kind))
                {
                    TextBox valueBox = new TextBox
                    {
                        Width = 160,
                        Text = token.Value,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                    };
                    valueBox.TextChanged += (s, e) => { token.Value = valueBox.Text; UpdatePreview(); };
                    stack.Children.Add(valueBox);
                }
                else
                {
                    TextBlock placeholder = new TextBlock
                    {
                        Text = "(복합벽에서 자동으로 읽어옴)",
                        Width = 160,
                        Foreground = Brushes.Gray,
                        FontStyle = FontStyles.Italic,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                    };
                    stack.Children.Add(placeholder);
                }

                if (index < _tokens.Count - 1)
                {
                    TextBlock sepLabel = new TextBlock { Text = "다음 구분자:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                    stack.Children.Add(sepLabel);

                    ComboBox sepCombo = new ComboBox
                    {
                        Width = 70,
                        ItemsSource = SeparatorOptions.Select(o => o.Label).ToList(),
                    };
                    int sepIndex = Array.FindIndex(SeparatorOptions, o => o.Sep == token.SeparatorAfter);
                    sepCombo.SelectedIndex = sepIndex >= 0 ? sepIndex : 0;
                    sepCombo.SelectionChanged += (s, e) =>
                    {
                        token.SeparatorAfter = SeparatorOptions[sepCombo.SelectedIndex].Sep;
                        UpdatePreview();
                    };
                    stack.Children.Add(sepCombo);
                }

                Button removeButton = new Button { Content = CreateXMark(), Width = 24, Margin = new Thickness(12, 0, 0, 0) };
                removeButton.Click += (s, e) => { _tokens.RemoveAt(index); RefreshList(); };
                stack.Children.Add(removeButton);

                TokenListPanel.Children.Add(row);
            }

            UpdatePreview();
        }

        private void UpdatePreview()
        {
            NamingSettings preview = new NamingSettings { Tokens = _tokens };
            PreviewText.Text = _tokens.Count > 0 ? preview.BuildName("샘플재질", 50) : "(매개변수가 없습니다)";
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            TokenKind kind = KindOptions[AddKindCombo.SelectedIndex].Kind;
            _tokens.Add(new NamingToken { Kind = kind, Value = "", SeparatorAfter = "" });
            RefreshList();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _tokens = NamingSettings.DefaultTokens();
            RefreshList();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            bool isDirectType = ModeTabControl.SelectedIndex == 1;

            // 토큰 목록은 "이름 형식으로 생성" 탭에서만 쓰이므로, "유형 직접 지정" 탭이 선택된 상태라면
            // 토큰이 비어 있어도 저장을 막을 이유가 없다.
            if (!isDirectType && _tokens.Count == 0)
            {
                MessageBox.Show("매개변수를 하나 이상 추가해야 합니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 기존에 로드해 둔 _settings를 그대로 재사용해야 TypeAssignmentPersistence(리본의 '단일/복수'
                // 버튼으로 설정하는 값)가 여기서 덮어써지지 않는다.
                _settings.Tokens = _tokens;
                _settings.Mode = isDirectType ? NamingMode.DirectType : NamingMode.Template;
                _settings.Save();
            }
            catch (Exception ex)
            {
                // 이벤트 핸들러에서 예외가 새어나가면 Revit 전체가 불안정해지므로 여기서 차단
                MessageBox.Show("설정 저장에 실패했습니다: " + ex.Message, "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
