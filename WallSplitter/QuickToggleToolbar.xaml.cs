using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.UI;
// Autodesk.Revit.DB를 통째로 using하면 Point가 System.Windows.Point와 충돌한다(아이콘 도형 그리기는
// QuickToggleIcons.cs로 옮겼지만, 드래그/위치 계산에 여전히 System.Windows.Point를 쓴다) - 필요한
// 타입만 별칭으로 가져와 충돌을 피한다.
using RevitDocument = Autodesk.Revit.DB.Document;
using RevitView = Autodesk.Revit.DB.View;

namespace WallSplitter
{
    // 세션 내내 떠 있는 모드리스 커스텀 툴바 - Revit 자체 신속접근 도구모음(QAT)에는 API로 버튼을 추가할 수
    // 없어(CLAUDE.md 참고), Revit 메인 창을 따라다니되 사용자가 드래그로 위치를 옮길 수 있는 자체 플로팅
    // 창으로 대체 구현했다. App.OnStartup에서 단 하나의 인스턴스만 만들어 Instance에 보관하고, 문서/뷰
    // 전환·유휴 이벤트에서 그 인스턴스의 메서드를 호출하는 식으로 배선한다 (이 프로젝트의 다른 창들과 달리
    // 모달이 아님).
    public partial class QuickToggleToolbar : Window
    {
        public static QuickToggleToolbar? Instance { get; private set; }

        // 활성 문서가 없으면 null - 설정이 문서(프로젝트 파일)별로 저장되므로, 리본의 "표시/숨김" 라벨을
        // 활성 문서 기준으로 맞추려는 App.OnQuickToggleViewActivated가 참조한다.
        public bool? CurrentToolbarVisible { get; private set; }

        private readonly UIApplication _uiapp;
        private RevitDocument? _cachedDoc;
        private QuickToggleSettings _cachedSettings = new QuickToggleSettings();

        // CONFIRMED LIVE BUG (2026-07-27), 두 번째 수정: `Idling`은 매우 자주(초당 여러 번) 발생하는데,
        // RefreshState()가 그때마다 RebuildButtons()로 버튼 전체를 지우고 새로 만들었다 - 마우스 클릭은
        // MouseDown(누름)과 MouseUp(뗌) 사이에 시간차가 있는데, 그 사이에 Idling이 한 번이라도 끼어들면
        // 눌렀던 바로 그 Button 인스턴스가 이미 지워지고 새 인스턴스로 교체돼 있어 Click 이벤트가 아예
        // 발생하지 않는 경우가 있었다 - "마우스를 가져다 대면 클릭이 잘 안 잡힌다"는 실측 피드백의 원인.
        // 고정: 버튼 "목록"(설정 저장 직후처럼 실제로 구성이 바뀐 경우)이 바뀌었을 때만 구조를 다시 짓고,
        // 그 외의 매 틱(Idling)에는 이미 만들어둔 같은 Button 인스턴스의 색/툴팁/활성 여부만 갱신한다.
        private List<QuickToggleButtonConfig>? _renderedButtons;
        private readonly List<(QuickToggleButtonConfig cfg, Button button, Canvas icon, TextBlock label)> _rows = new();

        // CONFIRMED LIVE BUG (2026-07-27), 세 번째 수정: 위치 재대입(Left/Top)을 고친 뒤에도 "클릭이 잘
        // 안 된다"는 재보고가 있었다 - UpdateButtonStates가 매 Idling 틱마다 IsEnabled/ToolTip/아이콘
        // 색을 "값이 같아도" 무조건 다시 대입하고 있었기 때문(같은 근본 패턴이 또 있었다). button.IsEnabled
        // 재대입은 값이 그대로여도 WPF의 IsEnabled 강제(coerce) 파이프라인을 다시 태우고, button.ToolTip
        // 재대입은 매번 새 문자열 인스턴스라 ToolTipService가 툴팁을 다시 여닫으려 든다 - 둘 다 마우스를
        // 누르고 있는 도중이면 ButtonBase의 마우스 캡처를 끊을 수 있다. 고정: 버튼별 마지막 상태를
        // 기억해두고(_lastStates), 실제로 상태가 바뀐 버튼만 갱신한다.
        private readonly Dictionary<string, QuickToggleButtonState> _lastStates = new();

        // "뷰 저장" 버튼으로 찍어둔 스냅샷 - 이번 Revit 세션(창 인스턴스) 한정 메모리 값이라 디스크에
        // 저장하지 않는다. 문서를 닫았다 다시 열면 사라지는 게 자연스럽다(그 문서에 대한 View 참조도
        // 더 이상 유효하지 않으므로).
        private ViewStateSnapshot? _savedViewState;

        public QuickToggleToolbar(UIApplication uiapp)
        {
            InitializeComponent();
            _uiapp = uiapp;
            Instance = this;

            new WindowInteropHelper(this) { Owner = uiapp.MainWindowHandle };

            UpdateSaveButtonVisual();
            RevertButton.Content = new Viewbox { Width = 20, Height = 16, Child = QuickToggleIcons.CreateUndoIcon(Theme.ToggleDisabled) };
        }

        // 문서가 하나도 없을 때(전부 닫힘) 호출 - 떠 있는 툴바를 정리한다.
        public void HideForNoDocument()
        {
            _cachedDoc = null;
            CurrentToolbarVisible = null;
            Hide();
        }

        // 뷰 전환 등으로 설정을 다시 읽어야 할 때 강제로 디스크에서 재로드한다.
        // (설정 창에서 저장 직후에도 호출 - 저장한 값이 바로 반영되도록)
        public void ForceReloadSettings(RevitDocument doc)
        {
            _cachedDoc = doc;
            _cachedSettings = QuickToggleSettings.Load(doc);
        }

        private void EnsureSettingsLoaded(RevitDocument doc)
        {
            // Idling은 매우 자주 발생하므로 그때마다 디스크에서 설정을 다시 읽지 않고, 문서가 바뀐 경우에만
            // 새로 로드한다 (같은 문서에서의 반복 갱신은 캐시된 설정 + 최신 뷰 상태 조회만으로 충분).
            // ReferenceEquals만 믿지 않는다 - Revit API가 같은 열린 문서에 대해 매 호출마다 완전히 같은
            // .NET 래퍼 인스턴스를 돌려준다는 보장을 문서화된 곳에서 확인하지 못했다. 만약 그 가정이
            // 틀리면 매 Idling 틱마다 "문서가 바뀐 것"으로 오판해 ForceReloadSettings가 매번 새
            // QuickToggleSettings(+ 새 Buttons 리스트 참조)를 만들고, 그러면 RefreshState의
            // "리스트 참조가 바뀐 경우에만 RebuildButtons" 판정도 매 틱마다 참이 되어 클릭이 씹히는 원래
            // 버그가 다른 경로로 되살아난다 - 그래서 PathName도 함께 비교해 이중으로 방어한다.
            if (!DocumentsMatch(_cachedDoc, doc))
                ForceReloadSettings(doc);
        }

        private static bool DocumentsMatch(RevitDocument? cached, RevitDocument doc)
        {
            if (cached == null) return false;
            if (ReferenceEquals(cached, doc) || cached.Equals(doc)) return true;
            return !string.IsNullOrEmpty(cached.PathName) && cached.PathName == doc.PathName;
        }

        // ViewActivated/Idling 양쪽에서 호출된다 - 뷰 전환 시 즉시 반영 + 유휴 틱마다 다른 경로로 바뀐
        // 필터/워크셋 상태도 따라잡는다. 창 위치 추적(Revit 창 이동/리사이즈 대응)도 여기서 같이 처리한다.
        public void RefreshState()
        {
            UIDocument? uidoc = _uiapp.ActiveUIDocument;
            if (uidoc?.Document == null)
            {
                HideForNoDocument();
                return;
            }

            RevitDocument doc = uidoc.Document;
            RevitView? view = doc.ActiveView;
            if (view == null)
            {
                Hide();
                return;
            }

            EnsureSettingsLoaded(doc);

            // 버튼 "목록"이 실제로 바뀐 경우(설정 저장/문서 전환)에만 구조를 다시 짓는다 - 그 외의 매우
            // 잦은 Idling 틱에는 이미 있는 Button 인스턴스의 상태만 갱신해서, 클릭 도중 버튼이 통째로
            // 교체되어 클릭이 씹히는 문제를 피한다 (위 클래스 주석 참고).
            if (!ReferenceEquals(_renderedButtons, _cachedSettings.Buttons))
            {
                RebuildButtons(view);
                _renderedButtons = _cachedSettings.Buttons;
                ResizeToContent();
            }
            else
            {
                UpdateButtonStates(view);
            }

            CurrentToolbarVisible = _cachedSettings.ToolbarVisible;

            if (_cachedSettings.ToolbarVisible)
            {
                RepositionToMainWindow();
                if (!IsVisible) Show();
            }
            else
            {
                Hide();
            }
        }

        private void RebuildButtons(RevitView view)
        {
            ButtonsPanel.Children.Clear();
            _rows.Clear();
            _lastStates.Clear();

            foreach (QuickToggleButtonConfig cfg in _cachedSettings.Buttons)
            {
                QuickToggleButtonState state = QuickToggleService.DetermineState(view, cfg);
                _lastStates[cfg.Id] = state;
                (Brush background, Brush borderBrush, Brush foreground) = VisualsFor(state, cfg);

                Canvas icon = QuickToggleIcons.Create(cfg.IconShape ?? QuickToggleIcons.DefaultFor(cfg.Category), foreground);
                // 2026-07-27, "아이콘도 좀 크게 해달라"는 요청 - QuickToggleIcons.Create 자체의 20x16
                // 좌표계는 그대로 두고(다른 도형 10종의 좌표를 전부 다시 계산할 필요 없이), Viewbox로
                // 렌더링 크기만 키운다. 아이콘 재색칠(QuickToggleIcons.SetBrush)은 원본 Canvas 참조를
                // 그대로 쓰므로 이 래핑과 무관하게 계속 동작한다.
                Viewbox iconBox = new Viewbox { Width = 28, Height = 22, Child = icon };
                TextBlock label = new TextBlock
                {
                    Text = cfg.Name,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = foreground,
                    Margin = new Thickness(0, 2, 0, 0),
                };

                // 시각적 여백은 Button 바깥 Margin이 아니라 안쪽 content의 Margin으로만 준다 - 바깥
                // Margin은 히트테스트 영역이 아니라서, 버튼 사이에 클릭이 씹히는 좁은 사각지대가 생겼었다
                // ("버튼이 마우스 커서에 잘 안 잡힌다"는 실측 피드백, 2026-07-27). Button 자체는 옆 버튼과
                // 완전히 맞닿아 틈이 없으므로 그 사각지대가 사라진다.
                StackPanel content = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 2, 8, 2) };
                content.Children.Add(iconBox);
                content.Children.Add(label);

                Button button = new Button
                {
                    Content = content,
                    Background = background,
                    BorderBrush = borderBrush,
                    IsEnabled = state != QuickToggleButtonState.Disabled,
                    ToolTip = ToolTipFor(cfg, state),
                    Tag = cfg,
                };
                button.Click += ToggleButton_Click;
                ButtonsPanel.Children.Add(button);
                _rows.Add((cfg, button, icon, label));
            }
        }

        // RebuildButtons와 달리 Button 인스턴스를 새로 만들지 않고 이미 그려진 것들의 상태만 갱신한다 -
        // 클릭 도중 인스턴스가 교체되지 않게 하기 위한 조치(위 클래스 주석 참고).
        private void UpdateButtonStates(RevitView view)
        {
            foreach ((QuickToggleButtonConfig cfg, Button button, Canvas icon, TextBlock label) in _rows)
            {
                QuickToggleButtonState state = QuickToggleService.DetermineState(view, cfg);

                // 실제로 상태가 바뀐 버튼만 건드린다 - 값이 같아도 매번 IsEnabled/ToolTip을 재대입하면
                // 마우스를 누르고 있는 도중 캡처가 끊겨 클릭이 씹혔다(위 클래스 주석 참고).
                if (_lastStates.TryGetValue(cfg.Id, out QuickToggleButtonState lastState) && lastState == state)
                    continue;
                _lastStates[cfg.Id] = state;

                (Brush background, Brush borderBrush, Brush foreground) = VisualsFor(state, cfg);
                QuickToggleIcons.SetBrush(icon, foreground);
                label.Foreground = foreground;
                button.Background = background;
                button.BorderBrush = borderBrush;
                button.IsEnabled = state != QuickToggleButtonState.Disabled;
                button.ToolTip = ToolTipFor(cfg, state);
            }
        }

        private static string ToolTipFor(QuickToggleButtonConfig cfg, QuickToggleButtonState state) => state switch
        {
            QuickToggleButtonState.On => cfg.Name + " - 클릭하면 끕니다",
            QuickToggleButtonState.Off => cfg.Name + " - 클릭하면 켭니다",
            _ => cfg.Name + " (이 뷰에서 사용할 수 없거나 대상이 지정되지 않았습니다)",
        };

        // 2026-07-27, 사용자 요청으로 확장: "버튼 색상을 설정하면 아이콘만이 아니라 버튼 배경 자체가
        // 다 바뀌고, 아이콘/텍스트는 그 배경색에 대비되어 잘 보이는 색으로 자동 전환"되어야 한다.
        // On 상태일 때만 버튼을 그 색(사용자 지정 또는 기본 Theme.ToggleOn)으로 실제로 채우고
        // (BorderBrush도 맞춰서 하나의 덩어리로 보이게), 아이콘/라벨 전경색은 그 배경의 밝기에 따라
        // QuickToggleIcons.ContrastingForeground가 밝은 색/어두운 색 중 알아서 고른다. Off/Disabled는
        // 기존 그대로 "선 그림"(투명 배경 + 하이라인 테두리) 스타일을 유지한다 - 꺼진 버튼끼리는 서로
        // 다른 색으로 구분할 필요가 없고, 채워진 건 "켜진 것 = 주 버튼"이라는 Industry 테마의 시각
        // 언어(readme의 "주 버튼만 유일하게 채워진 입체 오브젝트")와도 맞는다.
        private static (Brush Background, Brush BorderBrush, Brush Foreground) VisualsFor(QuickToggleButtonState state, QuickToggleButtonConfig cfg)
        {
            if (state == QuickToggleButtonState.On)
            {
                Color color = CustomOnColor(cfg) ?? ((SolidColorBrush)Theme.ToggleOn).Color;
                SolidColorBrush fill = new SolidColorBrush(color);
                fill.Freeze();
                return (fill, fill, QuickToggleIcons.ContrastingForeground(color));
            }
            if (state == QuickToggleButtonState.Off)
                return (Brushes.Transparent, Theme.Border, Theme.TextSecondary);
            return (Brushes.Transparent, Theme.Border, Theme.ToggleDisabled);
        }

        private static Color? CustomOnColor(QuickToggleButtonConfig cfg)
        {
            if (string.IsNullOrEmpty(cfg.OnColorHex)) return null;
            try
            {
                return (Color)ColorConverter.ConvertFromString(cfg.OnColorHex);
            }
            catch
            {
                return null; // 저장된 값이 손상된 경우 공용 색으로 안전하게 대체
            }
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not QuickToggleButtonConfig cfg) return;
            if (App.QuickToggleHandler == null || App.QuickToggleEvent == null) return;

            UIDocument? uidoc = _uiapp.ActiveUIDocument;
            RevitView? view = uidoc?.Document?.ActiveView;
            if (view == null) return;

            bool turnOn = QuickToggleService.DetermineState(view, cfg) != QuickToggleButtonState.On;
            App.QuickToggleHandler.PendingButtonId = cfg.Id;
            App.QuickToggleHandler.PendingTurnOn = turnOn;
            App.QuickToggleEvent.Raise();
        }

        // "뷰 저장" 버튼 - 순수 읽기라 트랜잭션 없이 바로 처리한다. 캡처 로직 자체는
        // QuickToggleService.CaptureViewState로 옮겨(뷰템플릿/필터/작업세트/카테고리 표시/크롭·범위까지
        // 전부 포함 - 2026-07-28 확장) 이 파일은 UI 갱신만 담당한다.
        private void SaveViewButton_Click(object sender, RoutedEventArgs e)
        {
            UIDocument? uidoc = _uiapp.ActiveUIDocument;
            RevitView? view = uidoc?.Document?.ActiveView;
            if (view == null) return;

            _savedViewState = QuickToggleService.CaptureViewState(view);
            UpdateSaveButtonVisual();

            RevertButton.IsEnabled = true;
            RevertButton.Content = new Viewbox { Width = 20, Height = 16, Child = QuickToggleIcons.CreateUndoIcon(Theme.TextSecondary) };
            RevertButton.ToolTip = $"'{view.Name}' 뷰의 저장된 상태로 되돌립니다.";
            SaveViewButton.ToolTip =
                $"'{view.Name}' 뷰의 현재 상태(모델/주석/해석모델/가져온 카테고리 표시, 필터, 작업세트, " +
                "뷰템플릿, 크롭 및 범위)가 저장되어 있습니다. 다시 누르면 갱신합니다.";
        }

        // 2026-07-28, "뷰가 저장이 되고 있는지 전혀 알 수가 없다"는 피드백으로 추가 - 저장된 스냅샷이
        // 있는 동안은 다른 빠른 토글 버튼의 On 상태와 같은 시각 언어(배경 전체를 강조색으로 채우고,
        // 아이콘은 그 배경에 대비되는 색)로 "뷰 저장" 버튼 자체를 표시한다. 이 버튼은 RebuildButtons/
        // UpdateButtonStates가 매 Idling 틱마다 갱신하는 등록형 버튼 목록(_rows)에 속하지 않고 XAML에
        // 고정된 별도 버튼이라, 여기서 직접 호출할 때만(생성 시/저장 클릭 시) 갱신하면 되고 그 사이에
        // 다른 코드가 덮어쓸 일이 없다.
        private void UpdateSaveButtonVisual()
        {
            bool hasSnapshot = _savedViewState != null;
            if (hasSnapshot)
            {
                Color color = ((SolidColorBrush)Theme.ToggleOn).Color;
                SolidColorBrush fill = new SolidColorBrush(color);
                fill.Freeze();
                SaveViewButton.Background = fill;
                SaveViewButton.BorderBrush = fill;
                SaveViewButton.Content = new Viewbox
                {
                    Width = 20,
                    Height = 16,
                    Child = QuickToggleIcons.CreateBookmarkIcon(QuickToggleIcons.ContrastingForeground(color)),
                };
            }
            else
            {
                SaveViewButton.Background = Brushes.Transparent;
                SaveViewButton.BorderBrush = Theme.Border;
                SaveViewButton.Content = new Viewbox
                {
                    Width = 20,
                    Height = 16,
                    Child = QuickToggleIcons.CreateBookmarkIcon(Theme.TextSecondary),
                };
            }
        }

        // "되돌리기" - Revit API 호출(뷰 전환, 뷰템플릿/필터/작업세트 반영)이 필요하므로 다른 버튼들과
        // 같은 ExternalEvent 경로를 그대로 재사용한다(QuickToggleExternalEventHandler.ExecuteRevert 참고).
        private void RevertButton_Click(object sender, RoutedEventArgs e)
        {
            if (_savedViewState == null) return;
            if (App.QuickToggleHandler == null || App.QuickToggleEvent == null) return;

            App.QuickToggleHandler.PendingRevertSnapshot = _savedViewState;
            App.QuickToggleEvent.Raise();
        }

        // 버튼 목록이 바뀔 때만(RebuildButtons 직후) 호출 - 내용에 맞춰 창 너비를 계산한다.
        // 2026-07-27, 사용자 요청으로 최대 너비 제한(가로 스크롤로 처리)을 없앴다 - "등록된 버튼이 많으면
        // 스크롤 대신 툴바 자체가 옆으로 길게 늘어나게 해달라"는 피드백. 이제 버튼이 아무리 많아도 전부
        // 한 줄에 펴서 보여주고, 창 너비는 그만큼 계속 늘어난다(위쪽 XAML의 가로 스크롤 제거와 짝).
        private const double MinWidthDip = 160;
        // XAML의 왼쪽 드래그 그립 열(Border Width)과 반드시 맞춰야 한다 - 2026-07-27, "잡을 수 있는
        // 픽셀이 너무 작아서 잡기 힘들다"는 피드백으로 16→28로 넓혔다.
        private const double GripWidthDip = 28;

        private void ResizeToContent()
        {
            ButtonsPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            RightControlsPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // 2026-07-27, "뷰 저장"/"되돌리기" 고정 버튼 추가 - 우측 패널도 함께 측정해야 창 너비가
            // 그 버튼들을 자르지 않고 전부 담는다.
            double contentWidth = ButtonsPanel.DesiredSize.Width + RightControlsPanel.DesiredSize.Width + 16;
            Width = GripWidthDip + Math.Max(MinWidthDip, contentWidth);
        }

        // 왼쪽 드래그 그립을 누르면 호출된다 - 버튼 영역과 완전히 분리된 전용 영역이라(위 클래스 주석
        // 참고) 버튼 클릭과 절대 헷갈리지 않는다.
        private void DragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();

            // DragMove()는 마우스를 뗄 때까지 블로킹된다 - 놓인 뒤 현재 위치를 Revit 메인 창 기준
            // 오프셋으로 환산해 저장해서, 다음에 메인 창이 움직여도 사용자가 고른 상대 위치를 유지한다.
            if (_cachedDoc == null) return;

            IntPtr mainHandle = _uiapp.MainWindowHandle;
            if (mainHandle == IntPtr.Zero || !NativeMethods.GetWindowRect(mainHandle, out NativeMethods.RECT rect))
                return;

            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null) return;

            Matrix transform = source.CompositionTarget.TransformFromDevice;
            Point mainTopLeftDip = transform.Transform(new Point(rect.Left, rect.Top));

            _cachedSettings.ToolbarOffsetXDip = (int)Math.Round(Left - mainTopLeftDip.X);
            _cachedSettings.ToolbarOffsetYDip = (int)Math.Round(Top - mainTopLeftDip.Y);

            try { _cachedSettings.Save(_cachedDoc); }
            catch { /* 저장 실패해도(예: 문서가 그 사이 닫힘) 이번 세션 위치는 이미 반영되어 있으므로 무시 */ }
        }

        // Revit 메인 창의 위치를 Win32로 읽어 저장된 오프셋만큼 떨어진 곳에 툴바를 따라다니게 한다.
        // 오프셋 자체는 더 이상 고정 상수가 아니라 사용자가 드래그하면 그 즉시 갱신되는 값이다(위 참고).
        //
        // CONFIRMED LIVE BUG (2026-07-27), 수정 — "마우스를 움직이면서 눌러야 겨우 클릭된다": 이 메서드가
        // 바뀐 값이 있든 없든 매번 Left/Top을 새로 대입했는데, 이 메서드를 부르는 RefreshState()는
        // Idling에서도 호출되고 Idling은 정확히 "할 일이 없을 때"(=마우스가 멈춰 있을 때) 가장 자주,
        // 거의 끊임없이 발생한다. Left/Top 대입은 값이 같아도 매번 내부적으로 Win32 SetWindowPos를
        // 유발하는데, 이게 버튼을 누르고 있는 도중 반복되면 ButtonBase의 마우스 캡처가 끊겨 클릭(MouseUp)이
        // 완성되지 못했다 - 마우스를 움직이는 동안은 다른 메시지(WM_MOUSEMOVE)가 큐를 채워 Idling이 상대적
        // 으로 덜 끼어들어서 우연히 클릭이 되곤 했던 것. 고정: 계산된 새 위치가 현재 위치와 실제로 다를
        // 때만(부동소수점 반올림 오차를 감안해 0.5px 이상 차이) Left/Top을 대입한다.
        private const double PositionEpsilonDip = 0.5;

        private void RepositionToMainWindow()
        {
            IntPtr mainHandle = _uiapp.MainWindowHandle;
            if (mainHandle == IntPtr.Zero) return;
            if (!NativeMethods.GetWindowRect(mainHandle, out NativeMethods.RECT rect)) return;

            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null)
            {
                // 창이 아직 화면에 표시되기 전(Show() 호출 전)에는 디바이스 좌표 변환 정보가 없다 -
                // 일단 Show()부터 하고 다음 틱에 다시 맞춘다.
                if (!IsVisible) Show();
                return;
            }

            Matrix transform = source.CompositionTarget.TransformFromDevice;
            Point topLeftDip = transform.Transform(new Point(rect.Left, rect.Top));

            double newLeft = topLeftDip.X + _cachedSettings.ToolbarOffsetXDip;
            double newTop = topLeftDip.Y + _cachedSettings.ToolbarOffsetYDip;

            if (Math.Abs(Left - newLeft) > PositionEpsilonDip) Left = newLeft;
            if (Math.Abs(Top - newTop) > PositionEpsilonDip) Top = newTop;
        }

        private static class NativeMethods
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        }
    }
}
