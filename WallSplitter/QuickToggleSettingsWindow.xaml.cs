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
using Autodesk.Revit.DB;
// Autodesk.Revit.UI에도 TextBox가 있어(리본용) System.Windows.Controls.TextBox와 충돌한다 - 이 파일은
// TaskDialog 하나만 필요하므로 네임스페이스 전체를 끌어오지 않고 그 타입만 별칭으로 가져온다.
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using TaskDialogCommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons;
using TaskDialogResult = Autodesk.Revit.UI.TaskDialogResult;
// Autodesk.Revit.DB에 데이텀 그리드선을 나타내는 Grid 클래스가 있어 System.Windows.Controls.Grid와
// 충돌한다(둘 다 이 파일에서 필요) - WPF 쪽만 별칭으로 구분한다.
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // 리본 "빠른 토글 설정" 버튼으로 여는 모달 창 - 커맨드 Execute 안에서 ShowDialog()로 열리므로
    // (SettingsCommand/NamerCommand와 동일 구조) 이미 유효한 API 컨텍스트라 ExternalEvent가 필요 없다.
    public partial class QuickToggleSettingsWindow : Window
    {
        private readonly Document _doc;
        private readonly QuickToggleSettings _settings;
        private QuickToggleButtonConfig? _selected;

        private readonly List<View> _viewTemplates;
        private readonly List<ParameterFilterElement> _filters;
        private readonly List<Workset> _worksets;
        private readonly bool _isWorkshared;

        // 프리셋 편집의 카테고리(V/G) 탭 - 각 최상위 카테고리를 펼쳤는지 여부(카테고리 Id 기준). 이 창을
        // 새로 열면 초기화되는 세션 한정 UI 상태라 QuickToggleButtonConfig에는 저장하지 않는다.
        private readonly HashSet<int> _expandedCategoryIds = new HashSet<int>();

        public QuickToggleSettingsWindow(Document doc)
        {
            InitializeComponent();
            _doc = doc;
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

            if (!_isWorkshared)
            {
                AddWorksetButton.IsEnabled = false;
                AddWorksetButton.ToolTip = "이 문서는 작업공유(워크셰어링)가 설정되어 있지 않아 작업세트 버튼을 추가할 수 없습니다.";
            }

            RefreshButtonList();
            if (_settings.Buttons.Count > 0) SelectButton(_settings.Buttons[0]);
            else ShowEmptyEditPanel();
        }

        // ===== 왼쪽: 등록된 버튼 목록 =====

        private void RefreshButtonList()
        {
            ButtonListPanel.Children.Clear();

            for (int i = 0; i < _settings.Buttons.Count; i++)
            {
                int index = i;
                QuickToggleButtonConfig cfg = _settings.Buttons[i];
                bool isSelected = ReferenceEquals(cfg, _selected);

                Border row = new Border
                {
                    Background = isSelected ? Theme.SelectionHighlight : Brushes.Transparent,
                    BorderBrush = Theme.Border,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(4, 6, 4, 6),
                };

                WpfGrid grid = new WpfGrid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Child = grid;

                // CONFIRMED LIVE BUG (2026-07-27), 수정 (1차): 위/아래 버튼이 "투명하게 보여 뭐가 위/아래인지
                // 안 보인다"는 실측 피드백 - 다크 테마 때는 BaseButtonStyle의 기본 배경이 채워진 색이라
                // 버튼 자체가 뚜렷했지만, Industry 라이트 테마로 바뀌며 기본 버튼 배경이 투명(테두리만
                // 있는 "선 그림")이 되어(readme의 .btn-secondary 규칙), 작은 22px 아이콘 버튼에서는 그
                // 미묘한 하이라인 테두리만으론 존재감이 약했다. 배경을 명시적으로 Surface로 채우고
                // (기본 스타일의 투명 배경에 기대지 않음), 삼각형도 더 크고 굵게 키웠었다.
                // CONFIRMED LIVE BUG (2026-07-27), 수정 (2차, 진짜 원인): 1차 수정 이후에도 "안 보이고
                // 이상하다"는 재보고 - Width/Height만 24로 키우고 Padding은 그대로 둔 게 문제였다.
                // BaseButtonStyle의 기본 Padding("10,5")이 그대로 적용되면 24x24 버튼의 실제 콘텐츠
                // 영역은 24-10*2=4 x 24-5*2=14로 쪼그라들어, 12x10짜리 삼각형/12x12 X가 대부분 잘려나가
                // 보였다. 고정: Padding을 작게 명시해서 아이콘이 실제로 버튼 안에 다 들어오게 했다.
                Button upButton = new Button { Content = CreateTriangle(pointingUp: true), Width = 24, Height = 24, Padding = new Thickness(2), Background = Theme.Surface, Margin = new Thickness(0, 0, 2, 0), IsEnabled = index > 0 };
                upButton.Click += (s, e) => { MoveButton(index, index - 1); };
                WpfGrid.SetColumn(upButton, 0);
                grid.Children.Add(upButton);

                Button downButton = new Button { Content = CreateTriangle(pointingUp: false), Width = 24, Height = 24, Padding = new Thickness(2), Background = Theme.Surface, Margin = new Thickness(0, 0, 8, 0), IsEnabled = index < _settings.Buttons.Count - 1 };
                downButton.Click += (s, e) => { MoveButton(index, index + 1); };
                WpfGrid.SetColumn(downButton, 1);
                grid.Children.Add(downButton);

                Border nameArea = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand };
                StackPanel nameStack = new StackPanel();
                nameStack.Children.Add(new TextBlock { Text = CategoryLabel(cfg.Category), FontSize = 10, Foreground = Theme.TextSecondary });
                nameStack.Children.Add(new TextBlock { Text = cfg.Name, FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
                nameArea.Child = nameStack;
                nameArea.MouseLeftButtonDown += (s, e) => SelectButton(cfg);
                WpfGrid.SetColumn(nameArea, 2);
                grid.Children.Add(nameArea);

                Button deleteButton = new Button { Content = CreateXMark(), Width = 24, Height = 24, Padding = new Thickness(2), Background = Theme.Surface, Margin = new Thickness(8, 0, 0, 0) };
                deleteButton.Click += (s, e) => { DeleteButton(index); };
                WpfGrid.SetColumn(deleteButton, 3);
                grid.Children.Add(deleteButton);

                ButtonListPanel.Children.Add(row);
            }

            if (_settings.Buttons.Count == 0)
                ButtonListPanel.Children.Add(new TextBlock
                {
                    Text = "아래에서 버튼을 추가하세요.",
                    Foreground = Theme.TextSecondary,
                    Margin = new Thickness(6),
                    TextWrapping = TextWrapping.Wrap,
                });
        }

        private static string CategoryLabel(QuickToggleCategory category) => category switch
        {
            QuickToggleCategory.ViewTemplate => "뷰템플릿",
            QuickToggleCategory.Filter => "필터",
            QuickToggleCategory.Workset => "작업세트",
            QuickToggleCategory.Preset => "프리셋",
            QuickToggleCategory.ColorTool => "색상",
            _ => "",
        };

        private void MoveButton(int index, int newIndex)
        {
            (_settings.Buttons[index], _settings.Buttons[newIndex]) = (_settings.Buttons[newIndex], _settings.Buttons[index]);
            RefreshButtonList();
        }

        private void DeleteButton(int index)
        {
            QuickToggleButtonConfig removed = _settings.Buttons[index];
            _settings.Buttons.RemoveAt(index);

            if (ReferenceEquals(_selected, removed))
                _selected = _settings.Buttons.Count > 0 ? _settings.Buttons[0] : null;

            RefreshButtonList();
            if (_selected != null) BuildEditPanel(_selected);
            else ShowEmptyEditPanel();
        }

        private void AddViewTemplateButton_Click(object sender, RoutedEventArgs e) => AddButtonOfCategory(QuickToggleCategory.ViewTemplate);
        private void AddFilterButton_Click(object sender, RoutedEventArgs e) => AddButtonOfCategory(QuickToggleCategory.Filter);
        private void AddWorksetButton_Click(object sender, RoutedEventArgs e) => AddButtonOfCategory(QuickToggleCategory.Workset);
        private void AddPresetButton_Click(object sender, RoutedEventArgs e) => AddButtonOfCategory(QuickToggleCategory.Preset);
        private void AddColorToolButton_Click(object sender, RoutedEventArgs e) => AddButtonOfCategory(QuickToggleCategory.ColorTool);

        private void AddButtonOfCategory(QuickToggleCategory category)
        {
            QuickToggleButtonConfig cfg = new QuickToggleButtonConfig
            {
                Category = category,
                Name = _settings.NextDefaultName(category),
            };
            _settings.Buttons.Add(cfg);
            SelectButton(cfg);
        }

        // ===== 오른쪽: 선택된 버튼의 편집 영역 =====

        private void SelectButton(QuickToggleButtonConfig cfg)
        {
            _selected = cfg;
            RefreshButtonList();
            BuildEditPanel(cfg);
        }

        private void ShowEmptyEditPanel()
        {
            EditHeaderHost.Children.Clear();
            EditPanelHost.Children.Clear();
            EditPanelHost.Children.Add(new TextBlock
            {
                Text = "왼쪽 아래에서 버튼을 추가하거나, 목록에서 버튼을 선택하세요.",
                Foreground = Theme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // 이름 입력칸/카테고리 안내는 EditHeaderHost(고정, 스크롤 안 됨)에 넣고, 실제 대상 목록만
        // EditPanelHost(스크롤됨)에 넣는다 - 목록이 길어져도 이름 칸이 항상 보이게 하기 위함
        // (2026-07-27 실측 피드백: "스크롤을 내리면 이름 변경 부분이 같이 사라진다").
        private void BuildEditPanel(QuickToggleButtonConfig cfg)
        {
            EditHeaderHost.Children.Clear();
            EditPanelHost.Children.Clear();

            // 2026-07-27, 사용자 요청으로 위쪽(이름/아이콘/색상) 영역의 위아래 여백을 최소로 줄였다 -
            // "이 부분이 너무 커서 아래 대상 선택 부분이 작아 보인다"는 피드백.
            StackPanel nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            nameRow.Children.Add(new TextBlock { Text = "이름: ", VerticalAlignment = VerticalAlignment.Center });
            TextBox nameBox = new TextBox { Width = 240, Text = cfg.Name, VerticalContentAlignment = VerticalAlignment.Center };
            nameBox.TextChanged += (s, e) => { cfg.Name = nameBox.Text; RefreshButtonList(); };
            nameRow.Children.Add(nameBox);
            EditHeaderHost.Children.Add(nameRow);

            BuildIconAndColorPicker(cfg);

            if (cfg.Category == QuickToggleCategory.Preset)
            {
                // 2026-07-29, "스크롤로 하나씩 찾는 대신 카테고리 탭 형식으로 클릭해서 들어가게 해달라"는
                // 요청으로, 기존에 세 섹션을 한 화면에 이어붙이던 방식을 Revit V/G 대화상자와 같은 탭
                // 구조로 바꿨다(Theme.xaml에 이미 TabControl/TabItem 스타일이 있어 그대로 재사용). 프리셋은
                // 단일 카테고리 버튼과 달리 여러 대상을 동시에 담을 수 있고, 비워둔/포함 안 시킨 항목은
                // 이 프리셋에서 아예 제외된다(QuickToggleService.TogglePreset/DeterminePresetState 참고).
                BuildPresetTabs(cfg);
                return;
            }

            if (cfg.Category == QuickToggleCategory.ColorTool)
            {
                // 2026-07-29, "모델을 선택해서 색상과 투명도를 설정해줄 수 있는 버튼" 요청으로 추가 - 여기서는
                // "어떤 모델 카테고리에 적용할지"만 고른다(체크박스만, 재정의 값 편집 없음). 실제 색상/투명도는
                // 툴바에서 이 버튼을 클릭했을 때 뜨는 팝업(ColorToolPopupWindow)에서 실시간으로 고른다.
                EditPanelHost.Children.Add(new TextBlock
                {
                    Text = "이 색상 버튼이 적용할 모델 카테고리를 선택하세요 (여러 개 선택 가능). 실제 색상/투명도 값은 " +
                           "저장하지 않고, 커스텀 버튼 툴바에서 이 버튼을 클릭했을 때 뜨는 패널에서 그때그때 고릅니다.",
                    Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
                });
                foreach (Category top in QuickToggleService.TopLevelCategoriesOfType(_doc, CategoryType.Model))
                    EditPanelHost.Children.Add(BuildColorToolCategoryRow(cfg, top, depth: 0));
                return;
            }

            EditHeaderHost.Children.Add(new TextBlock
            {
                Text = CategoryLabel(cfg.Category) + " 대상 선택" + (cfg.Category == QuickToggleCategory.ViewTemplate ? " (하나만 선택 가능)" : " (여러 개 선택 가능 - 모두 함께 켜고 꺼집니다)"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 3),
            });

            switch (cfg.Category)
            {
                case QuickToggleCategory.ViewTemplate:
                    BuildViewTemplatePicker(cfg, EditPanelHost);
                    break;
                case QuickToggleCategory.Filter:
                    BuildFilterPicker(cfg, EditPanelHost);
                    break;
                case QuickToggleCategory.Workset:
                    BuildWorksetPicker(cfg, EditPanelHost);
                    break;
            }
        }

        // ===== 프리셋 편집 - 탭 구조 (2026-07-29) =====

        private void BuildPresetTabs(QuickToggleButtonConfig cfg)
        {
            TabControl tabs = new TabControl();

            StackPanel vtPanel = new StackPanel { Margin = new Thickness(8) };
            vtPanel.Children.Add(new TextBlock
            {
                Text = "하나만 선택 가능 - <없음>을 선택하면 이 프리셋은 뷰템플릿을 건드리지 않습니다.",
                Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            BuildViewTemplatePicker(cfg, vtPanel);
            tabs.Items.Add(new TabItem { Header = "뷰템플릿", Content = vtPanel });

            StackPanel filterPanel = new StackPanel { Margin = new Thickness(8) };
            filterPanel.Children.Add(new TextBlock
            {
                Text = "여러 개 선택 가능 - 비워두면 이 프리셋은 필터를 건드리지 않습니다.",
                Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            BuildFilterPicker(cfg, filterPanel);
            tabs.Items.Add(new TabItem { Header = "필터", Content = filterPanel });

            StackPanel worksetPanel = new StackPanel { Margin = new Thickness(8) };
            worksetPanel.Children.Add(new TextBlock
            {
                Text = "여러 개 선택 가능 - 비워두면 이 프리셋은 작업세트를 건드리지 않습니다.",
                Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            BuildWorksetPicker(cfg, worksetPanel);
            tabs.Items.Add(new TabItem { Header = "작업세트", Content = worksetPanel });

            tabs.Items.Add(new TabItem { Header = "모델 카테고리", Content = BuildPresetCategoryTab(cfg, "모델 카테고리", QuickToggleService.TopLevelCategoriesOfType(_doc, CategoryType.Model)) });
            tabs.Items.Add(new TabItem { Header = "주석 카테고리", Content = BuildPresetCategoryTab(cfg, "주석 카테고리", QuickToggleService.TopLevelCategoriesOfType(_doc, CategoryType.Annotation)) });
            tabs.Items.Add(new TabItem { Header = "해석 모델 카테고리", Content = BuildPresetCategoryTab(cfg, "해석 모델 카테고리", QuickToggleService.TopLevelCategoriesOfType(_doc, CategoryType.AnalyticalModel)) });
            tabs.Items.Add(new TabItem { Header = "가져온 카테고리", Content = BuildPresetCategoryTab(cfg, "가져온 카테고리", QuickToggleService.ImportedCategories(_doc)) });

            if (_lastPresetTabIndex >= 0 && _lastPresetTabIndex < tabs.Items.Count) tabs.SelectedIndex = _lastPresetTabIndex;
            tabs.SelectionChanged += (s, e) => { if (tabs.SelectedIndex >= 0) _lastPresetTabIndex = tabs.SelectedIndex; };

            EditPanelHost.Children.Add(tabs);
        }

        // 마지막으로 보던 프리셋 탭 - 편집(체크박스/재정의 창) 후 BuildEditPanel이 통째로 다시 그려지므로,
        // 이 값이 없으면 매번 첫 탭("뷰템플릿")으로 튕겨서 "카테고리 탭에서 체크했더니 다른 탭으로 이동한다"는
        // 혼란을 준다. 이 창을 새로 열면 초기화되는 세션 한정 UI 상태.
        private int _lastPresetTabIndex = 0;

        private UIElement BuildPresetCategoryTab(QuickToggleButtonConfig cfg, string groupLabel, List<Category> topCategories)
        {
            StackPanel panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(new TextBlock
            {
                Text = $"{groupLabel} - 포함시키면 프리셋이 켜질 때 표시/숨김과 아래에서 편집한 그래픽 재정의를 적용하고, " +
                       "꺼지면 표시 상태로 되돌리며 재정의를 지웁니다.",
                Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });

            if (topCategories.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "이 그룹에 카테고리가 없습니다.", Foreground = Theme.TextSecondary });
                return panel;
            }

            foreach (Category top in topCategories)
                panel.Children.Add(BuildCategoryRow(cfg, top, depth: 0));

            return panel;
        }

        private UIElement BuildCategoryRow(QuickToggleButtonConfig cfg, Category category, int depth)
        {
            StackPanel container = new StackPanel();
            int catId = category.Id.ToInt();
            List<Category> subs = QuickToggleService.SubCategoriesOf(category);
            bool hasChildren = subs.Count > 0;
            bool expanded = _expandedCategoryIds.Contains(catId);

            WpfGrid row = new WpfGrid { Margin = new Thickness(depth * 18, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (hasChildren)
            {
                Button expandButton = new Button
                {
                    Content = CreateExpandGlyph(expanded),
                    Width = 18, Height = 18, Padding = new Thickness(3),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                };
                expandButton.Click += (s, e) =>
                {
                    if (expanded) _expandedCategoryIds.Remove(catId); else _expandedCategoryIds.Add(catId);
                    BuildEditPanel(cfg);
                };
                WpfGrid.SetColumn(expandButton, 0);
                row.Children.Add(expandButton);
            }

            CategoryOverrideConfig? existing = cfg.CategoryOverrides.FirstOrDefault(c => c.CategoryId == catId);
            CheckBox includeBox = new CheckBox { IsChecked = existing != null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
            WpfGrid.SetColumn(includeBox, 1);
            row.Children.Add(includeBox);

            TextBlock label = new TextBlock
            {
                Text = existing == null ? category.Name : category.Name + SummarizeOverride(existing),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            WpfGrid.SetColumn(label, 2);
            row.Children.Add(label);

            Button editButton = new Button { Content = "편집", Padding = new Thickness(8, 2, 8, 2), IsEnabled = existing != null };
            editButton.Click += (s, e) =>
            {
                CategoryOverrideConfig current = cfg.CategoryOverrides.First(c => c.CategoryId == catId);
                CategoryOverrideEditWindow dlg = new CategoryOverrideEditWindow(_doc, category, current.Clone()) { Owner = this };
                if (dlg.ShowDialog() == true && dlg.Result != null)
                {
                    int idx = cfg.CategoryOverrides.FindIndex(c => c.CategoryId == catId);
                    cfg.CategoryOverrides[idx] = dlg.Result;
                    BuildEditPanel(cfg);
                }
            };
            WpfGrid.SetColumn(editButton, 3);
            row.Children.Add(editButton);

            includeBox.Checked += (s, e) =>
            {
                if (cfg.CategoryOverrides.Any(c => c.CategoryId == catId)) return;
                // 기본값: 표시=false(숨김) - 프리셋에 카테고리를 포함시키는 가장 흔한 목적이 "이 프리셋이
                // 켜지면 이 카테고리를 숨긴다"이므로, 체크만 해도 바로 의미 있는 동작을 하도록 했다. 더
                // 세밀한 재정의(선/패턴/투명도 등)는 "편집" 버튼에서 추가로 설정한다.
                cfg.CategoryOverrides.Add(new CategoryOverrideConfig
                {
                    CategoryId = catId,
                    CategoryName = category.Name,
                    ParentCategoryName = category.Parent?.Name,
                    Visible = false,
                });
                BuildEditPanel(cfg);
            };
            includeBox.Unchecked += (s, e) =>
            {
                cfg.CategoryOverrides.RemoveAll(c => c.CategoryId == catId);
                BuildEditPanel(cfg);
            };

            container.Children.Add(row);

            if (hasChildren && expanded)
                foreach (Category sub in subs)
                    container.Children.Add(BuildCategoryRow(cfg, sub, depth + 1));

            return container;
        }

        // 접었다 펼치지 않고도 목록에서 바로 뭐가 설정됐는지 알 수 있도록 짧은 요약을 붙인다 - 카테고리가
        // 많을 때 하나하나 "편집"을 눌러보지 않아도 되게 하기 위함.
        private static string SummarizeOverride(CategoryOverrideConfig co)
        {
            List<string> parts = new List<string>();
            if (co.Visible == false) parts.Add("숨김");
            else if (co.Visible == true) parts.Add("표시");
            if (co.Halftone == true) parts.Add("하프톤");
            if (co.Transparency.HasValue) parts.Add($"투명도 {co.Transparency}%");
            if (co.ProjectionLineColor.HasValue || co.ProjectionLineWeight.HasValue || co.ProjectionLinePatternName != null) parts.Add("투영선 재정의");
            if (co.CutLineColor.HasValue || co.CutLineWeight.HasValue || co.CutLinePatternName != null) parts.Add("절단선 재정의");
            if (co.SurfaceForegroundPatternName != null || co.SurfaceBackgroundPatternName != null) parts.Add("표면패턴 재정의");
            if (co.CutForegroundPatternName != null || co.CutBackgroundPatternName != null) parts.Add("절단패턴 재정의");
            return parts.Count == 0 ? "" : "  —  " + string.Join(", ", parts);
        }

        // 색상 버튼 설정 - BuildCategoryRow와 거의 같은 모양이지만 훨씬 단순하다(체크박스만, "편집" 버튼도
        // 재정의 요약도 없음 - 실제 색상/투명도 값은 여기서 정하지 않고 툴바 팝업에서 그때그때 고르므로).
        // 별도 메서드로 둔 이유는 이 코드베이스의 "작은 UI는 공유 대신 복제" 관례와, 프리셋의 카테고리
        // 트리(BuildCategoryRow)를 이 단순한 용도 때문에 매개변수로 복잡하게 만들고 싶지 않아서다.
        private UIElement BuildColorToolCategoryRow(QuickToggleButtonConfig cfg, Category category, int depth)
        {
            StackPanel container = new StackPanel();
            int catId = category.Id.ToInt();
            List<Category> subs = QuickToggleService.SubCategoriesOf(category);
            bool hasChildren = subs.Count > 0;
            bool expanded = _expandedCategoryIds.Contains(catId);

            WpfGrid row = new WpfGrid { Margin = new Thickness(depth * 18, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (hasChildren)
            {
                Button expandButton = new Button
                {
                    Content = CreateExpandGlyph(expanded),
                    Width = 18, Height = 18, Padding = new Thickness(3),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                };
                expandButton.Click += (s, e) =>
                {
                    if (expanded) _expandedCategoryIds.Remove(catId); else _expandedCategoryIds.Add(catId);
                    BuildEditPanel(cfg);
                };
                WpfGrid.SetColumn(expandButton, 0);
                row.Children.Add(expandButton);
            }

            CheckBox includeBox = new CheckBox
            {
                Content = category.Name,
                IsChecked = cfg.ColorButtonCategories.Any(c => c.CategoryId == catId),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0),
            };
            WpfGrid.SetColumn(includeBox, 1);
            row.Children.Add(includeBox);

            includeBox.Checked += (s, e) =>
            {
                if (cfg.ColorButtonCategories.Any(c => c.CategoryId == catId)) return;
                cfg.ColorButtonCategories.Add(new CategoryOverrideConfig
                {
                    CategoryId = catId,
                    CategoryName = category.Name,
                    ParentCategoryName = category.Parent?.Name,
                });
            };
            includeBox.Unchecked += (s, e) => { cfg.ColorButtonCategories.RemoveAll(c => c.CategoryId == catId); };

            container.Children.Add(row);

            if (hasChildren && expanded)
                foreach (Category sub in subs)
                    container.Children.Add(BuildColorToolCategoryRow(cfg, sub, depth + 1));

            return container;
        }

        private static Polygon CreateExpandGlyph(bool expanded)
        {
            // 접힘: ▶(오른쪽 방향), 펼침: ▼(아래 방향).
            PointCollection points = expanded
                ? new PointCollection { new System.Windows.Point(0, 0), new System.Windows.Point(10, 0), new System.Windows.Point(5, 8) }
                : new PointCollection { new System.Windows.Point(0, 0), new System.Windows.Point(8, 5), new System.Windows.Point(0, 10) };
            return new Polygon { Points = points, Fill = Theme.TextSecondary, Width = 10, Height = 10 };
        }

        // 버튼마다 아이콘 모양/on 상태 색을 직접 고를 수 있게 해달라는 요청(2026-07-27)으로 추가 -
        // 카테고리 대상 목록과 달리 이 버튼 자체의 표시 방식이라 스크롤 안 되는 EditHeaderHost에 넣는다.
        // 색상은 별도 컬러피커 컨트롤 없이(이 프로젝트가 커스텀 컨트롤을 안 쓰는 관례) 미리 정한 팔레트
        // 스와치 중에서 고르는 방식으로 단순화했다.
        private static readonly (string Hex, string Name)[] ColorPalette =
        {
            ("#5980A6", "스틸블루(기본)"),
            ("#3D8F5C", "초록"),
            ("#A6595D", "빨강"),
            ("#A67B3D", "호박"),
            ("#6B5DA6", "보라"),
            ("#3D7A8F", "청록"),
            ("#8F6B3D", "갈색"),
            ("#59748F", "슬레이트"),
        };

        private void BuildIconAndColorPicker(QuickToggleButtonConfig cfg)
        {
            QuickToggleIconShape currentShape = cfg.IconShape ?? QuickToggleIcons.DefaultFor(cfg.Category);
            string currentColor = string.IsNullOrEmpty(cfg.OnColorHex) ? ColorPalette[0].Hex : cfg.OnColorHex;

            EditHeaderHost.Children.Add(new TextBlock { Text = "아이콘", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            WrapPanel iconRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (QuickToggleIconShape shape in Enum.GetValues(typeof(QuickToggleIconShape)))
            {
                bool isSelected = shape == currentShape;
                Border swatch = new Border
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(0, 0, 4, 4),
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    BorderBrush = isSelected ? (Brush)new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(currentColor)) : Theme.Border,
                    Background = Theme.Surface,
                    Cursor = Cursors.Hand,
                    ToolTip = QuickToggleIcons.LabelFor(shape),
                    Child = new Viewbox
                    {
                        Width = 16, Height = 13,
                        Child = QuickToggleIcons.Create(shape, Theme.TextPrimary),
                    },
                };
                swatch.MouseLeftButtonDown += (s, e) => { cfg.IconShape = shape; BuildEditPanel(cfg); };
                iconRow.Children.Add(swatch);
            }
            EditHeaderHost.Children.Add(iconRow);

            EditHeaderHost.Children.Add(new TextBlock { Text = "켜짐 색상", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            WrapPanel colorRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach ((string hex, string name) in ColorPalette)
            {
                bool isSelected = string.Equals(hex, currentColor, StringComparison.OrdinalIgnoreCase);
                Border swatch = new Border
                {
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(0, 0, 4, 0),
                    BorderThickness = new Thickness(isSelected ? 3 : 1),
                    BorderBrush = isSelected ? Theme.TextPrimary : Theme.Border,
                    Background = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex)),
                    Cursor = Cursors.Hand,
                    ToolTip = name,
                };
                swatch.MouseLeftButtonDown += (s, e) => { cfg.OnColorHex = hex; BuildEditPanel(cfg); };
                colorRow.Children.Add(swatch);
            }
            EditHeaderHost.Children.Add(colorRow);
        }

        private void BuildViewTemplatePicker(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel target)
        {
            StackPanel list = new StackPanel();

            RadioButton noneRadio = new RadioButton { Content = "<없음>", GroupName = "vt_" + cfg.Id, IsChecked = cfg.ViewTemplateId == null, Margin = new Thickness(0, 2, 0, 2) };
            noneRadio.Checked += (s, e) => { cfg.ViewTemplateId = null; cfg.ViewTemplateName = null; };
            list.Children.Add(noneRadio);

            foreach (View vt in _viewTemplates)
            {
                int id = vt.Id.ToInt();
                string name = vt.Name;
                RadioButton r = new RadioButton { Content = vt.Name, GroupName = "vt_" + cfg.Id, IsChecked = cfg.ViewTemplateId == id, Margin = new Thickness(0, 2, 0, 2) };
                r.Checked += (s, e) => { cfg.ViewTemplateId = id; cfg.ViewTemplateName = name; };
                list.Children.Add(r);
            }

            if (_viewTemplates.Count == 0)
                list.Children.Add(new TextBlock { Text = "이 문서에 뷰템플릿이 없습니다.", Foreground = Theme.TextSecondary, Margin = new Thickness(0, 4, 0, 0) });

            target.Children.Add(list);
        }

        private void BuildFilterPicker(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel target)
        {
            StackPanel list = new StackPanel();

            foreach (ParameterFilterElement f in _filters)
            {
                int id = f.Id.ToInt();
                string name = f.Name;
                CheckBox cb = new CheckBox { Content = f.Name, IsChecked = cfg.FilterIds.Contains(id), Margin = new Thickness(0, 2, 0, 2) };
                cb.Checked += (s, e) => { if (!cfg.FilterIds.Contains(id)) cfg.FilterIds.Add(id); if (!cfg.FilterNames.Contains(name)) cfg.FilterNames.Add(name); };
                cb.Unchecked += (s, e) => { cfg.FilterIds.Remove(id); cfg.FilterNames.Remove(name); };
                list.Children.Add(cb);
            }

            if (_filters.Count == 0)
                list.Children.Add(new TextBlock { Text = "이 문서에 필터가 없습니다.", Foreground = Theme.TextSecondary });

            target.Children.Add(list);
        }

        private void BuildWorksetPicker(QuickToggleButtonConfig cfg, System.Windows.Controls.Panel target)
        {
            StackPanel list = new StackPanel();

            if (!_isWorkshared)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "이 문서는 작업공유(워크셰어링)가 설정되어 있지 않습니다.",
                    Foreground = Theme.WarningText,
                    TextWrapping = TextWrapping.Wrap,
                });
                target.Children.Add(list);
                return;
            }

            foreach (Workset w in _worksets)
            {
                int id = w.Id.IntegerValue;
                string name = w.Name;
                CheckBox cb = new CheckBox { Content = w.Name, IsChecked = cfg.WorksetIds.Contains(id), Margin = new Thickness(0, 2, 0, 2) };
                cb.Checked += (s, e) => { if (!cfg.WorksetIds.Contains(id)) cfg.WorksetIds.Add(id); if (!cfg.WorksetNames.Contains(name)) cfg.WorksetNames.Add(name); };
                cb.Unchecked += (s, e) => { cfg.WorksetIds.Remove(id); cfg.WorksetNames.Remove(name); };
                list.Children.Add(cb);
            }

            if (_worksets.Count == 0)
                list.Children.Add(new TextBlock { Text = "이 문서에 사용자 작업세트가 없습니다.", Foreground = Theme.TextSecondary });

            target.Children.Add(list);
        }

        // 위/아래 이동·삭제 버튼 아이콘 - SettingsWindow의 CreateTriangle/CreateXMark와 동일한 방식
        // (텍스트 글리프는 폰트/테마에 따라 안 보일 수 있어 도형으로 직접 그림) - 이 코드베이스는 이런 작은
        // UI 패턴을 공유 컴포넌트로 뽑지 않고 각 창에 그대로 복제하는 관례를 따른다.
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

        // ===== 저장/취소 =====

        // 툴바가 화면 밖이나 다시 찾기 힘든 위치로 드래그된 경우를 위한 안전장치 - 기본 오프셋으로 되돌린다.
        // 2026-07-28부터 툴바 위치는 프로젝트별이 아니라 PC 전역 설정이라, 이 창의 "저장"을 거치지 않고
        // 여기서 바로 리셋·저장하고 열려 있는 툴바에도 즉시 반영한다.
        private void ResetPositionButton_Click(object sender, RoutedEventArgs e)
        {
            new QuickToggleGlobalSettings().Save();
            QuickToggleToolbar.Instance?.ReloadGlobalSettings();
            MessageBox.Show("툴바 위치를 기본값으로 되돌렸습니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ===== 내보내기/가져오기 (다른 모델 간 설정 이식, 2026-07-28 요청) =====

        private static readonly JsonSerializerOptions ExportJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private void ExportButton_Click(object sender, RoutedEventArgs e)
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

        // 가져온 버튼은 ID(ElementId)가 아니라 이름으로 현재 문서에서 다시 찾는다 - 다른 모델에서 만든
        // 설정이라 ElementId 자체는 이 문서에서 무의미하다. 이름이 일치하지 않는 대상은 "누락 목록"에
        // 모아 한 번에 경고하고, 사용자가 그래도 가져올지 선택한다. 기존 등록된 버튼은 지우지 않고
        // 그 위에 추가(overlay)한다 - 이 시점엔 아직 파일로 저장되지 않고, 창의 "저장" 버튼을 눌러야
        // 최종 반영된다(다른 편집과 동일한 흐름).
        private void ImportButton_Click(object sender, RoutedEventArgs e)
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
            if (imported == null || imported.Count == 0)
            {
                MessageBox.Show("가져올 버튼이 없습니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Dictionary<string, int> viewTemplateByName = _viewTemplates.ToDictionary(v => v.Name, v => v.Id.ToInt());
            Dictionary<string, int> filterByName = _filters.ToDictionary(f => f.Name, f => f.Id.ToInt());
            Dictionary<string, int> worksetByName = _worksets.ToDictionary(w => w.Name, w => w.Id.IntegerValue);

            // 카테고리는 같은 이름이 서로 다른 상위 카테고리 아래 있을 수 있어(부모 이름까지 함께) 키로
            // 쓴다. 선/채우기 패턴 이름은 여기서 재검색하지 않는다 - QuickToggleService가 프리셋을 적용할
            // 때마다(Toggle 시점) 그 문서에서 이름으로 다시 찾으므로 이식 시점엔 그대로 문자열만 옮기면
            // 되고, 대상 문서에 없는 패턴이면 그 속성만 조용히 건너뛴다(ResolveLinePatternId/FillPatternId).
            Dictionary<(string Name, string? Parent), int> categoryByName = new Dictionary<(string, string?), int>();
            foreach (Category c in QuickToggleService.AllCategoriesForNameMatching(_doc))
            {
                var key = (c.Name, c.Parent?.Name);
                if (!categoryByName.ContainsKey(key)) categoryByName[key] = c.Id.ToInt();
            }

            List<string> missing = new List<string>();

            // 이름 기반 재검색은 카테고리로 분기하지 않고 항상 세 부분 다 시도한다 - 프리셋 버튼은
            // 세 필드를 동시에 가질 수 있고, 단일 카테고리 버튼은 어차피 나머지 두 필드가 비어있는
            // 리스트/null이라 그 부분은 그냥 아무 일도 하지 않는다(2026-07-28, 프리셋 추가로 카테고리별
            // switch였던 기존 로직을 통합).
            foreach (QuickToggleButtonConfig cfg in imported)
            {
                if (string.IsNullOrEmpty(cfg.ViewTemplateName))
                {
                    cfg.ViewTemplateId = null;
                }
                else if (viewTemplateByName.TryGetValue(cfg.ViewTemplateName, out int vtId))
                {
                    cfg.ViewTemplateId = vtId;
                }
                else
                {
                    missing.Add($"{cfg.Name} - 뷰템플릿 '{cfg.ViewTemplateName}'");
                    cfg.ViewTemplateId = null;
                }

                List<int> resolvedFilterIds = new List<int>();
                List<string> resolvedFilterNames = new List<string>();
                foreach (string name in cfg.FilterNames)
                {
                    if (filterByName.TryGetValue(name, out int filterId))
                    {
                        resolvedFilterIds.Add(filterId);
                        resolvedFilterNames.Add(name);
                    }
                    else
                    {
                        missing.Add($"{cfg.Name} - 필터 '{name}'");
                    }
                }
                cfg.FilterIds = resolvedFilterIds;
                cfg.FilterNames = resolvedFilterNames;

                List<int> resolvedWorksetIds = new List<int>();
                List<string> resolvedWorksetNames = new List<string>();
                foreach (string name in cfg.WorksetNames)
                {
                    if (worksetByName.TryGetValue(name, out int worksetId))
                    {
                        resolvedWorksetIds.Add(worksetId);
                        resolvedWorksetNames.Add(name);
                    }
                    else
                    {
                        missing.Add($"{cfg.Name} - 작업세트 '{name}'");
                    }
                }
                cfg.WorksetIds = resolvedWorksetIds;
                cfg.WorksetNames = resolvedWorksetNames;

                List<CategoryOverrideConfig> resolvedCategoryOverrides = new List<CategoryOverrideConfig>();
                foreach (CategoryOverrideConfig co in cfg.CategoryOverrides)
                {
                    var key = (co.CategoryName, co.ParentCategoryName);
                    if (categoryByName.TryGetValue(key, out int catId))
                    {
                        co.CategoryId = catId;
                        resolvedCategoryOverrides.Add(co);
                    }
                    else
                    {
                        missing.Add($"{cfg.Name} - 카테고리 '{co.CategoryName}'");
                    }
                }
                cfg.CategoryOverrides = resolvedCategoryOverrides;

                List<CategoryOverrideConfig> resolvedColorButtonCategories = new List<CategoryOverrideConfig>();
                foreach (CategoryOverrideConfig co in cfg.ColorButtonCategories)
                {
                    var key = (co.CategoryName, co.ParentCategoryName);
                    if (categoryByName.TryGetValue(key, out int catId))
                    {
                        co.CategoryId = catId;
                        resolvedColorButtonCategories.Add(co);
                    }
                    else
                    {
                        missing.Add($"{cfg.Name} - 카테고리 '{co.CategoryName}'");
                    }
                }
                cfg.ColorButtonCategories = resolvedColorButtonCategories;
            }

            if (missing.Count > 0)
            {
                TaskDialog dlg = new TaskDialog("WallSplitter")
                {
                    MainInstruction = "이 문서에 없는 대상이 있습니다",
                    MainContent = "다음 항목을 이 문서에서 찾을 수 없습니다:\n\n" + string.Join("\n", missing) +
                                   "\n\n그래도 가져오시겠습니까? (없는 항목은 비워진 채로 가져와집니다)",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.Yes,
                };
                if (dlg.Show() != TaskDialogResult.Yes) return;
            }

            foreach (QuickToggleButtonConfig cfg in imported)
            {
                cfg.Id = Guid.NewGuid().ToString();
                _settings.Buttons.Add(cfg);
            }

            RefreshButtonList();
            if (imported.Count > 0) SelectButton(imported[0]);
            MessageBox.Show($"{imported.Count}개 버튼을 가져왔습니다. '저장'을 눌러야 반영됩니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings.Save(_doc);
            }
            catch (Exception ex)
            {
                MessageBox.Show("설정 저장에 실패했습니다: " + ex.Message, "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

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
