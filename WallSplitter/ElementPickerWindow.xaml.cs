using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // ChangeReplayEngine이 이름 매칭이 모호(0개 또는 여러 개)할 때 사용자에게 직접 고르게 하는 범용 모달 창.
    // LayerTypeAssignmentWindow의 "필터 텍스트박스 + 목록" 아이디어를 빌리되 새로 작성했다 - 그쪽은 레이어별
    // 여러 행을 ElementType 하나로 고정해 전부 채워야 확인이 켜지는 구조라 "건너뛰기"라는 개념이 아예 없고,
    // 여기서는 한 번에 하나만 고르거나(선택 완료) 이 항목만 넘어가거나(건너뛰기) 이 문서 전체를 포기(취소)할
    // 수 있어야 한다.
    public partial class ElementPickerWindow : Window
    {
        private readonly List<Element> _allCandidates;
        private readonly Func<Element, string> _displayNameSelector;

        public Element? Result { get; private set; }
        public bool Skipped { get; private set; }

        public ElementPickerWindow(string headerText, List<Element> candidates, Func<Element, string>? displayNameSelector = null)
        {
            InitializeComponent();
            _allCandidates = candidates;
            _displayNameSelector = displayNameSelector ?? (el => el.Name ?? "");

            HeaderText.Text = headerText;
            RenderList(_allCandidates);
        }

        private void RenderList(List<Element> items)
        {
            CandidateList.ItemsSource = items
                .Select(el => new PickListItem(el, _displayNameSelector(el)))
                .OrderBy(i => i.Display)
                .ToList();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = FilterBox.Text.Trim();
            List<Element> filtered = string.IsNullOrEmpty(filter)
                ? _allCandidates
                : _allCandidates.Where(el => _displayNameSelector(el).IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            RenderList(filtered);
        }

        private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OkButton.IsEnabled = CandidateList.SelectedItem != null;
        }

        private void CandidateList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (CandidateList.SelectedItem != null) Confirm();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => Confirm();

        private void Confirm()
        {
            if (CandidateList.SelectedItem is not PickListItem item) return;
            Result = item.Element;
            DialogResult = true;
            Close();
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            Skipped = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private sealed class PickListItem
        {
            public Element Element { get; }
            public string Display { get; }
            public PickListItem(Element element, string display) { Element = element; Display = display; }
            public override string ToString() => Display;
        }
    }
}
