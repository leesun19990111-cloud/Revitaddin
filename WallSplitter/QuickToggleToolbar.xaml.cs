using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.UI;
// Autodesk.Revit.DB를 통째로 using하면 Line/Point가 System.Windows.Shapes.Line, System.Windows.Point와
// 충돌한다(둘 다 이 파일에서 필요) - 필요한 두 타입만 별칭으로 가져와 충돌을 피한다.
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

                Canvas icon = CreateCategoryIcon(cfg.Category, BrushFor(state));
                TextBlock label = new TextBlock
                {
                    Text = cfg.Name,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = state == QuickToggleButtonState.Disabled ? Theme.ToggleDisabled : Theme.TextPrimary,
                    Margin = new Thickness(0, 2, 0, 0),
                };

                StackPanel content = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6, 2, 6, 2) };
                content.Children.Add(icon);
                content.Children.Add(label);

                Button button = new Button
                {
                    Content = content,
                    Margin = new Thickness(2, 0, 2, 0),
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
                SetIconBrush(icon, BrushFor(state));
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

        private static Brush BrushFor(QuickToggleButtonState state) => state switch
        {
            QuickToggleButtonState.On => Theme.ToggleOn,
            QuickToggleButtonState.Off => Theme.TextSecondary,
            _ => Theme.ToggleDisabled,
        };

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

        // 카테고리별로 손으로 그린 벡터 아이콘 (SettingsWindow의 CreateTriangle/CreateXMark와 같은 방식 -
        // 텍스트 글리프는 폰트/테마에 따라 안 보일 수 있어 도형으로 직접 그린다).
        private static Canvas CreateCategoryIcon(QuickToggleCategory category, Brush brush)
        {
            Canvas canvas = new Canvas { Width = 20, Height = 16, HorizontalAlignment = HorizontalAlignment.Center };

            switch (category)
            {
                case QuickToggleCategory.ViewTemplate:
                    // 겹쳐진 3개의 가로 막대 - 레이어(뷰템플릿)를 은유
                    for (int i = 0; i < 3; i++)
                    {
                        canvas.Children.Add(new Rectangle
                        {
                            Width = 20 - i * 4,
                            Height = 3,
                            Fill = brush,
                            RadiusX = 1,
                            RadiusY = 1,
                        });
                        Canvas.SetLeft(canvas.Children[i], i * 2);
                        Canvas.SetTop(canvas.Children[i], i * 5.5);
                    }
                    break;

                case QuickToggleCategory.Filter:
                    // 깔때기(필터) 모양
                    Polygon funnel = new Polygon
                    {
                        Points = new PointCollection
                        {
                            new Point(0, 0), new Point(20, 0), new Point(12, 9), new Point(12, 16),
                            new Point(8, 16), new Point(8, 9),
                        },
                        Fill = brush,
                    };
                    canvas.Children.Add(funnel);
                    break;

                case QuickToggleCategory.Workset:
                default:
                    // 3줄 리스트 - 작업세트 묶음을 은유
                    for (int i = 0; i < 3; i++)
                    {
                        canvas.Children.Add(new Line
                        {
                            X1 = 0, Y1 = i * 6 + 2, X2 = 20, Y2 = i * 6 + 2,
                            Stroke = brush,
                            StrokeThickness = 2.5,
                        });
                    }
                    break;
            }

            return canvas;
        }

        // UpdateButtonStates가 상태만 바뀌었을 때 아이콘 색을 다시 칠하기 위한 헬퍼 - CreateCategoryIcon이
        // 만드는 도형 종류(Rectangle/Polygon/Line)에 맞춰 Fill 또는 Stroke를 갱신한다.
        private static void SetIconBrush(Canvas canvas, Brush brush)
        {
            foreach (UIElement child in canvas.Children)
            {
                switch (child)
                {
                    case Rectangle rect: rect.Fill = brush; break;
                    case Polygon poly: poly.Fill = brush; break;
                    case Line line: line.Stroke = brush; break;
                }
            }
        }

        // 버튼 목록이 바뀔 때만(RebuildButtons 직후) 호출 - 내용에 맞춰 창 너비를 계산한다. 예전에는
        // Revit 메인 창 너비에 맞춰 늘렸지만, 이제 자유롭게 옮길 수 있는 패널이 되면서 그럴 이유가 없어져
        // 내용 기준으로만 크기를 잡고(버튼이 많아 넘치면 XAML의 가로 스크롤이 처리) 최대 너비만 둔다.
        private const double MinWidthDip = 160;
        private const double MaxWidthDip = 480;

        private void ResizeToContent()
        {
            ButtonsPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double contentWidth = ButtonsPanel.DesiredSize.Width + 16;
            Width = Math.Min(MaxWidthDip, Math.Max(MinWidthDip, contentWidth));
        }

        // RootBorder의 빈 영역(버튼이 없는 곳)을 누르면 호출된다 - 클릭이 실제로 어떤 Button 위에서
        // 시작됐다면 ButtonBase가 이 라우팅 이벤트를 이미 Handled로 표시해두므로 여기까지 올라오지 않는다.
        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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

            Left = topLeftDip.X + _cachedSettings.ToolbarOffsetXDip;
            Top = topLeftDip.Y + _cachedSettings.ToolbarOffsetYDip;
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
