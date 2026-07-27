using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
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

            EditHeaderHost.Children.Add(new TextBlock
            {
                Text = CategoryLabel(cfg.Category) + " 대상 선택" + (cfg.Category == QuickToggleCategory.ViewTemplate ? " (하나만 선택 가능)" : " (여러 개 선택 가능 - 모두 함께 켜고 꺼집니다)"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 3),
            });

            switch (cfg.Category)
            {
                case QuickToggleCategory.ViewTemplate:
                    BuildViewTemplatePicker(cfg);
                    break;
                case QuickToggleCategory.Filter:
                    BuildFilterPicker(cfg);
                    break;
                case QuickToggleCategory.Workset:
                    BuildWorksetPicker(cfg);
                    break;
            }
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

        private void BuildViewTemplatePicker(QuickToggleButtonConfig cfg)
        {
            StackPanel list = new StackPanel();

            RadioButton noneRadio = new RadioButton { Content = "<없음>", GroupName = "vt_" + cfg.Id, IsChecked = cfg.ViewTemplateId == null, Margin = new Thickness(0, 2, 0, 2) };
            noneRadio.Checked += (s, e) => cfg.ViewTemplateId = null;
            list.Children.Add(noneRadio);

            foreach (View vt in _viewTemplates)
            {
                int id = vt.Id.ToInt();
                RadioButton r = new RadioButton { Content = vt.Name, GroupName = "vt_" + cfg.Id, IsChecked = cfg.ViewTemplateId == id, Margin = new Thickness(0, 2, 0, 2) };
                r.Checked += (s, e) => cfg.ViewTemplateId = id;
                list.Children.Add(r);
            }

            if (_viewTemplates.Count == 0)
                list.Children.Add(new TextBlock { Text = "이 문서에 뷰템플릿이 없습니다.", Foreground = Theme.TextSecondary, Margin = new Thickness(0, 4, 0, 0) });

            EditPanelHost.Children.Add(list);
        }

        private void BuildFilterPicker(QuickToggleButtonConfig cfg)
        {
            StackPanel list = new StackPanel();

            foreach (ParameterFilterElement f in _filters)
            {
                int id = f.Id.ToInt();
                CheckBox cb = new CheckBox { Content = f.Name, IsChecked = cfg.FilterIds.Contains(id), Margin = new Thickness(0, 2, 0, 2) };
                cb.Checked += (s, e) => { if (!cfg.FilterIds.Contains(id)) cfg.FilterIds.Add(id); };
                cb.Unchecked += (s, e) => cfg.FilterIds.Remove(id);
                list.Children.Add(cb);
            }

            if (_filters.Count == 0)
                list.Children.Add(new TextBlock { Text = "이 문서에 필터가 없습니다.", Foreground = Theme.TextSecondary });

            EditPanelHost.Children.Add(list);
        }

        private void BuildWorksetPicker(QuickToggleButtonConfig cfg)
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
                EditPanelHost.Children.Add(list);
                return;
            }

            foreach (Workset w in _worksets)
            {
                int id = w.Id.IntegerValue;
                CheckBox cb = new CheckBox { Content = w.Name, IsChecked = cfg.WorksetIds.Contains(id), Margin = new Thickness(0, 2, 0, 2) };
                cb.Checked += (s, e) => { if (!cfg.WorksetIds.Contains(id)) cfg.WorksetIds.Add(id); };
                cb.Unchecked += (s, e) => cfg.WorksetIds.Remove(id);
                list.Children.Add(cb);
            }

            if (_worksets.Count == 0)
                list.Children.Add(new TextBlock { Text = "이 문서에 사용자 작업세트가 없습니다.", Foreground = Theme.TextSecondary });

            EditPanelHost.Children.Add(list);
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
        private void ResetPositionButton_Click(object sender, RoutedEventArgs e)
        {
            QuickToggleSettings defaults = new QuickToggleSettings();
            _settings.ToolbarOffsetXDip = defaults.ToolbarOffsetXDip;
            _settings.ToolbarOffsetYDip = defaults.ToolbarOffsetYDip;
            MessageBox.Show("툴바 위치를 기본값으로 되돌립니다. '저장'을 눌러야 반영됩니다.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Information);
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
