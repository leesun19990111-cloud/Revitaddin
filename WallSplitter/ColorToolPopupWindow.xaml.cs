using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // "색상 버튼"을 클릭하면 뜨는 실시간 조절 패널 (2026-07-29 추가, 사용자 요청 - "커스텀 버튼창에서
    // 만든 색상버튼을 클릭하면 창의 위치에따라 상단/하단으로 UI가 펼쳐지면서 활성화되어있는 뷰의 모델
    // 카테고리에 색상을 지정해줄 수 있고, 투명도를 조절해줄 수 있는 버튼이 있으면 좋겠어"). 모덜(모달이
    // 아닌) 창으로 열어두고(QuickToggleToolbar.ShowColorToolPopup), 팔레트를 클릭하거나 슬라이더를
    // 움직일 때마다 그 즉시 ExternalEvent로 활성 뷰에 반영한다 - 다른 버튼들의 on/off 토글과 달리 이
    // 패널은 열려있는 동안 계속 값을 바꿔가며 실시간으로 미리보기하는 용도라 "확인" 버튼이 없다.
    public partial class ColorToolPopupWindow : Window
    {
        private readonly QuickToggleButtonConfig _cfg;
        // 이 팝업을 연 문서에서 이름으로 다시 찾은 대상 카테고리 - 저장된 CategoryId를 그대로 쓰지 않는다.
        private readonly List<int> _resolvedCategoryIds = new List<int>();

        private static readonly (string Hex, string Name)[] Palette =
        {
            ("#000000", "검정"), ("#FFFFFF", "흰색"), ("#808080", "회색"), ("#A6A6A6", "밝은 회색"),
            ("#FF0000", "빨강"), ("#00A030", "초록"), ("#0000FF", "파랑"), ("#FFFF00", "노랑"),
            ("#00FFFF", "시안"), ("#FF00FF", "마젠타"), ("#A6595D", "적갈"), ("#3D8F5C", "짙은 초록"),
            ("#5980A6", "스틸블루"), ("#A67B3D", "호박"), ("#6B5DA6", "보라"), ("#8F6B3D", "갈색"),
        };

        public ColorToolPopupWindow(View view, QuickToggleButtonConfig cfg)
        {
            InitializeComponent();
            _cfg = cfg;

            RootPanel.Children.Add(new TextBlock { Text = _cfg.Name, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });

            if (_cfg.ColorButtonCategories.Count == 0)
            {
                RootPanel.Children.Add(new TextBlock
                {
                    Text = "이 버튼에 지정된 모델 카테고리가 없습니다. 설정 창에서 먼저 카테고리를 선택하세요.",
                    Foreground = Theme.WarningText,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            // 저장된 CategoryId는 문서마다 다를 수 있어(설정이 PC 전역이 된 2026-09-03부터) 이름으로
            // 다시 찾은 결과를 쓴다. 사용자가 카테고리를 골랐는데 이 문서에 하나도 없으면 조작할 대상이
            // 없으므로 위의 "지정된 카테고리가 없습니다"와 같은 안내를 보여준다.
            _resolvedCategoryIds = QuickToggleService.ResolveColorCategoryIds(view.Document, _cfg);
            if (_resolvedCategoryIds.Count == 0)
            {
                RootPanel.Children.Add(new TextBlock
                {
                    Text = "이 버튼에 지정된 모델 카테고리를 지금 열려 있는 프로젝트에서 찾지 못했습니다.",
                    Foreground = Theme.WarningText,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            int firstCategoryId = _resolvedCategoryIds[0];
            (int? initialColor, int initialTransparency) = QuickToggleService.ReadCurrentColorAndTransparency(view, firstCategoryId);
            RenderControls(initialColor, initialTransparency);
        }

        // 팔레트/16진수/투명도/지우기/닫기 컨트롤을 실제로 그린다 - 최초 생성 시엔 뷰에서 읽어온 값으로,
        // "재지정 지우기" 이후엔 지운 직후의 값(색상 없음/투명도 0)으로 다시 그린다. ExternalEvent는
        // 비동기라(Raise() 직후 Revit이 곧바로 처리한다는 보장이 없음) 지운 다음 뷰를 다시 읽어 갱신하면
        // 아직 반영 전의 예전 값을 보여줄 위험이 있다 - 그래서 지우기는 view를 다시 읽지 않고 "지운
        // 상태가 어떤 모습이어야 하는지"를 이미 알고 있는 값(null/0)으로 즉시 다시 그린다.
        private void RenderControls(int? initialColor, int initialTransparency)
        {
            // 상단 이름 표시(RootPanel.Children[0])만 남기고 나머지 컨트롤만 다시 그린다.
            while (RootPanel.Children.Count > 1) RootPanel.Children.RemoveAt(1);

            RootPanel.Children.Add(new TextBlock { Text = "색상", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            WrapPanel grid = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach ((string hex, string name) in Palette)
            {
                System.Windows.Media.Color c = (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex);
                int rgb = (c.R << 16) | (c.G << 8) | c.B;
                Border swatch = new Border
                {
                    Width = 26, Height = 26,
                    Margin = new Thickness(0, 0, 4, 4),
                    BorderThickness = new Thickness(initialColor == rgb ? 2 : 1),
                    BorderBrush = initialColor == rgb ? Theme.TextPrimary : Theme.Border,
                    Background = new SolidColorBrush(c),
                    Cursor = Cursors.Hand,
                    ToolTip = name,
                };
                swatch.MouseLeftButtonDown += (s, e) => ApplyColor(rgb);
                grid.Children.Add(swatch);
            }
            RootPanel.Children.Add(grid);

            StackPanel hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            TextBox hexBox = new TextBox { Width = 100, Text = initialColor.HasValue ? initialColor.Value.ToString("X6") : "" };
            Button applyHexButton = new Button { Content = "적용", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            applyHexButton.Click += (s, e) =>
            {
                string text = hexBox.Text.Trim().TrimStart('#');
                if (int.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb))
                    ApplyColor(rgb);
                else
                    MessageBox.Show("16진수 색상 코드를 정확히 입력하세요 (예: FF8800).", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            hexRow.Children.Add(new TextBlock { Text = "16진수", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextSecondary, Margin = new Thickness(0, 0, 6, 0) });
            hexRow.Children.Add(hexBox);
            hexRow.Children.Add(applyHexButton);
            RootPanel.Children.Add(hexRow);

            RootPanel.Children.Add(new TextBlock { Text = "투명도", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            StackPanel transRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            Slider slider = new Slider { Minimum = 0, Maximum = 100, Width = 150, VerticalAlignment = VerticalAlignment.Center, Value = initialTransparency };
            TextBlock valueLabel = new TextBlock { Text = initialTransparency + "%", Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            slider.ValueChanged += (s, e) =>
            {
                int v = (int)Math.Round(slider.Value);
                valueLabel.Text = v + "%";
                ApplyTransparency(v);
            };
            transRow.Children.Add(slider);
            transRow.Children.Add(valueLabel);
            RootPanel.Children.Add(transRow);

            StackPanel buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            // 2026-07-29, "선택한 카테고리 요소에 입혀진 색상이 아무것도 없게 만들어주는 '재지정 지우기'
            // 버튼" 요청으로 추가 - 색상/투명도만 기본값으로 되돌리는 게 아니라 이 버튼이 만든 그래픽
            // 재정의 자체를 완전히 비운다(QuickToggleService.ClearColorTool, 빈 OverrideGraphicSettings로
            // 교체) - 다른 방법으로 걸린 재정의(예: 프리셋)까지 지우진 않지만, 이 색상 버튼이 흔히 쓰는
            // "실채우기+색+투명도" 조합은 확실히 제거된다.
            Button clearButton = new Button { Content = "재지정 지우기", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            clearButton.Click += (s, e) =>
            {
                SendClear();
                RenderControls(null, 0);
            };
            Button closeButton = new Button { Content = "닫기", Padding = new Thickness(14, 4, 14, 4) };
            closeButton.Click += (s, e) => Close();
            buttonRow.Children.Add(clearButton);
            buttonRow.Children.Add(closeButton);
            RootPanel.Children.Add(buttonRow);
        }

        private void ApplyColor(int rgb) => Send(rgb, null, clear: false);

        private void ApplyTransparency(int transparency) => Send(null, transparency, clear: false);

        private void SendClear() => Send(null, null, clear: true);

        // 어느 문서/뷰에 적용할지는 여기서 정하지 않는다 - QuickToggleExternalEventHandler.ExecuteColorApply가
        // 실행되는 바로 그 순간의 활성 뷰를 다시 조회한다(이 팝업을 연 뒤 사용자가 다른 뷰로 전환했을 수
        // 있으므로, 팝업을 열 때 캡처해둔 뷰가 아니라 실행 시점 기준으로 적용하는 게 맞다).
        private void Send(int? color, int? transparency, bool clear)
        {
            if (App.QuickToggleHandler == null || App.QuickToggleEvent == null) return;

            App.QuickToggleHandler.PendingColorApply = new ColorToolApplyRequest
            {
                CategoryIds = new List<int>(_resolvedCategoryIds),
                Color = color,
                Transparency = transparency,
                Clear = clear,
            };
            App.QuickToggleEvent.Raise();
        }
    }
}
