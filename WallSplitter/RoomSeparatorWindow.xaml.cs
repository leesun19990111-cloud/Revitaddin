using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
// Autodesk.Revit.DB와 System.Windows를 같이 쓰면 Grid/Control/Binding/Line/Point 같은 이름이 겹친다 -
// 이 프로젝트의 관례대로 겹치는 것만 별칭으로 구분한다(루트 CLAUDE.md 참고).
using RevitDocument = Autodesk.Revit.DB.Document;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // "룸 구분선 자동 생성" 창. Revit 조회/생성은 전부 RoomSeparatorService가 하고, 이 창은 고르는 일만
    // 한다(다른 창들과 같은 구조). 모달이라 닫힐 때까지 Revit이 기다리고, 생성은 "만들기"를 누른 그 자리에서
    // 트랜잭션 하나로 끝난다.
    public partial class RoomSeparatorWindow : Window
    {
        private readonly RevitDocument _doc;
        private readonly List<RoomSeparatorService.WallTypeChoice> _types;
        private readonly List<RoomSeparatorService.LevelChoice> _levels;

        // 체크 상태는 화면(체크박스)이 아니라 여기에 둔다 - 검색으로 목록을 다시 그려도 고른 것이 풀리지
        // 않게 하기 위함(재료 지정/커스텀 버튼 설정 창과 같은 방침).
        private readonly HashSet<string> _checkedTypes = new HashSet<string>();
        private readonly HashSet<int> _checkedLevels = new HashSet<int>();

        public RoomSeparatorWindow(RevitDocument doc)
        {
            InitializeComponent();
            _doc = doc;
            _types = RoomSeparatorService.CollectWallTypes(doc);
            _levels = RoomSeparatorService.CollectLevels(doc);

            // 지금 보고 있는 뷰가 평면뷰면 그 레벨을 미리 체크해 둔다 - 대부분 "지금 이 층"을 원한다.
            if (doc.ActiveView is ViewPlan plan && plan.GenLevel != null)
                _checkedLevels.Add(plan.GenLevel.Id.ToInt());

            BuildLevelList();
            BuildTypeList();
            UpdateCounts();
        }

        // ===== 목록 =====

        private void BuildTypeList()
        {
            TypeListPanel.Children.Clear();
            string filter = TypeSearchBox.Text?.Trim() ?? "";

            List<RoomSeparatorService.WallTypeChoice> shown = string.IsNullOrEmpty(filter)
                ? _types
                : _types.Where(t => t.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();

            if (_types.Count == 0)
            {
                TypeListPanel.Children.Add(Hint("이 모델과 링크에서 벽을 찾지 못했습니다."));
                return;
            }
            if (shown.Count == 0)
            {
                TypeListPanel.Children.Add(Hint("'" + filter + "'에 맞는 벽 유형이 없습니다."));
                return;
            }

            foreach (RoomSeparatorService.WallTypeChoice type in shown)
                TypeListPanel.Children.Add(BuildTypeRow(type));
        }

        private UIElement BuildTypeRow(RoomSeparatorService.WallTypeChoice type)
        {
            WpfCheckBox check = new WpfCheckBox
            {
                IsChecked = _checkedTypes.Contains(type.Name),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            check.Checked += (s, e) => { _checkedTypes.Add(type.Name); UpdateCounts(); };
            check.Unchecked += (s, e) => { _checkedTypes.Remove(type.Name); UpdateCounts(); };

            StackPanel info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = type.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock
            {
                Text = type.SourceSummary(),
                FontSize = 11,
                Foreground = Theme.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            WpfGrid grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            WpfGrid.SetColumn(check, 0);
            WpfGrid.SetColumn(info, 1);
            grid.Children.Add(check);
            grid.Children.Add(info);

            Border row = new Border
            {
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = grid,
            };
            // 줄 아무 데나 눌러도 체크가 되게 - 체크박스만 눌러야 하면 답답하다.
            row.MouseLeftButtonDown += (s, e) => check.IsChecked = !(check.IsChecked ?? false);
            return row;
        }

        private void BuildLevelList()
        {
            LevelListPanel.Children.Clear();
            if (_levels.Count == 0)
            {
                LevelListPanel.Children.Add(Hint("이 모델에 레벨이 없습니다."));
                return;
            }

            // 위층이 위에 오도록 - 목록에서는 높은 레벨을 위에 보여주는 게 자연스럽다.
            foreach (RoomSeparatorService.LevelChoice level in Enumerable.Reverse(_levels))
            {
                int id = level.Id;
                WpfCheckBox check = new WpfCheckBox
                {
                    Content = level.Name,
                    IsChecked = _checkedLevels.Contains(id),
                    Margin = new Thickness(8, 6, 8, 6),
                };
                check.Checked += (s, e) => { _checkedLevels.Add(id); UpdateCounts(); };
                check.Unchecked += (s, e) => { _checkedLevels.Remove(id); UpdateCounts(); };
                LevelListPanel.Children.Add(check);
            }
        }

        private static TextBlock Hint(string text) => new TextBlock
        {
            Text = text,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8),
        };

        private void UpdateCounts()
        {
            TypeCountText.Text = "선택됨 " + _checkedTypes.Count + "개";
            LevelCountText.Text = "선택됨 " + _checkedLevels.Count + "개";
            CreateButton.IsEnabled = _checkedTypes.Count > 0 && _checkedLevels.Count > 0;
            StatusText.Text = CreateButton.IsEnabled
                ? ""
                : "벽 유형과 레벨을 각각 하나 이상 골라주세요.";
        }

        // ===== 핸들러 =====

        private void TypeSearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildTypeList();

        private void ClearTypesButton_Click(object sender, RoutedEventArgs e)
        {
            _checkedTypes.Clear();
            BuildTypeList();
            UpdateCounts();
        }

        private void ClearLevelsButton_Click(object sender, RoutedEventArgs e)
        {
            _checkedLevels.Clear();
            BuildLevelList();
            UpdateCounts();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            List<int> levelIds = _levels.Where(l => _checkedLevels.Contains(l.Id)).Select(l => l.Id).ToList();
            if (_checkedTypes.Count == 0 || levelIds.Count == 0) return;

            RoomSeparatorService.RoomSeparatorResult result;
            using (Transaction tx = new Transaction(_doc, "룸 구분선 자동 생성"))
            {
                tx.Start();
                result = RoomSeparatorService.Create(
                    _doc, new HashSet<string>(_checkedTypes), levelIds, IncludeLinksCheck.IsChecked == true);
                tx.Commit();
            }

            ShowResult(result);
            Close();
        }

        private static void ShowResult(RoomSeparatorService.RoomSeparatorResult result)
        {
            List<string> lines = new List<string> { "룸 구분선 " + result.Created + "개를 만들었습니다." };

            if (result.UnmeasuredWalls > 0)
                lines.Add("벽 " + result.UnmeasuredWalls + "개는 중심면을 재지 못해 벽의 위치선 그대로 그렸습니다(곡선 벽 등). 그 선들은 벽 중심에서 벗어나 있을 수 있습니다.");
            if (result.SkippedWalls > 0)
                lines.Add("건너뛴 벽 " + result.SkippedWalls + "개 (위치선을 읽을 수 없거나 구분선으로 만들 수 없는 모양).");
            if (result.LevelsWithoutPlanView.Count > 0)
                lines.Add("평면뷰가 없어 건너뛴 레벨: " + string.Join(", ", result.LevelsWithoutPlanView));
            foreach (string note in result.Notes) lines.Add(note);

            if (result.Created == 0)
                lines.Add("\n고른 유형의 벽이 그 레벨에 없거나, 링크 포함을 꺼 둔 것은 아닌지 확인해 보세요.");

            System.Windows.MessageBox.Show(string.Join("\n", lines), "룸 구분선 자동 생성",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
