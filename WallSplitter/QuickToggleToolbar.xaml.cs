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

        public QuickToggleToolbar(UIApplication uiapp)
        {
            InitializeComponent();
            _uiapp = uiapp;
            Instance = this;

            new WindowInteropHelper(this) { Owner = uiapp.MainWindowHandle };
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
            if (!ReferenceEquals(_cachedDoc, doc))
                ForceReloadSettings(doc);
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

            foreach (QuickToggleButtonConfig cfg in _cachedSettings.Buttons)
            {
                QuickToggleButtonState state = QuickToggleService.DetermineState(view, cfg);

                Canvas icon = QuickToggleIcons.Create(cfg.IconShape ?? QuickToggleIcons.DefaultFor(cfg.Category), BrushFor(state, cfg));
                TextBlock label = new TextBlock
                {
                    Text = cfg.Name,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = state == QuickToggleButtonState.Disabled ? Theme.ToggleDisabled : Theme.TextPrimary,
                    Margin = new Thickness(0, 2, 0, 0),
                };

                // 시각적 여백은 Button 바깥 Margin이 아니라 안쪽 content의 Margin으로만 준다 - 바깥
                // Margin은 히트테스트 영역이 아니라서, 버튼 사이에 클릭이 씹히는 좁은 사각지대가 생겼었다
                // ("버튼이 마우스 커서에 잘 안 잡힌다"는 실측 피드백, 2026-07-27). Button 자체는 옆 버튼과
                // 완전히 맞닿아 틈이 없으므로 그 사각지대가 사라진다.
                StackPanel content = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 2, 8, 2) };
                content.Children.Add(icon);
                content.Children.Add(label);

                Button button = new Button
                {
                    Content = content,
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
                QuickToggleIcons.SetBrush(icon, BrushFor(state, cfg));
                label.Foreground = state == QuickToggleButtonState.Disabled ? Theme.ToggleDisabled : Theme.TextPrimary;
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

        // cfg.OnColorHex가 지정돼 있으면(사용자가 버튼마다 직접 고른 색, 2026-07-27 요청으로 추가) On
        // 상태일 때 그 색을 쓰고, 아니면 예전 그대로 공용 Theme.ToggleOn을 쓴다. Off/Disabled는 항상
        // 공용 색을 쓴다 - 꺼진 버튼끼리는 서로 구분할 필요가 없기 때문.
        private static Brush BrushFor(QuickToggleButtonState state, QuickToggleButtonConfig cfg) => state switch
        {
            QuickToggleButtonState.On => CustomOnBrush(cfg) ?? Theme.ToggleOn,
            QuickToggleButtonState.Off => Theme.TextSecondary,
            _ => Theme.ToggleDisabled,
        };

        private static Brush? CustomOnBrush(QuickToggleButtonConfig cfg)
        {
            if (string.IsNullOrEmpty(cfg.OnColorHex)) return null;
            try
            {
                SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cfg.OnColorHex));
                brush.Freeze();
                return brush;
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

        // 버튼 목록이 바뀔 때만(RebuildButtons 직후) 호출 - 내용에 맞춰 창 너비를 계산한다. 예전에는
        // Revit 메인 창 너비에 맞춰 늘렸지만, 이제 자유롭게 옮길 수 있는 패널이 되면서 그럴 이유가 없어져
        // 내용 기준으로만 크기를 잡고(버튼이 많아 넘치면 XAML의 가로 스크롤이 처리) 최대 너비만 둔다.
        // GripWidthDip은 XAML의 왼쪽 드래그 그립 열(Width="16")과 반드시 맞춰야 한다.
        private const double MinWidthDip = 160;
        private const double MaxWidthDip = 480;
        private const double GripWidthDip = 16;

        private void ResizeToContent()
        {
            ButtonsPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double contentWidth = ButtonsPanel.DesiredSize.Width + 16;
            Width = GripWidthDip + Math.Min(MaxWidthDip, Math.Max(MinWidthDip, contentWidth));
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
