using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
// Autodesk.Revit.DB에 데이텀 그리드선을 나타내는 Grid 클래스가 있어 System.Windows.Controls.Grid와
// 충돌한다(둘 다 이 파일에서 필요) - WPF 쪽만 별칭으로 구분한다.
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // 리본 "커스텀 버튼 설정" 버튼으로 여는 모달 창 - 커맨드 Execute 안에서 ShowDialog()로 열리므로
    // (SettingsCommand/NamerCommand와 동일 구조) 이미 유효한 API 컨텍스트라 ExternalEvent가 필요 없다.
    //
    // 2026-09-03 UX 전면 개편. 화면 구성 의도와 이전 구조의 문제점은 QuickToggleSettingsWindow.xaml
    // 상단 주석에 정리해 두었다. 코드 쪽에서 특히 지킬 것:
    //  - 아이콘/색을 고를 때 편집 패널 전체를 다시 그리지 말 것. 예전 구현은 스와치를 누를 때마다
    //    BuildEditPanel(cfg)로 통째로 다시 그려서 대상 목록의 검색어와 스크롤 위치가 함께 초기화됐다
    //    (카테고리 트리 펼침/접힘도 같은 문제). 지금은 _refreshAppearanceSection/_refreshEditTitle/
    //    _refreshTargetSummary 세 훅으로 실제로 달라지는 부분만 다시 그린다 - 이 파일과 짝인
    //    QuickToggleToolbar가 "필요 없는데 다시 그려서" 세 번이나 클릭이 씹혔던 것과 같은 부류의 함정이다.
    //  - 색은 전부 Theme/Theme.xaml 토큰에서만 가져올 것(Industry 디자인 시스템: 스틸블루 단일 강조색,
    //    모서리 반지름 0, 채워진 오브젝트는 주 버튼과 강조 뱃지뿐) - docs/design-system/CLAUDE.md 참고.
    public partial class QuickToggleSettingsWindow : Window
    {
        private readonly Document _doc;
        private readonly LanguageType _revitLanguage;
        private readonly QuickToggleSettings _settings;
        private QuickToggleButtonConfig? _selected;

        private readonly List<View> _viewTemplates;
        private readonly List<ParameterFilterElement> _filters;
        private readonly List<Workset> _worksets;
        private readonly bool _isWorkshared;

        // 색상 버튼의 카테고리 트리 - 각 최상위 카테고리를 펼쳤는지 여부(카테고리 Id 기준). 이 창을
        // 새로 열면 초기화되는 세션 한정 UI 상태라 QuickToggleButtonConfig에는 저장하지 않는다.
        private readonly HashSet<int> _expandedCategoryIds = new HashSet<int>();

        // "② 모양"(아이콘/켜짐 색)은 기본으로 접어 두고 세로 공간을 "③ 대상"에 몰아준다 - 2026-07-27의
        // "이름/아이콘/색상 부분이 너무 커서 아래 대상 선택 부분이 작아 보인다"는 피드백을 구조 자체로
        // 해결한 것. "고급"(내보내기/가져오기/툴바 위치)도 같은 이유로 접어 둔다. 둘 다 세션 한정 상태.
        private bool _appearanceExpanded;
        private bool _advancedExpanded;

        // 편집 패널의 부분 갱신 훅 (위 클래스 주석 참고). BuildEditPanel이 매번 새로 채우고,
        // ShowEmptyEditPanel은 전부 null로 비운다.
        private Action? _refreshAppearanceSection;
        private Action? _refreshEditTitle;
        private Action? _refreshTargetSummary;

        // "어떤 버튼을 만들까요?" 카드 목록. 설명 문구는 "이 버튼을 누르면 무슨 일이 일어나는가"를
        // 한 문장으로만 적는다 - 종류를 고르는 순간 필요한 정보는 그것뿐이고, 세부 사항은 고른 뒤
        // 편집 화면의 ③ 대상 안내에서 다시 설명한다.
        private static readonly (QuickToggleCategory Category, string Title, string Description)[] ButtonKinds =
        {
            (QuickToggleCategory.ViewTemplate, "뷰템플릿",
                "지정해 둔 뷰템플릿을 지금 보는 뷰에 씌우고, 다시 누르면 벗깁니다."),
            (QuickToggleCategory.Filter, "필터",
                "지정해 둔 필터들의 표시를 한 번에 켜고 끕니다."),
            (QuickToggleCategory.Workset, "작업세트",
                "지정해 둔 작업세트들의 표시를 한 번에 켜고 끕니다."),
            (QuickToggleCategory.LinkedCad, "링크된 도면",
                "지금 보는 뷰에 링크된 CAD 도면을 한 번에 끄고 켭니다. 미리 고를 대상이 없습니다."),
            (QuickToggleCategory.LinkedModel, "링크된 모델",
                "링크된 Revit 모델 목록을 열어 하나씩 끄고 켭니다. 미리 고를 대상이 없습니다."),
            (QuickToggleCategory.ColorTool, "색상",
                "고른 모델 카테고리의 색과 투명도를 패널에서 즉시 조절합니다."),
            (QuickToggleCategory.CommandLauncher, "기능",
                "재료 지정·NAMER·동기화 같은 기능을 클릭 한 번으로 실행합니다."),
        };

        public QuickToggleSettingsWindow(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            _revitLanguage = doc.Application.Language;
            _settings = QuickToggleSettings.Load(doc);

            _viewTemplates = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate)
                .OrderBy(v => v.Name)
                .ToList();

            _filters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                .OrderBy(f => f.Name)
                .ToList();

            _isWorkshared = doc.IsWorkshared;
            _worksets = _isWorkshared
                ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).OrderBy(w => w.Name).ToList()
                : new List<Workset>();

            AddBlueprintCornerMarks(PreviewCard, new Thickness(12, 10, 12, 10));
            BuildAddChooser();
            UpdateAdvancedSection();
            RefreshButtonList();
            RefreshPreviewStrip();

            if (_settings.Buttons.Count > 0) SelectButton(_settings.Buttons[0]);
            else ShowEmptyEditPanel();
        }

        // ===== 공통: 버튼 한 개의 상태 판정과 시각 =====

        // 버튼 한 개가 지금 어떤 상태인지 - 설정 창이 이걸 알려주지 않으면 "만들었는데 왜 안 눌리지?"가
        // 되기 쉽다(툴바는 그냥 회색으로 그릴 뿐이다).
        private enum ButtonReadiness
        {
            Ready,        // 대상이 지정돼 있고, 지금 열려 있는 프로젝트에서 실제로 찾힌다
            NoTarget,     // 대상을 아직 하나도 안 골랐다
            NotInProject, // 대상은 지정돼 있는데 이 프로젝트에 그 이름이 없다 (전역 설정이라 생기는 새 상태)
        }

        // 설정이 PC 전역이 된 2026-09-03부터는 "대상을 골랐는가"와 "그 대상이 이 프로젝트에 있는가"가
        // 서로 다른 질문이 됐다 - 다른 프로젝트에서 만든 버튼은 대상 이름은 멀쩡히 들고 있지만 지금 문서엔
        // 그 이름이 없을 수 있다. 툴바가 그런 버튼을 회색으로 그리는 이유를 여기서 그대로 설명한다.
        private ButtonReadiness ReadinessOf(QuickToggleButtonConfig cfg)
        {
            switch (cfg.Category)
            {
                case QuickToggleCategory.ViewTemplate:
                    if (cfg.ViewTemplateId == null && string.IsNullOrEmpty(cfg.ViewTemplateName)) return ButtonReadiness.NoTarget;
                    return _viewTemplates.Any(v => v.Name == cfg.ViewTemplateName) || string.IsNullOrEmpty(cfg.ViewTemplateName)
                        ? ButtonReadiness.Ready
                        : ButtonReadiness.NotInProject;

                case QuickToggleCategory.Filter:
                    if (cfg.FilterNames.Count == 0 && cfg.FilterIds.Count == 0) return ButtonReadiness.NoTarget;
                    if (cfg.FilterNames.Count == 0) return ButtonReadiness.Ready; // 이름 없는 옛 설정 - 판정하지 않는다
                    return cfg.FilterNames.Any(n => _filters.Any(f => f.Name == n))
                        ? ButtonReadiness.Ready
                        : ButtonReadiness.NotInProject;

                case QuickToggleCategory.Workset:
                    if (cfg.WorksetNames.Count == 0 && cfg.WorksetIds.Count == 0) return ButtonReadiness.NoTarget;
                    if (!_isWorkshared) return ButtonReadiness.NotInProject;
                    if (cfg.WorksetNames.Count == 0) return ButtonReadiness.Ready;
                    return cfg.WorksetNames.Any(n => _worksets.Any(w => w.Name == n))
                        ? ButtonReadiness.Ready
                        : ButtonReadiness.NotInProject;

                case QuickToggleCategory.ColorTool:
                    return cfg.ColorButtonCategories.Count == 0 ? ButtonReadiness.NoTarget : ButtonReadiness.Ready;

                case QuickToggleCategory.CommandLauncher:
                    return string.IsNullOrEmpty(cfg.CommandId) ? ButtonReadiness.NoTarget : ButtonReadiness.Ready;

                default:
                    // 링크 버튼은 설정에서 고를 대상이 자체가 없다 - 대상은 "그때 활성 뷰에 걸려 있는 링크"다.
                    return ButtonReadiness.Ready;
            }
        }

        private bool IsConfigured(QuickToggleButtonConfig cfg) => ReadinessOf(cfg) == ButtonReadiness.Ready;

        // 왼쪽 목록의 두 번째 줄에 쓰는 한 줄 요약 - 지금 무엇이 걸려 있는지를 목록에서 바로 보여준다.
        private string TargetSummary(QuickToggleButtonConfig cfg)
        {
            if (ReadinessOf(cfg) == ButtonReadiness.NotInProject) return "이 프로젝트에 없음";

            return cfg.Category switch
            {
                QuickToggleCategory.ViewTemplate => string.IsNullOrEmpty(cfg.ViewTemplateName)
                    ? (cfg.ViewTemplateId == null ? "대상 미지정" : "뷰템플릿 1개")
                    : cfg.ViewTemplateName!,
                QuickToggleCategory.Filter => cfg.FilterNames.Count > 0
                    ? cfg.FilterNames.Count + "개 선택"
                    : (cfg.FilterIds.Count == 0 ? "대상 미지정" : cfg.FilterIds.Count + "개 선택"),
                QuickToggleCategory.Workset => cfg.WorksetNames.Count > 0
                    ? cfg.WorksetNames.Count + "개 선택"
                    : (cfg.WorksetIds.Count == 0 ? "대상 미지정" : cfg.WorksetIds.Count + "개 선택"),
                QuickToggleCategory.ColorTool => cfg.ColorButtonCategories.Count == 0
                    ? "대상 미지정"
                    : "카테고리 " + cfg.ColorButtonCategories.Count + "개",
                QuickToggleCategory.CommandLauncher => string.IsNullOrEmpty(cfg.CommandId)
                    ? "기능 미지정"
                    : SunnyToolsCommands.DisplayLabelFor(cfg.CommandKind, cfg.CommandId, _revitLanguage, cfg.CommandLabel),
                _ => "활성 뷰의 링크",
            };
        }

        // 켜짐/꺼짐 개념이 있는 버튼만 "켜짐 색상"이 실제로 화면에 나타난다 - 색상/기능 버튼은
        // QuickToggleService.DetermineState가 항상 Off를 돌려주므로 지정한 색이 쓰일 일이 없다
        // (QuickToggleToolbar.VisualsFor는 On일 때만 그 색으로 채운다).
        private static bool ColorAppliesTo(QuickToggleCategory category) =>
            category != QuickToggleCategory.ColorTool && category != QuickToggleCategory.CommandLauncher;

        // 설정 창에는 활성 뷰가 없어 진짜 on/off를 알 수 없다 - 켜고 끌 수 있는 버튼은 "켜졌을 때"의
        // 모습으로(사용자가 고른 색을 확인하는 게 미리보기의 목적), on/off 개념이 없는 색상·기능
        // 버튼은 툴바에서 늘 그렇듯 꺼진 모습으로, 대상을 안 고른 버튼은 회색(Disabled)으로 그린다.
        // 색 조합 자체는 QuickToggleToolbar.VisualsFor와 같은 규칙이다.
        private (Brush Background, Brush BorderBrush, Brush Foreground) PreviewVisualsFor(QuickToggleButtonConfig cfg)
        {
            if (!IsConfigured(cfg))
                return (Brushes.Transparent, Theme.Border, Theme.ToggleDisabled);
            if (!ColorAppliesTo(cfg.Category))
                return (Brushes.Transparent, Theme.Border, Theme.TextSecondary);

            SolidColorBrush fill = OnColorBrush(cfg);
            return (fill, fill, QuickToggleIcons.ContrastingForeground(fill.Color));
        }

        private static SolidColorBrush OnColorBrush(QuickToggleButtonConfig cfg)
        {
            if (!string.IsNullOrEmpty(cfg.OnColorHex))
            {
                try
                {
                    SolidColorBrush brush = new SolidColorBrush(
                        (System.Windows.Media.Color)ColorConverter.ConvertFromString(cfg.OnColorHex));
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                    // 저장된 값이 손상된 경우 공용 색으로 안전하게 대체 (툴바의 CustomOnColor와 같은 방침).
                }
            }
            return Theme.ToggleOn;
        }

        private static Canvas IconOf(QuickToggleButtonConfig cfg, Brush brush) =>
            QuickToggleIcons.Create(cfg.IconShape ?? QuickToggleIcons.DefaultFor(cfg.Category), brush);

        // ===== 상단: 툴바 미리보기 =====

        // 툴바에서 "작은 도구형 버튼"으로 그려지는 종류 - QuickToggleToolbar.IsSmallToolButton과 맞춰서 유지.
        private static bool IsSmallToolButton(QuickToggleCategory category) =>
            category == QuickToggleCategory.ColorTool;

        // 색상 버튼 묶음의 높이(대략 작은 버튼 2개) - QuickToggleToolbar.SmallToolGroupHeightDip과 같은 값.
        private const double SmallToolGroupHeightDip = 64;

        // 실제 툴바 창(QuickToggleToolbar.xaml)의 구조를 통째로 흉내 낸다:
        //   [드래그 그립 28px] [ButtonsPanel] [구분선 + 뷰 저장 + 되돌리기]
        // 전부 Surface 배경 + 하이라인 테두리의 Border 하나에 담겨 있다.
        //
        // **왜 이렇게까지 하나**: 처음엔 등록 버튼만 한 줄로 나열했는데, 실제 툴바는 색상 버튼을 세로
        // WrapPanel(2단)로 묶고 그 앞에 구분선까지 넣기 때문에 "미리보기가 실제와 다르다"는 지적을 받았다.
        // 미리보기가 실제와 다르면 있으나 마나이므로, 배치 규칙을 그대로 옮겨 그린다 -
        // **QuickToggleToolbar.RebuildButtons/ResizeToContent 쪽 배치를 바꾸면 이 메서드도 같이 맞출 것.**
        private void RefreshPreviewStrip()
        {
            PreviewPanel.Children.Clear();

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };

            if (_settings.Buttons.Count == 0)
            {
                buttons.Children.Add(new TextBlock
                {
                    Text = "아직 버튼이 없습니다. 왼쪽 아래 '+ 버튼 추가'로 시작하세요.",
                    Foreground = Theme.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 14, 4, 14),
                });
            }
            else
            {
                WrapPanel? smallToolGroup = null;
                foreach (QuickToggleButtonConfig cfg in _settings.Buttons)
                {
                    UIElement item = CreatePreviewButton(cfg);
                    if (!IsSmallToolButton(cfg.Category))
                    {
                        buttons.Children.Add(item);
                        continue;
                    }

                    if (smallToolGroup == null)
                    {
                        if (buttons.Children.Count > 0)
                            buttons.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 2, 4, 2), Background = Theme.Border });
                        smallToolGroup = new WrapPanel { Orientation = Orientation.Vertical, Height = SmallToolGroupHeightDip };
                        buttons.Children.Add(smallToolGroup);
                    }
                    smallToolGroup.Children.Add(item);
                }
            }

            PreviewPanel.Children.Add(CreateToolbarFrame(buttons));
        }

        // 툴바 창 전체(테두리 + 그립 + 버튼 영역 + 우측 고정 버튼)를 감싸 그린다.
        private UIElement CreateToolbarFrame(UIElement buttons)
        {
            WpfGrid grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 드래그 그립(점 3개, 28px) - 실제 툴바에서 창을 옮기는 손잡이.
            StackPanel dots = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 3; i++)
                dots.Children.Add(new Border { Width = 4, Height = 4, Background = Theme.TextSecondary, Margin = new Thickness(0, 2.5, 0, 2.5) });
            Border grip = new Border
            {
                Width = 28,
                Child = dots,
                ToolTip = "실제 툴바에서는 여기를 잡고 드래그해 위치를 옮깁니다.",
            };
            WpfGrid.SetColumn(grip, 0);
            grid.Children.Add(grip);

            WpfGrid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            // 우측에 항상 고정으로 붙는 "뷰 저장"/"되돌리기" - 등록 버튼 목록과 무관하게 늘 있으므로
            // 미리보기에도 포함해야 실제 폭·모양이 맞는다. 아직 저장한 상태가 없는 시작 시점의 모습으로 그린다.
            StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 4, 4) };
            right.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 2, 4, 2), Background = Theme.Border });
            right.Children.Add(new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(4),
                IsHitTestVisible = false,
                ToolTip = "현재 뷰의 표시 상태를 저장합니다 (툴바에 항상 붙어 있는 고정 버튼).",
                Content = new Viewbox { Width = 20, Height = 16, Child = QuickToggleIcons.CreateBookmarkIcon(Theme.TextSecondary) },
            });
            right.Children.Add(new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(4),
                Margin = new Thickness(2, 0, 0, 0),
                IsEnabled = false,
                ToolTip = "저장해 둔 뷰 상태로 되돌립니다 (툴바에 항상 붙어 있는 고정 버튼).",
                Content = new Viewbox { Width = 20, Height = 16, Child = QuickToggleIcons.CreateUndoIcon(Theme.ToggleDisabled) },
            });
            WpfGrid.SetColumn(right, 2);
            grid.Children.Add(right);

            return new Border
            {
                Background = Theme.Surface,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = grid,
            };
        }

        // 실제 QuickToggleToolbar.RebuildButtons와 같은 배치 규칙으로 그린다 - 색상 버튼만 "작은 리본
        // 버튼"(작은 아이콘이 왼쪽, 두 줄까지 줄바꿈되는 라벨이 오른쪽) 가로 배치이고 나머지는 "큰 아이콘
        // 위 + 라벨 아래" 세로 배치다. 두 파일의 수치가 어긋나면 미리보기가 거짓말을 하게 되므로,
        // 툴바 쪽 배치를 바꿀 때 이 메서드도 같이 맞출 것.
        private UIElement CreatePreviewButton(QuickToggleButtonConfig cfg)
        {
            (Brush background, Brush borderBrush, Brush foreground) = PreviewVisualsFor(cfg);
            Canvas icon = IconOf(cfg, foreground);
            UIElement content;

            if (cfg.Category == QuickToggleCategory.ColorTool)
            {
                StackPanel horizontal = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                horizontal.Children.Add(new Viewbox { Width = 16, Height = 13, Child = icon, VerticalAlignment = VerticalAlignment.Center });
                horizontal.Children.Add(new TextBlock
                {
                    Text = cfg.Name,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Width = 46,
                    Foreground = foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                });
                content = horizontal;
            }
            else
            {
                StackPanel vertical = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 2, 8, 2) };
                vertical.Children.Add(new Viewbox { Width = 28, Height = 22, Child = icon });
                vertical.Children.Add(new TextBlock
                {
                    Text = cfg.Name,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = foreground,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                content = vertical;
            }

            Button button = new Button
            {
                Content = content,
                Background = background,
                BorderBrush = borderBrush,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = CategoryLabel(cfg.Category) + " 버튼 · " + TargetSummary(cfg) + "\n클릭하면 이 버튼을 편집합니다.",
            };
            button.Click += (s, e) => SelectButton(cfg);

            // 지금 편집 중인 버튼을 강조색 테두리로 표시한다 - 미리보기와 오른쪽 편집 영역이 같은 버튼을
            // 가리키고 있다는 걸 눈으로 잇기 위함(테두리만 두르므로 버튼 자신의 색은 가리지 않는다).
            // 실제 툴바는 버튼끼리 완전히 맞닿아 있으므로(클릭 사각지대를 없애려고 일부러 그렇게 했다)
            // 바깥 Margin은 주지 않는다 - 선택 표시용 2px 테두리만 모든 버튼에 똑같이 둘러 간격을 맞춘다.
            return new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = ReferenceEquals(cfg, _selected) ? Theme.Accent : Brushes.Transparent,
                Child = button,
            };
        }

        // Industry 디자인 시스템의 .blueprint 모서리 등록 마크("+"). 2026-07-27 테마 이식 때는 "창마다
        // 레이아웃을 건드려야 한다"는 이유로 옮기지 않았던 장식인데, 이번엔 이 창을 다시 짜는 김에 가장
        // 대표적인 카드(툴바 미리보기) 한 곳에만 넣어 브랜드 톤을 살렸다 - 남용하면 설계도면 은유가
        // 오히려 옅어지므로 다른 카드에는 넣지 않는다.
        //
        // padding에는 이 Grid를 감싼 Border의 Padding을 그대로 넘긴다 - 그만큼 음수 마진으로 되밀어야
        // 마크가 카드 안쪽 내용 위에 겹치지 않고 테두리의 네 모서리에 정확히 얹힌다(첫 구현은 음수 마진이
        // 없어서 "툴바 미리보기" 글자와 첫 버튼 위에 마크가 겹쳐 그려졌다 - 렌더링해서 확인 후 수정).
        private static void AddBlueprintCornerMarks(WpfGrid host, Thickness padding)
        {
            const double size = 7;
            const double half = size / 2;

            (HorizontalAlignment H, VerticalAlignment V, Thickness M)[] corners =
            {
                (HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-(padding.Left + half), -(padding.Top + half), 0, 0)),
                (HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, -(padding.Top + half), -(padding.Right + half), 0)),
                (HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(-(padding.Left + half), 0, 0, -(padding.Bottom + half))),
                (HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, -(padding.Right + half), -(padding.Bottom + half))),
            };

            foreach ((HorizontalAlignment h, VerticalAlignment v, Thickness m) in corners)
            {
                Canvas mark = new Canvas
                {
                    Width = size,
                    Height = size,
                    HorizontalAlignment = h,
                    VerticalAlignment = v,
                    Margin = m,
                    IsHitTestVisible = false,
                };
                mark.Children.Add(new System.Windows.Shapes.Line { X1 = 0, Y1 = 3.5, X2 = 7, Y2 = 3.5, Stroke = Theme.Border, StrokeThickness = 1 });
                mark.Children.Add(new System.Windows.Shapes.Line { X1 = 3.5, Y1 = 0, X2 = 3.5, Y2 = 7, Stroke = Theme.Border, StrokeThickness = 1 });
                host.Children.Add(mark);
            }
        }

        // ===== 왼쪽: 등록된 버튼 목록 =====

        private void RefreshButtonList()
        {
            ButtonListPanel.Children.Clear();

            if (_settings.Buttons.Count == 0)
            {
                ButtonListPanel.Children.Add(new TextBlock
                {
                    Text = "아직 버튼이 없습니다.\n아래 '+ 버튼 추가'를 누르세요.",
                    Foreground = Theme.TextSecondary,
                    Margin = new Thickness(8),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            // 2026-09-03, "색상버튼은 색상버튼끼리, 작업세트버튼은 작업세트끼리 묶여서 함께 이동할 수도
            // 있고, 연결고리버튼을 눌러 고리를 끊으면 개별로도 움직일 수 있게" 요청으로 목록을 종류별
            // 덩어리(run) 단위로 그린다. run = 같은 종류가 연속으로 이어지는 구간이다. 묶여 있는 종류는
            // QuickToggleSettings.NormalizeGrouping()이 반드시 하나의 run으로 모아 두므로 "그룹 전체
            // 이동"이 성립하고, 고리를 끊은 종류는 여기저기 흩어질 수 있어 run이 여러 개 생길 수 있다
            // (그때는 각 run이 자기 머리글을 갖는다 - 지금 어디에 놓여 있는지를 그대로 보여주는 게 낫다).
            List<(int Start, int Count, QuickToggleCategory Category)> runs = BuildRuns();

            for (int r = 0; r < runs.Count; r++)
            {
                (int runStart, int runCount, QuickToggleCategory runCategory) = runs[r];
                ButtonListPanel.Children.Add(BuildGroupHeader(runs, r));

                for (int i = runStart; i < runStart + runCount; i++)
                    ButtonListPanel.Children.Add(BuildButtonRow(i, runStart, runCount));
            }
        }

        private List<(int Start, int Count, QuickToggleCategory Category)> BuildRuns()
        {
            List<(int Start, int Count, QuickToggleCategory Category)> runs = new List<(int, int, QuickToggleCategory)>();
            int i = 0;
            while (i < _settings.Buttons.Count)
            {
                QuickToggleCategory category = _settings.Buttons[i].Category;
                int start = i;
                while (i < _settings.Buttons.Count && _settings.Buttons[i].Category == category) i++;
                runs.Add((start, i - start, category));
            }
            return runs;
        }

        // 종류 덩어리의 머리글 - 종류 이름 + 개수 + 연결고리 토글 + (묶여 있을 때만) 그룹 통째 이동.
        private UIElement BuildGroupHeader(List<(int Start, int Count, QuickToggleCategory Category)> runs, int runIndex)
        {
            (int start, int count, QuickToggleCategory category) = runs[runIndex];
            bool linked = _settings.IsLinked(category);

            Border header = new Border
            {
                Background = Theme.WindowBackground,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6, 4, 6, 4),
            };

            WpfGrid grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Child = grid;

            TextBlock title = new TextBlock
            {
                Text = CategoryLabel(category) + " (" + count + ")",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = linked ? Theme.TextPrimary : Theme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            WpfGrid.SetColumn(title, 0);
            grid.Children.Add(title);

            Button chain = new Button
            {
                Content = CreateChainGlyph(linked),
                Width = 24,
                Height = 24,
                Padding = new Thickness(2),
                Background = Theme.Surface,
                Margin = new Thickness(4, 0, 6, 0),
                ToolTip = linked
                    ? "이 종류의 버튼들이 묶여 있어 함께 움직입니다. 누르면 고리를 끊어 하나씩 옮길 수 있습니다."
                    : "고리가 끊겨 있어 버튼을 하나씩 옮길 수 있습니다. 누르면 다시 묶어 한 덩어리로 모읍니다.",
            };
            chain.Click += (s, e) =>
            {
                _settings.SetLinked(category, !linked);
                RefreshButtonList();
                RefreshPreviewStrip();
            };
            WpfGrid.SetColumn(chain, 1);
            grid.Children.Add(chain);

            StackPanel groupMove = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (linked)
            {
                Button groupUp = new Button
                {
                    Content = CreateTriangle(pointingUp: true), Width = 24, Height = 24, Padding = new Thickness(2),
                    Background = Theme.Surface, Margin = new Thickness(0, 0, 2, 0),
                    IsEnabled = runIndex > 0, ToolTip = "이 종류를 통째로 위로 옮기기",
                };
                groupUp.Click += (s, e) => MoveRun(runIndex, -1);
                groupMove.Children.Add(groupUp);

                Button groupDown = new Button
                {
                    Content = CreateTriangle(pointingUp: false), Width = 24, Height = 24, Padding = new Thickness(2),
                    Background = Theme.Surface,
                    IsEnabled = runIndex < runs.Count - 1, ToolTip = "이 종류를 통째로 아래로 옮기기",
                };
                groupDown.Click += (s, e) => MoveRun(runIndex, +1);
                groupMove.Children.Add(groupDown);
            }
            else
            {
                groupMove.Children.Add(new TextBlock
                {
                    Text = "풀림",
                    FontSize = 11,
                    Foreground = Theme.WarningText,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                });
            }
            WpfGrid.SetColumn(groupMove, 2);
            grid.Children.Add(groupMove);

            return header;
        }

        // 묶여 있는 종류의 개별 ▲▼는 그 덩어리 안에서만 움직이고(밖으로 나가면 묶음이 깨지므로),
        // 고리가 끊긴 종류는 목록 전체를 한 칸씩 넘나든다.
        private UIElement BuildButtonRow(int index, int runStart, int runCount)
        {
            {
                QuickToggleButtonConfig cfg = _settings.Buttons[index];
                bool isSelected = ReferenceEquals(cfg, _selected);
                bool configured = IsConfigured(cfg);
                bool linked = _settings.IsLinked(cfg.Category);
                int lowerBound = linked ? runStart : 0;
                int upperBound = linked ? runStart + runCount - 1 : _settings.Buttons.Count - 1;

                Border row = new Border
                {
                    Background = isSelected ? Theme.SelectionHighlight : Brushes.Transparent,
                    BorderBrush = Theme.Border,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(14, 6, 6, 6),
                    Cursor = Cursors.Hand,
                };
                // 행 전체가 선택 영역이다 - 예전엔 이름 텍스트 부분만 클릭 대상이라 옆 여백을 눌러도
                // 아무 일이 없었다. 위/아래/삭제 Button은 자기 자신이 MouseLeftButtonDown을 처리하므로
                // 여기까지 이벤트가 올라오지 않는다.
                row.MouseLeftButtonDown += (s, e) => SelectButton(cfg);

                WpfGrid grid = new WpfGrid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Child = grid;

                // 툴바에서 실제로 어떤 아이콘/색으로 보일지를 목록에서도 바로 알 수 있게 작은 칩으로 그린다.
                (Brush chipBackground, Brush chipBorder, Brush chipForeground) = PreviewVisualsFor(cfg);
                Border iconChip = new Border
                {
                    Width = 28,
                    Height = 28,
                    Background = chipBackground,
                    BorderBrush = chipBorder,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new Viewbox { Width = 16, Height = 13, Child = IconOf(cfg, chipForeground) },
                };
                WpfGrid.SetColumn(iconChip, 0);
                grid.Children.Add(iconChip);

                StackPanel info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(cfg.Name) ? "(이름 없음)" : cfg.Name,
                    FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                // 종류는 바로 위 그룹 머리글에 이미 적혀 있으므로 여기서는 대상 요약만 보여준다.
                info.Children.Add(new TextBlock
                {
                    Text = TargetSummary(cfg),
                    FontSize = 11,
                    Foreground = configured ? Theme.TextSecondary : Theme.WarningText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                WpfGrid.SetColumn(info, 1);
                grid.Children.Add(info);

                StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

                // CONFIRMED LIVE BUG (2026-07-27), 수정 (1차): 위/아래 버튼이 "투명하게 보여 뭐가 위/아래인지
                // 안 보인다"는 실측 피드백 - Industry 라이트 테마에서 기본 버튼 배경이 투명("선 그림")이라
                // 22px짜리 작은 아이콘 버튼은 존재감이 약했다. 배경을 명시적으로 Surface로 채운다.
                // CONFIRMED LIVE BUG (2026-07-27), 수정 (2차, 진짜 원인): Width/Height만 24로 키우고
                // Padding은 그대로 뒀던 게 문제였다 - BaseButtonStyle의 기본 Padding("10,5")이 그대로
                // 적용되면 24x24 버튼의 콘텐츠 영역이 4x14로 쪼그라들어 도형이 대부분 잘렸다.
                // 세 버튼 모두 Padding을 작게 명시해야 한다.
                string moveScope = linked ? " (이 종류 안에서)" : "";
                Button upButton = new Button { Content = CreateTriangle(pointingUp: true), Width = 24, Height = 24, Padding = new Thickness(2), Background = Theme.Surface, Margin = new Thickness(0, 0, 2, 0), IsEnabled = index > lowerBound, ToolTip = "위로 옮기기" + moveScope };
                upButton.Click += (s, e) => { if (linked) MoveButton(index, index - 1); else MoveLooseButton(index, -1); };
                actions.Children.Add(upButton);

                Button downButton = new Button { Content = CreateTriangle(pointingUp: false), Width = 24, Height = 24, Padding = new Thickness(2), Background = Theme.Surface, Margin = new Thickness(0, 0, 6, 0), IsEnabled = index < upperBound, ToolTip = "아래로 옮기기" + moveScope };
                downButton.Click += (s, e) => { if (linked) MoveButton(index, index + 1); else MoveLooseButton(index, +1); };
                actions.Children.Add(downButton);

                Button deleteButton = new Button { Content = CreateXMark(), Width = 24, Height = 24, Padding = new Thickness(2), Background = Theme.Surface, ToolTip = "이 버튼 삭제" };
                deleteButton.Click += (s, e) => { DeleteButton(index); };
                actions.Children.Add(deleteButton);

                WpfGrid.SetColumn(actions, 2);
                grid.Children.Add(actions);

                return row;
            }
        }

        private static string CategoryLabel(QuickToggleCategory category) => category switch
        {
            QuickToggleCategory.ViewTemplate => "뷰템플릿",
            QuickToggleCategory.Filter => "필터",
            QuickToggleCategory.Workset => "작업세트",
            QuickToggleCategory.ColorTool => "색상",
            QuickToggleCategory.CommandLauncher => "기능",
            QuickToggleCategory.LinkedCad => "링크된 도면",
            QuickToggleCategory.LinkedModel => "링크된 모델",
            _ => "",
        };

        private void MoveButton(int index, int newIndex)
        {
            if (newIndex < 0 || newIndex >= _settings.Buttons.Count) return;
            (_settings.Buttons[index], _settings.Buttons[newIndex]) = (_settings.Buttons[newIndex], _settings.Buttons[index]);
            RefreshButtonList();
            RefreshPreviewStrip();
        }

        // 고리가 끊긴 버튼 하나를 목록 전체에서 옮긴다. 한 칸씩 움직이되 **묶여 있는 종류 덩어리는 통째로
        // 건너뛴다** - 그러지 않으면 묶인 덩어리 한가운데로 끼어들어가 "묶인 종류는 항상 한 덩어리"라는
        // 전제가 깨진다. (한 칸씩 옮긴 뒤 NormalizeGrouping으로 정리하는 방법도 생각했지만, 그러면 묶인
        // 덩어리 위로는 영원히 못 올라간다 - 정리 과정이 그 이동을 그대로 되돌려 놓기 때문이다.)
        private void MoveLooseButton(int index, int direction)
        {
            if (index < 0 || index >= _settings.Buttons.Count) return;

            List<QuickToggleButtonConfig> list = _settings.Buttons;
            QuickToggleButtonConfig cfg = list[index];
            list.RemoveAt(index);

            int target;
            if (direction < 0)
            {
                int j = index - 1;
                if (j >= 0 && _settings.IsLinked(list[j].Category))
                {
                    QuickToggleCategory blocking = list[j].Category;
                    while (j >= 0 && list[j].Category == blocking) j--;
                    target = j + 1;
                }
                else target = index - 1;
            }
            else
            {
                // 위에서 자기 자신을 뺐으므로, 원래 "바로 아래" 항목이 지금 index 자리에 있다.
                int j = index;
                if (j < list.Count && _settings.IsLinked(list[j].Category))
                {
                    QuickToggleCategory blocking = list[j].Category;
                    while (j < list.Count && list[j].Category == blocking) j++;
                    target = j;
                }
                else target = index + 1;
            }

            list.Insert(Math.Max(0, Math.Min(list.Count, target)), cfg);
            RefreshButtonList();
            RefreshPreviewStrip();
        }

        // 묶여 있는 종류 덩어리를 통째로 이웃 덩어리와 자리바꿈한다 - 한 칸씩 미는 게 아니라 두 구간을
        // 통으로 맞바꾸므로, 덩어리 안의 순서는 그대로 유지된다.
        private void MoveRun(int runIndex, int direction)
        {
            List<(int Start, int Count, QuickToggleCategory Category)> runs = BuildRuns();
            int otherIndex = runIndex + direction;
            if (runIndex < 0 || runIndex >= runs.Count || otherIndex < 0 || otherIndex >= runs.Count) return;

            (int firstStart, int firstCount, _) = runs[Math.Min(runIndex, otherIndex)];
            (int secondStart, int secondCount, _) = runs[Math.Max(runIndex, otherIndex)];

            List<QuickToggleButtonConfig> first = _settings.Buttons.GetRange(firstStart, firstCount);
            List<QuickToggleButtonConfig> second = _settings.Buttons.GetRange(secondStart, secondCount);

            _settings.Buttons.RemoveRange(firstStart, firstCount + secondCount);
            _settings.Buttons.InsertRange(firstStart, second);
            _settings.Buttons.InsertRange(firstStart + secondCount, first);

            RefreshButtonList();
            RefreshPreviewStrip();
        }

        // 연결고리 아이콘 - 고리 두 개가 걸려 있으면(묶임) 서로 겹치고, 끊기면 사이가 벌어진다.
        // 텍스트 글리프(⛓) 대신 도형으로 직접 그리는 이 창의 관례를 따른다(폰트에 따라 안 보일 수 있음).
        // Autodesk.Revit.DB에도 Ellipse가 있어 완전한 이름으로 구분한다(이 파일의 Grid/Line과 같은 종류의 충돌).
        private static UIElement CreateChainGlyph(bool linked)
        {
            Canvas canvas = new Canvas { Width = 18, Height = 12 };
            Brush stroke = linked ? Theme.TextPrimary : Theme.WarningText;
            double gap = linked ? 0 : 2.5;

            System.Windows.Shapes.Ellipse left = new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 8, Stroke = stroke, StrokeThickness = 1.6, Fill = Brushes.Transparent,
            };
            Canvas.SetLeft(left, 0 - gap);
            Canvas.SetTop(left, 2);
            canvas.Children.Add(left);

            System.Windows.Shapes.Ellipse right = new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 8, Stroke = stroke, StrokeThickness = 1.6, Fill = Brushes.Transparent,
            };
            Canvas.SetLeft(right, 7 + gap);
            Canvas.SetTop(right, 2);
            canvas.Children.Add(right);

            if (!linked)
            {
                // 끊긴 상태는 벌어진 틈만으로는 잘 안 읽혀서, 가운데에 짧은 사선을 하나 그어 확실히 구분한다.
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 11, Y1 = 0, X2 = 7, Y2 = 12, Stroke = Theme.WarningText, StrokeThickness = 1.6,
                });
            }

            return canvas;
        }

        private void DeleteButton(int index)
        {
            QuickToggleButtonConfig removed = _settings.Buttons[index];
            _settings.Buttons.RemoveAt(index);

            if (ReferenceEquals(_selected, removed))
                _selected = _settings.Buttons.Count > 0 ? _settings.Buttons[0] : null;

            RefreshButtonList();
            RefreshPreviewStrip();
            if (_selected != null) BuildEditPanel(_selected);
            else ShowEmptyEditPanel();
        }

        // ===== "어떤 버튼을 만들까요?" 오버레이 =====

        private void BuildAddChooser()
        {
            AddChooserPanel.Children.Clear();

            foreach ((QuickToggleCategory category, string title, string description) in ButtonKinds)
            {
                QuickToggleCategory captured = category;
                bool enabled = category != QuickToggleCategory.Workset || _isWorkshared;

                StackPanel head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                head.Children.Add(new Viewbox
                {
                    Width = 20,
                    Height = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = QuickToggleIcons.Create(QuickToggleIcons.DefaultFor(category), Theme.Accent),
                });
                head.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });

                StackPanel content = new StackPanel();
                content.Children.Add(head);
                content.Children.Add(new TextBlock
                {
                    Text = description,
                    Foreground = Theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                });

                Button card = new Button
                {
                    Content = content,
                    Width = 232,
                    Height = 112,
                    Margin = new Thickness(0, 0, 10, 10),
                    Padding = new Thickness(12, 10, 12, 10),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    Cursor = Cursors.Hand,
                    IsEnabled = enabled,
                    ToolTip = enabled ? null : "이 문서는 작업공유(워크셰어링)가 설정되어 있지 않아 작업세트 버튼을 추가할 수 없습니다.",
                };
                card.Click += (s, e) => { HideAddOverlay(); AddButtonOfCategory(captured); };

                AddChooserPanel.Children.Add(card);
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e) => ShowAddOverlay();

        private void CloseAddOverlayButton_Click(object sender, RoutedEventArgs e) => HideAddOverlay();

        private void ShowAddOverlay() => AddOverlay.Visibility = System.Windows.Visibility.Visible;

        private void HideAddOverlay() => AddOverlay.Visibility = System.Windows.Visibility.Collapsed;

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 오버레이가 떠 있을 때 Esc는 창 전체가 아니라 오버레이만 닫는다 - 그냥 두면 "취소"
            // 버튼의 IsCancel이 먼저 걸려 편집하던 창이 통째로 닫혀버린다.
            if (e.Key == Key.Escape && AddOverlay.Visibility == System.Windows.Visibility.Visible)
            {
                HideAddOverlay();
                e.Handled = true;
            }
        }

        private void AddButtonOfCategory(QuickToggleCategory category)
        {
            QuickToggleButtonConfig cfg = new QuickToggleButtonConfig
            {
                Category = category,
                Name = _settings.NextDefaultName(category),
            };
            _settings.Buttons.Add(cfg);
            // 묶여 있는 종류라면 목록 끝이 아니라 그 종류 덩어리의 끝으로 들어가야 한다.
            _settings.NormalizeGrouping();
            SelectButton(cfg);
        }

        // ===== 오른쪽: 선택된 버튼의 편집 영역 =====

        private void SelectButton(QuickToggleButtonConfig cfg)
        {
            _selected = cfg;
            RefreshButtonList();
            RefreshPreviewStrip();
            BuildEditPanel(cfg);
        }

        private void ShowEmptyEditPanel()
        {
            _refreshAppearanceSection = null;
            _refreshEditTitle = null;
            _refreshTargetSummary = null;
            EditHeaderHost.Children.Clear();
            EditPanelHost.Children.Clear();

            StackPanel empty = new StackPanel { Margin = new Thickness(0, 60, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new TextBlock
            {
                Text = "아직 만든 버튼이 없습니다",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            empty.Children.Add(new TextBlock
            {
                Text = "커스텀 버튼바에 넣을 버튼을 만들어 보세요.\n뷰템플릿·필터·작업세트를 한 번에 켜고 끄거나, 링크를 끄고 켜거나, 자주 쓰는 기능을 바로 실행할 수 있습니다.",
                Foreground = Theme.TextSecondary,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                Margin = new Thickness(0, 6, 0, 14),
            });
            Button add = new Button
            {
                Content = "+ 버튼 추가",
                Padding = new Thickness(18, 7, 18, 7),
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)FindResource("PrimaryButtonStyle"),
            };
            add.Click += (s, e) => ShowAddOverlay();
            empty.Children.Add(add);

            EditPanelHost.Children.Add(empty);
        }

        // 제목/이름/모양/대상 헤더는 EditHeaderHost(고정, 스크롤 안 됨)에, 실제 대상 목록만
        // EditPanelHost(스크롤됨)에 넣는다 - 목록이 길어져도 이름 칸과 검색창이 항상 보이게 하기 위함
        // (2026-07-27 실측 피드백: "스크롤을 내리면 이름 변경 부분이 같이 사라진다"). 2026-09-03 개편에서
        // 검색 입력칸도 스크롤 영역에서 고정 영역으로 올렸다 - 목록을 내리는 동안에도 검색어를 고칠 수 있다.
        private void BuildEditPanel(QuickToggleButtonConfig cfg)
        {
            EditHeaderHost.Children.Clear();
            EditPanelHost.Children.Clear();
            _refreshAppearanceSection = null;
            _refreshTargetSummary = null;

            EditHeaderHost.Children.Add(BuildEditTitleRow(cfg));
            EditHeaderHost.Children.Add(CreateDivider());

            // --- ① 이름 ---
            EditHeaderHost.Children.Add(CreateStepHeader(1, "이름", "커스텀 버튼바에 표시될 이름입니다."));
            TextBox nameBox = new TextBox
            {
                Text = cfg.Name,
                MinWidth = 240,
                MaxWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(26, 0, 0, 12),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            nameBox.TextChanged += (s, e) =>
            {
                cfg.Name = nameBox.Text;
                _refreshEditTitle?.Invoke();
                RefreshButtonList();
                RefreshPreviewStrip();
            };
            EditHeaderHost.Children.Add(nameBox);

            // --- ② 모양 ---
            EditHeaderHost.Children.Add(BuildAppearanceSection(cfg));

            // --- ③ 대상 ---
            EditHeaderHost.Children.Add(CreateDivider());
            EditHeaderHost.Children.Add(CreateStepHeader(3, StepThreeTitle(cfg.Category), StepThreeHint(cfg.Category)));

            switch (cfg.Category)
            {
                case QuickToggleCategory.LinkedCad:
                case QuickToggleCategory.LinkedModel:
                    BuildLinkedInfo(cfg, EditPanelHost);
                    break;
                case QuickToggleCategory.ColorTool:
                    BuildColorToolPicker(cfg, EditHeaderHost, EditPanelHost);
                    break;
                case QuickToggleCategory.CommandLauncher:
                    BuildCommandPicker(cfg, EditHeaderHost, EditPanelHost);
                    break;
                case QuickToggleCategory.ViewTemplate:
                    BuildViewTemplatePicker(cfg, EditHeaderHost, EditPanelHost);
                    break;
                case QuickToggleCategory.Filter:
                    BuildFilterPicker(cfg, EditHeaderHost, EditPanelHost);
                    break;
                case QuickToggleCategory.Workset:
                    BuildWorksetPicker(cfg, EditHeaderHost, EditPanelHost);
                    break;
            }
        }

        private static string StepThreeTitle(QuickToggleCategory category) => category switch
        {
            QuickToggleCategory.ViewTemplate => "대상 뷰템플릿",
            QuickToggleCategory.Filter => "대상 필터",
            QuickToggleCategory.Workset => "대상 작업세트",
            QuickToggleCategory.ColorTool => "대상 모델 카테고리",
            QuickToggleCategory.CommandLauncher => "실행할 기능",
            _ => "대상",
        };

        private static string StepThreeHint(QuickToggleCategory category) => category switch
        {
            QuickToggleCategory.ViewTemplate => "하나만 고를 수 있습니다",
            QuickToggleCategory.Filter => "여러 개 선택 - 모두 함께 켜지고 꺼집니다",
            QuickToggleCategory.Workset => "여러 개 선택 - 모두 함께 켜지고 꺼집니다",
            QuickToggleCategory.ColorTool => "여러 개 선택 가능",
            QuickToggleCategory.CommandLauncher => "하나만 고를 수 있습니다",
            _ => "미리 고를 대상이 없습니다",
        };

        // 편집 중인 버튼이 무엇인지(이름/종류)와 지금 쓸 수 있는 상태인지를 한 줄로 보여준다.
        private UIElement BuildEditTitleRow(QuickToggleButtonConfig cfg)
        {
            WpfGrid titleRow = new WpfGrid { Margin = new Thickness(0, 0, 0, 4) };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border chipHost = new Border { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            WpfGrid.SetColumn(chipHost, 0);
            titleRow.Children.Add(chipHost);

            TextBlock titleName = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock titleKind = new TextBlock { FontSize = 11, Foreground = Theme.TextSecondary };
            StackPanel titleText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleText.Children.Add(titleName);
            titleText.Children.Add(titleKind);
            WpfGrid.SetColumn(titleText, 1);
            titleRow.Children.Add(titleText);

            TextBlock statusText = new TextBlock { FontSize = 11 };
            Border statusBadge = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = statusText,
            };
            WpfGrid.SetColumn(statusBadge, 2);
            titleRow.Children.Add(statusBadge);

            _refreshEditTitle = () =>
            {
                (Brush background, Brush borderBrush, Brush foreground) = PreviewVisualsFor(cfg);
                chipHost.Child = new Border
                {
                    Width = 32,
                    Height = 32,
                    Background = background,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    Child = new Viewbox { Width = 18, Height = 15, Child = IconOf(cfg, foreground) },
                };
                titleName.Text = string.IsNullOrWhiteSpace(cfg.Name) ? "(이름 없음)" : cfg.Name;
                titleKind.Text = CategoryLabel(cfg.Category) + " 버튼";

                ButtonReadiness readiness = ReadinessOf(cfg);
                bool ok = readiness == ButtonReadiness.Ready;
                statusText.Text = readiness switch
                {
                    ButtonReadiness.Ready => "사용 준비됨",
                    ButtonReadiness.NotInProject => "이 프로젝트에 없음",
                    _ => "대상 미지정",
                };
                statusText.Foreground = ok ? Theme.ToggleOn : Theme.WarningText;
                statusBadge.BorderBrush = ok ? Theme.ToggleOn : Theme.WarningText;
                statusBadge.ToolTip = readiness switch
                {
                    ButtonReadiness.Ready => null,
                    // 설정이 PC 전역이라 다른 프로젝트에서 만든 버튼이 그대로 따라온다 - 그 프로젝트에만
                    // 있던 이름이면 여기서는 찾을 수 없고, 툴바에서도 회색으로 표시된다.
                    ButtonReadiness.NotInProject =>
                        "지정해 둔 대상 이름을 지금 열려 있는 프로젝트에서 찾지 못했습니다. " +
                        "이 프로젝트에서는 회색으로 표시되고, 같은 이름이 있는 프로젝트에서는 그대로 동작합니다.",
                    _ => "대상을 고르지 않으면 커스텀 버튼바에서 회색으로 표시되고 눌리지 않습니다.",
                };
            };
            _refreshEditTitle();

            return titleRow;
        }

        // ===== ② 모양 (아이콘 / 켜짐 색상) =====

        // 아이콘 12종 + 색 8종을 늘 펼쳐두면 그만큼 "③ 대상"이 밀린다(2026-07-27 피드백) - 기본은 접어서
        // 현재 값만 요약으로 보여주고, "변경"을 눌렀을 때만 펼친다. 대상 선택 자체가 없는 링크 버튼은
        // 접을 이유가 없으므로 항상 펼친 상태로 두고 토글 버튼도 감춘다.
        private UIElement BuildAppearanceSection(QuickToggleButtonConfig cfg)
        {
            bool collapsible = HasTargetPicker(cfg.Category);

            WpfGrid header = new WpfGrid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement stepHeader = CreateStepHeader(2, "모양", null);
            WpfGrid.SetColumn(stepHeader, 0);
            header.Children.Add(stepHeader);

            StackPanel summary = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 6),
            };
            WpfGrid.SetColumn(summary, 1);
            header.Children.Add(summary);

            Border toggleGlyphHost = new Border { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
            TextBlock toggleText = new TextBlock { Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextSecondary, FontSize = 11 };
            StackPanel toggleContent = new StackPanel { Orientation = Orientation.Horizontal };
            toggleContent.Children.Add(toggleGlyphHost);
            toggleContent.Children.Add(toggleText);
            Button toggleButton = new Button
            {
                Content = toggleContent,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 0, 2),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                Visibility = collapsible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed,
            };
            WpfGrid.SetColumn(toggleButton, 2);
            header.Children.Add(toggleButton);

            StackPanel body = new StackPanel { Margin = new Thickness(26, 0, 0, 8) };

            _refreshAppearanceSection = () =>
                RenderAppearance(cfg, collapsible, summary, body, toggleGlyphHost, toggleText);
            toggleButton.Click += (s, e) =>
            {
                _appearanceExpanded = !_appearanceExpanded;
                _refreshAppearanceSection?.Invoke();
            };
            _refreshAppearanceSection();

            StackPanel section = new StackPanel();
            section.Children.Add(header);
            section.Children.Add(body);
            return section;
        }

        private void RenderAppearance(QuickToggleButtonConfig cfg, bool collapsible,
            System.Windows.Controls.Panel summary, System.Windows.Controls.Panel body,
            Border toggleGlyphHost, TextBlock toggleText)
        {
            bool expanded = !collapsible || _appearanceExpanded;
            bool colorApplies = ColorAppliesTo(cfg.Category);
            QuickToggleIconShape currentShape = cfg.IconShape ?? QuickToggleIcons.DefaultFor(cfg.Category);
            string currentColor = string.IsNullOrEmpty(cfg.OnColorHex) ? DefaultOnColorHex : cfg.OnColorHex!;

            toggleGlyphHost.Child = CreateExpandGlyph(expanded);
            toggleText.Text = expanded ? "접기" : "변경";

            // --- 접힘 상태에서도 지금 무엇이 골라져 있는지는 항상 보인다 ---
            summary.Children.Clear();
            (Brush background, Brush borderBrush, Brush foreground) = PreviewVisualsFor(cfg);
            summary.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Viewbox { Width = 13, Height = 11, Child = IconOf(cfg, foreground) },
            });
            summary.Children.Add(new TextBlock
            {
                Text = QuickToggleIcons.LabelFor(currentShape),
                Foreground = Theme.TextSecondary,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            });
            if (colorApplies)
            {
                summary.Children.Add(new Border
                {
                    Width = 16,
                    Height = 16,
                    Background = OnColorBrush(cfg),
                    BorderBrush = Theme.Border,
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                });
                summary.Children.Add(new TextBlock
                {
                    Text = ColorNameOf(currentColor),
                    Foreground = Theme.TextSecondary,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                });
            }

            body.Children.Clear();
            body.Visibility = expanded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (!expanded) return;

            body.Children.Add(new TextBlock { Text = "아이콘", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 4) });
            WrapPanel iconRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (QuickToggleIconShape shape in Enum.GetValues(typeof(QuickToggleIconShape)))
            {
                QuickToggleIconShape captured = shape;
                bool isSelected = shape == currentShape;
                Border swatch = new Border
                {
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(0, 0, 5, 5),
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    BorderBrush = isSelected ? Theme.Accent : Theme.Border,
                    Background = Theme.Surface,
                    Cursor = Cursors.Hand,
                    ToolTip = QuickToggleIcons.LabelFor(shape),
                    Child = new Viewbox { Width = 17, Height = 14, Child = QuickToggleIcons.Create(shape, Theme.TextPrimary) },
                };
                swatch.MouseLeftButtonDown += (s, e) => { cfg.IconShape = captured; OnAppearanceChanged(); };
                iconRow.Children.Add(swatch);
            }
            body.Children.Add(iconRow);

            if (!colorApplies)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "이 종류의 버튼은 켜짐/꺼짐이 없어 '켜짐 색상'이 화면에 나타나지 않습니다 - 아이콘 모양으로 구분하세요.",
                    Foreground = Theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                });
                return;
            }

            body.Children.Add(new TextBlock { Text = "켜짐 색상", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            WrapPanel colorRow = new WrapPanel();
            foreach ((string hex, string name) in ColorPalette)
            {
                string capturedHex = hex;
                bool isSelected = string.Equals(hex, currentColor, StringComparison.OrdinalIgnoreCase);
                Border swatch = new Border
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(0, 0, 5, 0),
                    BorderThickness = new Thickness(isSelected ? 3 : 1),
                    BorderBrush = isSelected ? Theme.TextPrimary : Theme.Border,
                    Background = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex)),
                    Cursor = Cursors.Hand,
                    ToolTip = name,
                };
                swatch.MouseLeftButtonDown += (s, e) => { cfg.OnColorHex = capturedHex; OnAppearanceChanged(); };
                colorRow.Children.Add(swatch);
            }
            body.Children.Add(colorRow);
        }

        // 아이콘/색을 바꿨을 때 실제로 달라지는 것만 다시 그린다 - 예전처럼 BuildEditPanel로 전체를
        // 다시 그리면 아래 대상 목록의 검색어와 스크롤 위치까지 함께 초기화된다.
        private void OnAppearanceChanged()
        {
            _refreshAppearanceSection?.Invoke();
            _refreshEditTitle?.Invoke();
            RefreshButtonList();
            RefreshPreviewStrip();
        }

        // 대상을 바꿨을 때(체크박스/라디오) - 요약 줄, 제목의 상태 뱃지, 왼쪽 목록, 미리보기만 갱신하고
        // 목록 자체는 손대지 않는다(체크한 항목이 눈앞에서 사라지거나 스크롤이 튀지 않도록).
        private void OnTargetChanged()
        {
            _refreshTargetSummary?.Invoke();
            _refreshEditTitle?.Invoke();
            RefreshButtonList();
            RefreshPreviewStrip();
        }

        private static bool HasTargetPicker(QuickToggleCategory category) =>
            category != QuickToggleCategory.LinkedCad && category != QuickToggleCategory.LinkedModel;

        // ===== ③ 대상: 공통 부품 =====

        // 검색 입력칸 + 현재 선택 요약 + (여러 개 고르는 목록만) 전체 해제. 목록을 필터링할 때는 이 줄을
        // 다시 만들지 않고 결과 패널만 다시 그려야 한다 - 안 그러면 한 글자 입력할 때마다 입력칸 자체가
        // 새 인스턴스로 교체되어 포커스가 끊긴다(2026-07-30에 이미 겪은 함정).
        private static WpfGrid BuildSearchRow(out TextBox searchBox, out TextBlock summaryLabel, out Button clearButton)
        {
            WpfGrid row = new WpfGrid { Margin = new Thickness(26, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock label = new TextBlock
            {
                Text = "검색",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.TextSecondary,
                Margin = new Thickness(0, 0, 6, 0),
            };
            WpfGrid.SetColumn(label, 0);
            row.Children.Add(label);

            TextBox box = new TextBox { Width = 180, VerticalContentAlignment = VerticalAlignment.Center };
            WpfGrid.SetColumn(box, 1);
            row.Children.Add(box);

            TextBlock summary = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.TextSecondary,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 0, 8, 0),
            };
            WpfGrid.SetColumn(summary, 2);
            row.Children.Add(summary);

            Button clear = new Button
            {
                Content = "전체 해제",
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = System.Windows.Visibility.Collapsed,
            };
            WpfGrid.SetColumn(clear, 3);
            row.Children.Add(clear);

            searchBox = box;
            summaryLabel = summary;
            clearButton = clear;
            return row;
        }

        private static bool MatchesSearch(string name, string filter) =>
            string.IsNullOrEmpty(filter) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private static StackPanel CreateResultsHost() => new StackPanel { Margin = new Thickness(26, 0, 0, 0) };

        private static TextBlock CreateNote(string text) => new TextBlock
        {
            Text = text,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(26, 0, 0, 8),
        };

        private static TextBlock CreateEmptyResult(string text) => new TextBlock
        {
            Text = text,
            Foreground = Theme.TextSecondary,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // ===== ③ 대상: 링크 버튼 (고를 대상이 없음) =====

        private void BuildLinkedInfo(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel target)
        {
            bool isCad = cfg.Category == QuickToggleCategory.LinkedCad;

            Border note = new Border
            {
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                Background = Theme.Surface,
                Padding = new Thickness(12),
                Margin = new Thickness(26, 0, 0, 0),
            };
            StackPanel content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = isCad
                    ? "커스텀 버튼바에서 누르면 지금 보고 있는 뷰에 링크된 CAD 도면을 한 번에 끄고, 다시 누르면 켭니다" +
                      "(Revit의 가시성/그래픽 설정 - '가져온 카테고리'를 끄고 켜는 것과 같습니다)."
                    : "커스텀 버튼바에서 누르면 지금 보고 있는 뷰에 링크된 Revit 모델 목록이 열리고, 거기서 링크를 " +
                      "하나씩 끄고 켤 수 있습니다('전체 켜기'/'전체 끄기' 버튼도 있습니다).",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            content.Children.Add(new TextBlock
            {
                Text = isCad
                    ? "링크된 도면이 없는 뷰에서는 버튼이 회색(비활성)으로 표시됩니다. 도면을 '링크'가 아니라 '가져오기'로 넣은 경우는 대상이 아닙니다."
                    : "링크된 모델이 없는 뷰에서는 버튼이 회색(비활성)으로 표시됩니다. 버튼 색은 지금 이 뷰에 보이는 링크가 하나라도 있는지를 알려줍니다.",
                Foreground = Theme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
            });
            note.Child = content;
            target.Children.Add(note);
        }

        // ===== ③ 대상: 뷰템플릿 =====

        private void BuildViewTemplatePicker(QuickToggleButtonConfig cfg,
            System.Windows.Controls.Panel headerHost, System.Windows.Controls.Panel scrollHost)
        {
            StackPanel results = CreateResultsHost();
            headerHost.Children.Add(BuildSearchRow(out TextBox searchBox, out TextBlock summary, out Button clearButton));
            scrollHost.Children.Add(results);

            _refreshTargetSummary = () => summary.Text = "현재 선택: " +
                (string.IsNullOrEmpty(cfg.ViewTemplateName)
                    ? (cfg.ViewTemplateId == null ? "없음" : "뷰템플릿 1개")
                    : cfg.ViewTemplateName!);
            _refreshTargetSummary();

            searchBox.TextChanged += (s, e) => RenderViewTemplateList(cfg, results, searchBox.Text);
            RenderViewTemplateList(cfg, results, "");
        }

        private void RenderViewTemplateList(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel resultsPanel, string filter)
        {
            resultsPanel.Children.Clear();

            // <없음>은 검색어와 무관하게 항상 보여준다 - 이름으로 검색할 대상이 아니라 "선택 해제"라는
            // 별도 기능이라서.
            bool noneSelected = cfg.ViewTemplateId == null && string.IsNullOrEmpty(cfg.ViewTemplateName);
            RadioButton noneRadio = new RadioButton { Content = "<없음>", GroupName = "vt_" + cfg.Id, IsChecked = noneSelected, Margin = new Thickness(0, 3, 0, 3) };
            noneRadio.Checked += (s, e) => { cfg.ViewTemplateId = null; cfg.ViewTemplateName = null; OnTargetChanged(); };
            resultsPanel.Children.Add(noneRadio);

            bool any = false;
            foreach (View vt in _viewTemplates.Where(v => MatchesSearch(v.Name, filter)))
            {
                any = true;
                int id = vt.Id.ToInt();
                string name = vt.Name;
                // 저장된 ElementId가 아니라 이름으로 맞춰본다 - 설정이 PC 전역이라(2026-09-03) 다른
                // 프로젝트에서 만든 버튼은 ID가 이 문서와 전혀 맞지 않는다.
                RadioButton r = new RadioButton { Content = vt.Name, GroupName = "vt_" + cfg.Id, IsChecked = cfg.ViewTemplateName == name, Margin = new Thickness(0, 3, 0, 3) };
                r.Checked += (s, e) => { cfg.ViewTemplateId = id; cfg.ViewTemplateName = name; OnTargetChanged(); };
                resultsPanel.Children.Add(r);
            }

            if (_viewTemplates.Count == 0)
                resultsPanel.Children.Add(CreateEmptyResult("이 문서에 뷰템플릿이 없습니다."));
            else if (!any && !string.IsNullOrEmpty(filter))
                resultsPanel.Children.Add(CreateEmptyResult("검색 결과가 없습니다."));
        }

        // ===== ③ 대상: 필터 =====

        private void BuildFilterPicker(QuickToggleButtonConfig cfg,
            System.Windows.Controls.Panel headerHost, System.Windows.Controls.Panel scrollHost)
        {
            StackPanel results = CreateResultsHost();
            headerHost.Children.Add(BuildSearchRow(out TextBox searchBox, out TextBlock summary, out Button clearButton));
            scrollHost.Children.Add(results);

            _refreshTargetSummary = () =>
            {
                summary.Text = "선택됨 " + cfg.FilterNames.Count + "개";
                clearButton.Visibility = cfg.FilterNames.Count > 0
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            };
            _refreshTargetSummary();

            clearButton.Click += (s, e) =>
            {
                cfg.FilterIds.Clear();
                cfg.FilterNames.Clear();
                RenderFilterList(cfg, results, searchBox.Text);
                OnTargetChanged();
            };
            searchBox.TextChanged += (s, e) => RenderFilterList(cfg, results, searchBox.Text);
            RenderFilterList(cfg, results, "");
        }

        private void RenderFilterList(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel resultsPanel, string filter)
        {
            resultsPanel.Children.Clear();

            bool any = false;
            foreach (ParameterFilterElement f in _filters.Where(f => MatchesSearch(f.Name, filter)))
            {
                any = true;
                int id = f.Id.ToInt();
                string name = f.Name;
                CheckBox cb = new CheckBox { Content = f.Name, IsChecked = cfg.FilterNames.Contains(name), Margin = new Thickness(0, 3, 0, 3) };
                cb.Checked += (s, e) =>
                {
                    if (!cfg.FilterIds.Contains(id)) cfg.FilterIds.Add(id);
                    if (!cfg.FilterNames.Contains(name)) cfg.FilterNames.Add(name);
                    OnTargetChanged();
                };
                cb.Unchecked += (s, e) => { cfg.FilterIds.Remove(id); cfg.FilterNames.Remove(name); OnTargetChanged(); };
                resultsPanel.Children.Add(cb);
            }

            if (_filters.Count == 0)
                resultsPanel.Children.Add(CreateEmptyResult("이 문서에 필터가 없습니다."));
            else if (!any && !string.IsNullOrEmpty(filter))
                resultsPanel.Children.Add(CreateEmptyResult("검색 결과가 없습니다."));
        }

        // ===== ③ 대상: 작업세트 =====

        private void BuildWorksetPicker(QuickToggleButtonConfig cfg,
            System.Windows.Controls.Panel headerHost, System.Windows.Controls.Panel scrollHost)
        {
            if (!_isWorkshared)
            {
                scrollHost.Children.Add(new TextBlock
                {
                    Text = "이 문서는 작업공유(워크셰어링)가 설정되어 있지 않아 작업세트를 고를 수 없습니다.",
                    Foreground = Theme.WarningText,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(26, 0, 0, 0),
                });
                return;
            }

            StackPanel results = CreateResultsHost();
            headerHost.Children.Add(BuildSearchRow(out TextBox searchBox, out TextBlock summary, out Button clearButton));
            scrollHost.Children.Add(results);

            _refreshTargetSummary = () =>
            {
                summary.Text = "선택됨 " + cfg.WorksetNames.Count + "개";
                clearButton.Visibility = cfg.WorksetNames.Count > 0
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            };
            _refreshTargetSummary();

            clearButton.Click += (s, e) =>
            {
                cfg.WorksetIds.Clear();
                cfg.WorksetNames.Clear();
                RenderWorksetList(cfg, results, searchBox.Text);
                OnTargetChanged();
            };
            searchBox.TextChanged += (s, e) => RenderWorksetList(cfg, results, searchBox.Text);
            RenderWorksetList(cfg, results, "");
        }

        private void RenderWorksetList(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel resultsPanel, string filter)
        {
            resultsPanel.Children.Clear();

            bool any = false;
            foreach (Workset w in _worksets.Where(w => MatchesSearch(w.Name, filter)))
            {
                any = true;
                int id = w.Id.IntegerValue;
                string name = w.Name;
                CheckBox cb = new CheckBox { Content = w.Name, IsChecked = cfg.WorksetNames.Contains(name), Margin = new Thickness(0, 3, 0, 3) };
                cb.Checked += (s, e) =>
                {
                    if (!cfg.WorksetIds.Contains(id)) cfg.WorksetIds.Add(id);
                    if (!cfg.WorksetNames.Contains(name)) cfg.WorksetNames.Add(name);
                    OnTargetChanged();
                };
                cb.Unchecked += (s, e) => { cfg.WorksetIds.Remove(id); cfg.WorksetNames.Remove(name); OnTargetChanged(); };
                resultsPanel.Children.Add(cb);
            }

            if (_worksets.Count == 0)
                resultsPanel.Children.Add(CreateEmptyResult("이 문서에 사용자 작업세트가 없습니다."));
            else if (!any && !string.IsNullOrEmpty(filter))
                resultsPanel.Children.Add(CreateEmptyResult("검색 결과가 없습니다."));
        }

        // ===== ③ 대상: 색상 버튼의 모델 카테고리 트리 =====

        private void BuildColorToolPicker(QuickToggleButtonConfig cfg,
            System.Windows.Controls.Panel headerHost, System.Windows.Controls.Panel scrollHost)
        {
            headerHost.Children.Add(CreateNote(
                "실제 색상/투명도 값은 저장하지 않습니다 - 여기서는 '어떤 모델 카테고리에 적용할지'만 고르고, " +
                "색과 투명도는 커스텀 버튼바에서 이 버튼을 눌렀을 때 뜨는 패널에서 그때그때 조절합니다."));

            List<Category> topCategories = QuickToggleService.TopLevelCategoriesOfType(_doc, CategoryType.Model);
            StackPanel results = CreateResultsHost();
            headerHost.Children.Add(BuildSearchRow(out TextBox searchBox, out TextBlock summary, out Button clearButton));
            scrollHost.Children.Add(results);

            _refreshTargetSummary = () =>
            {
                summary.Text = "선택됨 " + cfg.ColorButtonCategories.Count + "개";
                clearButton.Visibility = cfg.ColorButtonCategories.Count > 0
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            };
            _refreshTargetSummary();

            clearButton.Click += (s, e) =>
            {
                cfg.ColorButtonCategories.Clear();
                RenderCategoryList(cfg, results, topCategories, searchBox.Text);
                OnTargetChanged();
            };
            searchBox.TextChanged += (s, e) => RenderCategoryList(cfg, results, topCategories, searchBox.Text);
            RenderCategoryList(cfg, results, topCategories, "");
        }

        // 2026-07-30, "대상을 선택할 때 검색할 수 있는 입력칸" 요청으로 추가 - 검색어가 비어있으면 기존
        // 트리(펼침/접힘) 그대로 보여주고, 검색어가 있으면 깊이와 무관하게 이름이 일치하는 카테고리를
        // 평평하게 나열한다(하위 카테고리를 찾으려고 매번 상위를 펼쳐야 하는 불편을 없애기 위함).
        private void RenderCategoryList(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel resultsPanel,
            List<Category> topCategories, string filter)
        {
            resultsPanel.Children.Clear();

            // 펼침/접힘 토글이 목록만 다시 그리도록 자기 자신을 넘긴다 - 예전엔 BuildEditPanel로 편집
            // 패널 전체를 다시 그려서 검색어까지 날아갔다.
            void Rerender() => RenderCategoryList(cfg, resultsPanel, topCategories, filter);

            if (string.IsNullOrEmpty(filter))
            {
                foreach (Category top in topCategories)
                    resultsPanel.Children.Add(BuildColorToolCategoryRow(cfg, top, 0, Rerender));
                return;
            }

            List<Category> flatMatches = new List<Category>();
            void Collect(Category c)
            {
                if (MatchesSearch(c.Name, filter)) flatMatches.Add(c);
                foreach (Category sub in QuickToggleService.SubCategoriesOf(c)) Collect(sub);
            }
            foreach (Category top in topCategories) Collect(top);

            if (flatMatches.Count == 0)
            {
                resultsPanel.Children.Add(CreateEmptyResult("검색 결과가 없습니다."));
                return;
            }

            foreach (Category cat in flatMatches.OrderBy(c => c.Name))
                resultsPanel.Children.Add(BuildColorToolCategoryRow(cfg, cat, 0, Rerender));
        }

        // 색상 버튼의 대상 카테고리 트리 - 체크박스로 포함 여부만 고른다(실제 색상/투명도 값은
        // 여기서 정하지 않고 툴바에서 이 버튼을 눌렀을 때 뜨는 팝업에서 그때그때 고르므로).
        private UIElement BuildColorToolCategoryRow(QuickToggleButtonConfig cfg, Category category, int depth, Action rerender)
        {
            StackPanel container = new StackPanel();
            int catId = category.Id.ToInt();
            List<Category> subs = QuickToggleService.SubCategoriesOf(category);
            bool hasChildren = subs.Count > 0;
            bool expanded = _expandedCategoryIds.Contains(catId);

            WpfGrid row = new WpfGrid { Margin = new Thickness(depth * 18, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (hasChildren)
            {
                Button expandButton = new Button
                {
                    Content = CreateExpandGlyph(expanded),
                    Width = 18,
                    Height = 18,
                    Padding = new Thickness(3),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = expanded ? "하위 카테고리 접기" : "하위 카테고리 펼치기",
                };
                expandButton.Click += (s, e) =>
                {
                    if (expanded) _expandedCategoryIds.Remove(catId); else _expandedCategoryIds.Add(catId);
                    rerender();
                };
                WpfGrid.SetColumn(expandButton, 0);
                row.Children.Add(expandButton);
            }

            CheckBox includeBox = new CheckBox
            {
                Content = category.Name,
                IsChecked = cfg.ColorButtonCategories.Any(c => c.CategoryName == category.Name && (c.ParentCategoryName ?? "") == (category.Parent?.Name ?? "")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0),
            };
            WpfGrid.SetColumn(includeBox, 1);
            row.Children.Add(includeBox);

            includeBox.Checked += (s, e) =>
            {
                if (cfg.ColorButtonCategories.Any(c => c.CategoryName == category.Name && (c.ParentCategoryName ?? "") == (category.Parent?.Name ?? ""))) return;
                cfg.ColorButtonCategories.Add(new ColorToolCategoryConfig
                {
                    CategoryId = catId,
                    CategoryName = category.Name,
                    ParentCategoryName = category.Parent?.Name,
                });
                OnTargetChanged();
            };
            includeBox.Unchecked += (s, e) =>
            {
                cfg.ColorButtonCategories.RemoveAll(c => c.CategoryName == category.Name && (c.ParentCategoryName ?? "") == (category.Parent?.Name ?? ""));
                OnTargetChanged();
            };

            container.Children.Add(row);

            if (hasChildren && expanded)
                foreach (Category sub in subs)
                    container.Children.Add(BuildColorToolCategoryRow(cfg, sub, depth + 1, rerender));

            return container;
        }

        // ===== ③ 대상: 기능 버튼 (2026-08-03 추가) =====

        // Sunny Tools 자체 명령은 개수가 적어(SunnyToolsCommands.All) 검색어 없이 항상 보여주고, Revit
        // 기본 명령(PostableCommand)은 수백 개라 검색어를 입력해야만 나타난다(SunnyToolsCommands.
        // SearchNativeCommands) - 필터 없이 전부 그리면 창이 느려지고 오히려 원하는 걸 찾기 어렵다.
        private void BuildCommandPicker(QuickToggleButtonConfig cfg,
            System.Windows.Controls.Panel headerHost, System.Windows.Controls.Panel scrollHost)
        {
            headerHost.Children.Add(CreateNote(
                "Sunny Tools 자체 기능은 아래 목록에 항상 나타나고, Revit 기본 기능은 한글과 영문 중 어느 쪽으로 " +
                "검색해도 찾을 수 있습니다. 결과 이름은 현재 Revit 언어로 표시됩니다."));

            StackPanel results = CreateResultsHost();
            headerHost.Children.Add(BuildSearchRow(out TextBox searchBox, out TextBlock summary, out Button clearButton));
            scrollHost.Children.Add(results);

            // 검색어를 바꾸면 고른 항목이 결과 목록에서 사라질 수 있어(예: NAMER를 고른 뒤 "동기화"로
            // 검색하면 그 라디오가 안 보임) 현재 선택을 항상 요약 줄에 띄워 시각적으로 잃어버리지 않게 한다.
            _refreshTargetSummary = () =>
            {
                string displayLabel = SunnyToolsCommands.DisplayLabelFor(
                    cfg.CommandKind, cfg.CommandId, _revitLanguage, cfg.CommandLabel);
                summary.Text = "현재 선택: " + (string.IsNullOrEmpty(displayLabel) ? "(아직 없음)" : displayLabel);
            };
            _refreshTargetSummary();

            searchBox.TextChanged += (s, e) => RenderCommandList(cfg, results, searchBox.Text);
            RenderCommandList(cfg, results, "");
        }

        private void RenderCommandList(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel resultsPanel, string filter)
        {
            resultsPanel.Children.Clear();

            resultsPanel.Children.Add(new TextBlock { Text = "Sunny Tools", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            List<(string Label, string ClassName)> sunnyMatches = SunnyToolsCommands.All.Where(c => MatchesSearch(c.Label, filter)).ToList();
            if (sunnyMatches.Count == 0)
            {
                resultsPanel.Children.Add(CreateEmptyResult("검색 결과가 없습니다."));
            }
            else
            {
                foreach ((string label, string className) in sunnyMatches)
                    AddCommandRadio(cfg, resultsPanel, QuickToggleCommandKind.SunnyTool, className, label);
            }

            if (string.IsNullOrWhiteSpace(filter))
            {
                resultsPanel.Children.Add(new TextBlock
                {
                    Text = "Revit 기본 기능 - 개수가 매우 많아 검색어를 입력해야 표시됩니다 (한글·영문 모두 검색 가능).",
                    Foreground = Theme.TextSecondary,
                    Margin = new Thickness(0, 12, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            const int maxNativeResults = 60;
            resultsPanel.Children.Add(new TextBlock { Text = "Revit 기본 기능", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 2) });
            List<(string Label, string Name)> nativeMatches = SunnyToolsCommands.SearchNativeCommands(
                filter, maxNativeResults, _revitLanguage);
            if (nativeMatches.Count == 0)
            {
                resultsPanel.Children.Add(CreateEmptyResult("검색 결과가 없습니다."));
                return;
            }

            foreach ((string label, string name) in nativeMatches)
                AddCommandRadio(cfg, resultsPanel, QuickToggleCommandKind.NativeRevit, name, label);

            if (nativeMatches.Count >= maxNativeResults)
                resultsPanel.Children.Add(new TextBlock
                {
                    Text = "결과가 많아 상위 " + maxNativeResults + "개만 표시합니다 - 검색어를 더 구체적으로 입력해 보세요.",
                    Foreground = Theme.TextSecondary,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
        }

        private void AddCommandRadio(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel resultsPanel,
            QuickToggleCommandKind kind, string id, string label)
        {
            bool isChecked = cfg.CommandKind == kind && cfg.CommandId == id;
            RadioButton r = new RadioButton { Content = label, GroupName = "cmd_" + cfg.Id, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 3) };
            r.Checked += (s, e) =>
            {
                cfg.CommandKind = kind;
                cfg.CommandId = id;
                cfg.CommandLabel = label;
                OnTargetChanged();
            };
            resultsPanel.Children.Add(r);
        }

        // ===== 작은 도형 부품 =====

        // 이 코드베이스는 텍스트 글리프(▲▼✕ 등) 대신 도형을 직접 그린다 - 폰트/테마에 따라 안 보일 수
        // 있어서. 작은 UI 패턴을 공유 컴포넌트로 뽑지 않고 각 창에 복제하는 관례도 그대로 따른다.
        private static Polygon CreateTriangle(bool pointingUp)
        {
            PointCollection points = pointingUp
                ? new PointCollection { new System.Windows.Point(6, 0), new System.Windows.Point(12, 10), new System.Windows.Point(0, 10) }
                : new PointCollection { new System.Windows.Point(0, 0), new System.Windows.Point(12, 0), new System.Windows.Point(6, 10) };
            return new Polygon { Points = points, Fill = Theme.TextPrimary, Width = 12, Height = 10 };
        }

        private static UIElement CreateXMark()
        {
            Canvas canvas = new Canvas { Width = 12, Height = 12 };
            canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, Y1 = 0, X2 = 12, Y2 = 12, Stroke = Theme.TextPrimary, StrokeThickness = 2 });
            canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, Y1 = 12, X2 = 12, Y2 = 0, Stroke = Theme.TextPrimary, StrokeThickness = 2 });
            return canvas;
        }

        private static Polygon CreateExpandGlyph(bool expanded)
        {
            // 접힘: ▶(오른쪽 방향), 펼침: ▼(아래 방향).
            PointCollection points = expanded
                ? new PointCollection { new System.Windows.Point(0, 0), new System.Windows.Point(10, 0), new System.Windows.Point(5, 8) }
                : new PointCollection { new System.Windows.Point(0, 0), new System.Windows.Point(8, 5), new System.Windows.Point(0, 10) };
            return new Polygon { Points = points, Fill = Theme.TextSecondary, Width = 10, Height = 10 };
        }

        // ①②③ 같은 원문자 글리프는 폰트에 따라 안 보일 수 있어 강조색 사각 뱃지 안에 숫자를 넣어 그린다
        // (Industry 디자인 시스템: 모서리 반지름 0, 강조색으로 채운 오브젝트는 소수로 제한).
        private static UIElement CreateStepHeader(int step, string title, string? hint)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new Border
            {
                Width = 18,
                Height = 18,
                Background = Theme.Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = step.ToString(),
                    Foreground = Theme.OnAccent,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            row.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(hint))
                row.Children.Add(new TextBlock
                {
                    Text = hint,
                    Foreground = Theme.TextSecondary,
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            return row;
        }

        private static Border CreateDivider() =>
            new Border { Height = 1, Background = Theme.Divider, Margin = new Thickness(0, 6, 0, 12) };

        // OnColorHex가 비어 있을 때 툴바가 실제로 쓰는 색(QuickToggleToolbar.VisualsFor → Theme.ToggleOn).
        // 팔레트의 "초록"과 같은 값이어야 하고, Theme.xaml의 ToggleOnBrush와도 맞춰서 유지할 것.
        // 예전 구현은 아무것도 안 고른 버튼의 "선택된 스와치"를 팔레트 첫 항목(스틸블루)으로 표시해서,
        // 설정 창이 보여주는 색과 툴바가 실제로 칠하는 색(초록)이 서로 달랐다 - 렌더링해서 확인 후 수정.
        private const string DefaultOnColorHex = "#3D8F5C";

        // 버튼마다 아이콘 모양/on 상태 색을 직접 고를 수 있게 해달라는 요청(2026-07-27)으로 추가된 팔레트.
        // 이 프로젝트는 전체 색상환 같은 커스텀 컨트롤을 쓰지 않으므로 미리 정한 스와치 중에서만 고른다.
        private static readonly (string Hex, string Name)[] ColorPalette =
        {
            ("#5980A6", "스틸블루"),
            ("#3D8F5C", "초록(기본)"),
            ("#A6595D", "빨강"),
            ("#A67B3D", "호박"),
            ("#6B5DA6", "보라"),
            ("#3D7A8F", "청록"),
            ("#8F6B3D", "갈색"),
            ("#59748F", "슬레이트"),
        };

        private static string ColorNameOf(string hex)
        {
            foreach ((string paletteHex, string name) in ColorPalette)
                if (string.Equals(paletteHex, hex, StringComparison.OrdinalIgnoreCase)) return name;
            return hex;
        }

        // ===== 고급 (내보내기/가져오기/툴바 위치) =====

        private void AdvancedToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _advancedExpanded = !_advancedExpanded;
            UpdateAdvancedSection();
        }

        private void UpdateAdvancedSection()
        {
            AdvancedGlyphHost.Child = CreateExpandGlyph(_advancedExpanded);
            AdvancedToggleText.Text = _advancedExpanded
                ? "고급 설정 접기"
                : "고급 설정 (내보내기 · 가져오기 · 툴바 위치)";
            AdvancedPanel.Visibility = _advancedExpanded
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        // ===== 툴바 위치 초기화 =====

        // 툴바가 화면 밖이나 다시 찾기 힘든 위치로 드래그된 경우를 위한 안전장치 - 기본 오프셋으로 되돌린다.
        // 2026-07-28부터 툴바 위치는 프로젝트별이 아니라 PC 전역 설정이라, 이 창의 "저장"을 거치지 않고
        // 여기서 바로 리셋·저장하고 열려 있는 툴바에도 즉시 반영한다.
        private void ResetPositionButton_Click(object sender, RoutedEventArgs e)
        {
            // WPF 이벤트 핸들러에서 예외가 새어나가면 Revit 프로세스가 그대로 죽는다 (SettingsWindow와 같은 방침).
            try { new QuickToggleGlobalSettings().Save(); }
            catch (Exception ex)
            {
                MessageBox.Show("툴바 위치 설정을 저장하지 못했습니다: " + ex.Message, "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            QuickToggleToolbar.Instance?.ReloadGlobalSettings();
            MessageBox.Show("툴바 위치를 기본값으로 되돌렸습니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ===== 내보내기/가져오기 (다른 모델 간 설정 이식, 2026-07-28 요청 + 2026-07-30 확장) =====

        private static readonly JsonSerializerOptions ExportJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private void ExportJsonButton_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON 파일 (*.json)|*.json",
                FileName = "커스텀버튼_설정.json",
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                string json = JsonSerializer.Serialize(_settings.Buttons, ExportJsonOptions);
                File.WriteAllText(dialog.FileName, json, Encoding.UTF8);
                MessageBox.Show($"{_settings.Buttons.Count}개 버튼을 내보냈습니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("내보내기에 실패했습니다: " + ex.Message, "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportJsonButton_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON 파일 (*.json)|*.json",
            };
            if (dialog.ShowDialog(this) != true) return;

            List<QuickToggleButtonConfig>? imported;
            try
            {
                string json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                imported = JsonSerializer.Deserialize<List<QuickToggleButtonConfig>>(json, ExportJsonOptions);
            }
            catch (Exception ex)
            {
                MessageBox.Show("가져오기에 실패했습니다: " + ex.Message, "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // 예전 버전에서 내보낸 파일에는 지금은 없어진 종류의 버튼(프리셋/그래픽 화면표시 검색)이
            // 들어 있을 수 있다 - 조용히 빼고 나머지만 가져온다(설정 파일을 읽을 때와 같은 처리).
            imported?.RemoveAll(b => QuickToggleSettings.IsRemovedCategory(b.Category));

            if (imported == null || imported.Count == 0)
            {
                MessageBox.Show("가져올 버튼이 없습니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // 예전 버전에서 내보낸 파일에는 지금은 없어진 종류의 버튼(프리셋/그래픽 화면표시 검색)이
            // 들어 있을 수 있다 - 조용히 빼고 나머지만 가져온다(설정 파일을 읽을 때와 같은 처리).
            imported?.RemoveAll(b => QuickToggleSettings.IsRemovedCategory(b.Category));

            if (imported == null || imported.Count == 0)
            {
                MessageBox.Show("가져올 버튼이 없습니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2026-09-03, 설정이 PC 전역이 되면서 이 단계가 크게 단순해졌다. 예전에는 가져온 버튼의
            // ViewTemplateId/FilterIds를 "이 문서 기준"으로 다시 풀어줘야 했지만(TransferButtons/
            // ResolveNamedTargets), 이제 대상은 툴바가 실제로 켜고 끄는 순간 그 문서에서 이름으로 다시
            // 찾으므로(QuickToggleService.Resolve*) 여기서는 이름만 그대로 들고 오면 된다.
            // Id는 새로 발급한다 - 같은 파일을 두 번 가져와도 기존 버튼과 Id가 겹치지 않도록.
            foreach (QuickToggleButtonConfig cfg in imported)
            {
                cfg.Id = Guid.NewGuid().ToString();
                _settings.Buttons.Add(cfg);
            }
            _settings.NormalizeGrouping();

            RefreshButtonList();
            RefreshPreviewStrip();
            SelectButton(imported[0]);

            MessageBox.Show(
                $"{imported.Count}개 버튼을 가져왔습니다. '저장'을 눌러야 반영됩니다.\n\n" +
                "대상(뷰템플릿/필터/작업세트)은 이름으로 다시 찾으므로, 지금 열려 있는 프로젝트에 같은 이름이 " +
                "없으면 그 버튼은 회색으로 표시됩니다.",
                "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ===== 저장/취소 =====

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show("설정 저장에 실패했습니다: " + ex.Message, "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 방금 고른 대상이 툴바에 바로 반영되도록 이름→ElementId 캐시를 버린다 - 안 그러면 최대
            // 2초간(TargetIndexLifetime) 예전 해석 결과가 남는다.
            QuickToggleService.InvalidateTargetIndex();

            // 열려 있는 커스텀 툴바가 있으면 저장한 값이 바로 반영되도록 강제 재로드.
            QuickToggleToolbar.Instance?.ForceReloadSettings(_doc);
            QuickToggleToolbar.Instance?.RefreshState();

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
