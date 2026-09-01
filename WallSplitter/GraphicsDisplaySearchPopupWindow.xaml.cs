using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 커스텀 툴바의 "그래픽 화면표시 검색" 버튼이 여는 모델리스 검색 패널. 현재 문서의 모델/주석
    // 카테고리를 한 번만 수집하고, 검색 결과에서 카테고리를 클릭하면 기존 V/G 재정의 편집창을 연다.
    // 이 창 자체에서는 Revit 문서를 변경하지 않는다. 편집 결과는 ExternalEvent 요청으로 넘겨 유효한
    // Revit API 컨텍스트에서 그 순간의 활성 뷰에 적용한다(QuickToggleExternalEventHandler 참고).
    public partial class GraphicsDisplaySearchPopupWindow : Window
    {
        private sealed class CategoryEntry
        {
            public Category Category { get; set; } = null!;
            public string Group { get; set; } = "";
            public string Path { get; set; } = "";
        }

        private readonly UIApplication _uiapp;
        private readonly Document _sourceDocument;
        private readonly List<CategoryEntry> _entries = new();
        internal int SourceViewId { get; }

        public GraphicsDisplaySearchPopupWindow(UIApplication uiapp, View view, QuickToggleButtonConfig cfg)
        {
            InitializeComponent();
            _uiapp = uiapp;
            _sourceDocument = view.Document;
            SourceViewId = view.Id.ToInt();

            TitleText.Text = cfg.Name;
            ViewText.Text = $"현재 뷰: {view.Name}  ·  모델·주석 카테고리를 검색한 뒤 클릭해서 편집하세요.";

            AddCategoryGroup(view, CategoryType.Model, "모델 카테고리");
            AddCategoryGroup(view, CategoryType.Annotation, "주석 카테고리");

            Loaded += (s, e) =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            };
            RenderResults("");
        }

        private void AddCategoryGroup(View view, CategoryType type, string group)
        {
            HashSet<int> visited = new();
            foreach (Category category in QuickToggleService.TopLevelCategoriesOfType(_sourceDocument, type))
                AddCategoryRecursive(view, category, group, category.Name, visited);
        }

        private void AddCategoryRecursive(View view, Category category, string group, string path, HashSet<int> visited)
        {
            int id;
            try { id = category.Id.ToInt(); }
            catch { return; }
            if (!visited.Add(id)) return;

            if (QuickToggleService.CanEditCategoryGraphics(view, category.Id))
                _entries.Add(new CategoryEntry { Category = category, Group = group, Path = path });

            foreach (Category subCategory in QuickToggleService.SubCategoriesOf(category))
                AddCategoryRecursive(view, subCategory, group, path + " > " + subCategory.Name, visited);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderResults(SearchBox.Text);

        private void RenderResults(string filter)
        {
            ResultsPanel.Children.Clear();
            string query = (filter ?? "").Trim();
            List<CategoryEntry> matches = _entries
                .Where(entry => query.Length == 0 ||
                                entry.Path.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                entry.Group.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderBy(entry => entry.Group)
                .ThenBy(entry => entry.Path)
                .ToList();

            if (matches.Count == 0)
            {
                ResultsPanel.Children.Add(new TextBlock
                {
                    Text = "일치하는 모델·주석 카테고리가 없습니다.",
                    Foreground = Theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6),
                });
                ResultCountText.Text = "0개";
                return;
            }

            string? currentGroup = null;
            foreach (CategoryEntry entry in matches)
            {
                if (currentGroup != entry.Group)
                {
                    currentGroup = entry.Group;
                    ResultsPanel.Children.Add(new TextBlock
                    {
                        Text = currentGroup,
                        FontWeight = FontWeights.Bold,
                        Foreground = Theme.TextSecondary,
                        Margin = new Thickness(6, ResultsPanel.Children.Count == 0 ? 2 : 10, 6, 3),
                    });
                }

                Button button = new Button
                {
                    Content = entry.Path,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 2),
                    Tag = entry,
                    ToolTip = "클릭해서 이 카테고리의 그래픽 화면표시를 편집합니다",
                };
                button.Click += CategoryButton_Click;
                ResultsPanel.Children.Add(button);
            }

            ResultCountText.Text = matches.Count + "개";
        }

        private void CategoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: CategoryEntry entry }) return;
            if (!IsSourceDocumentStillActive())
            {
                MessageBox.Show("검색 패널을 연 문서가 더 이상 활성 문서가 아닙니다. 패널을 닫고 현재 문서에서 다시 열어 주세요.",
                    "그래픽 화면표시 검색", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }

            Category category = entry.Category;
            CategoryOverrideConfig editable = new CategoryOverrideConfig
            {
                CategoryId = category.Id.ToInt(),
                CategoryName = category.Name,
                ParentCategoryName = category.Parent?.Name,
            };

            CategoryOverrideEditWindow editor = new CategoryOverrideEditWindow(
                _sourceDocument, category, editable, immediateMode: true) { Owner = this };
            if (editor.ShowDialog() != true || editor.Result == null) return;

            if (!HasAnyChange(editor.Result))
            {
                MessageBox.Show("변경할 항목을 하나 이상 지정해 주세요.", "그래픽 화면표시 검색",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (App.QuickToggleHandler == null || App.QuickToggleEvent == null) return;
            App.QuickToggleHandler.PendingGraphicsDisplayApply = new GraphicsDisplayApplyRequest
            {
                SourceDocumentPath = _sourceDocument.PathName ?? "",
                Override = editor.Result.Clone(),
            };
            App.QuickToggleEvent.Raise();
        }

        private bool IsSourceDocumentStillActive()
        {
            Document? active = _uiapp.ActiveUIDocument?.Document;
            if (active == null || !_sourceDocument.IsValidObject) return false;
            if (ReferenceEquals(active, _sourceDocument) || active.Equals(_sourceDocument)) return true;
            if (!string.IsNullOrEmpty(active.PathName) || !string.IsNullOrEmpty(_sourceDocument.PathName))
                return string.Equals(active.PathName, _sourceDocument.PathName, StringComparison.OrdinalIgnoreCase);
            // 아직 저장하지 않은 문서는 PathName이 둘 다 비어 있어 경로 비교로는 구분되지 않는다.
            return string.Equals(active.Title, _sourceDocument.Title, StringComparison.Ordinal);
        }

        private static bool HasAnyChange(CategoryOverrideConfig value) =>
            value.Visible.HasValue || value.Halftone.HasValue || value.DetailLevel != null || value.Transparency.HasValue ||
            value.ProjectionLineWeight.HasValue || value.ProjectionLineColor.HasValue || value.ProjectionLinePatternName != null ||
            value.CutLineWeight.HasValue || value.CutLineColor.HasValue || value.CutLinePatternName != null ||
            value.SurfaceForegroundVisible.HasValue || value.SurfaceForegroundPatternName != null || value.SurfaceForegroundColor.HasValue ||
            value.SurfaceBackgroundVisible.HasValue || value.SurfaceBackgroundPatternName != null || value.SurfaceBackgroundColor.HasValue ||
            value.CutForegroundVisible.HasValue || value.CutForegroundPatternName != null || value.CutForegroundColor.HasValue ||
            value.CutBackgroundVisible.HasValue || value.CutBackgroundPatternName != null || value.CutBackgroundColor.HasValue;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        }
    }
}
