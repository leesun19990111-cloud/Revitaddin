using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // "모델로 내보내기"/"모델에서 가져오기" 전용 - 같은 Revit 세션에 열려 있는 다른 프로젝트 문서 중
    // 하나를 고른다(2026-07-30 추가). 링크/패밀리 문서는 호출자가 미리 걸러서 넘긴다.
    public partial class OpenDocumentPickerWindow : Window
    {
        public Document? SelectedDocument { get; private set; }

        private readonly List<Document> _candidates;
        private Document? _selected;

        public OpenDocumentPickerWindow(string title, string hint, List<Document> candidates)
        {
            InitializeComponent();
            Title = title;
            HintText.Text = hint;
            _candidates = candidates;
            BuildList();
        }

        private void BuildList()
        {
            ListPanel.Children.Clear();

            if (_candidates.Count == 0)
            {
                ListPanel.Children.Add(new TextBlock
                {
                    Text = "같은 Revit 세션에 열려 있는 다른 프로젝트 문서가 없습니다.",
                    Foreground = Theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6),
                });
                return;
            }

            bool first = true;
            foreach (Document doc in _candidates)
            {
                RadioButton r = new RadioButton { Content = doc.Title, GroupName = "docs", Margin = new Thickness(6, 4, 6, 4) };
                r.Checked += (s, e) => _selected = doc;
                if (first) { r.IsChecked = true; _selected = doc; first = false; }
                ListPanel.Children.Add(r);
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                MessageBox.Show("문서를 선택하세요.", "WallSplitter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SelectedDocument = _selected;
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
