using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 레이어 하나에 지정할 유형을 담는 행 모델. SplitWallCommand가 채워서 넘기고,
    // 창을 닫을 때 SelectedType이 채워진 상태로 다시 읽어간다.
    public class LayerPickItem
    {
        public int Index { get; set; }
        public string MaterialName { get; set; } = "";
        public double ThicknessMm { get; set; }
        public ElementType? SelectedType { get; set; }
    }

    public partial class LayerTypeAssignmentWindow : Window
    {
        private readonly List<LayerPickItem> _items;
        private readonly List<ElementType> _allTypes;

        public List<ElementType> Result { get; private set; } = new List<ElementType>();

        public LayerTypeAssignmentWindow(string header, List<LayerPickItem> items, List<ElementType> allTypes, string? warningMessage)
        {
            InitializeComponent();
            _items = items;
            _allTypes = allTypes;

            HeaderText.Text = header;

            if (string.IsNullOrEmpty(warningMessage))
            {
                WarningText.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                WarningText.Text = "⚠ " + warningMessage;
                WarningText.Visibility = System.Windows.Visibility.Visible;
            }

            BuildRows();
            UpdateOkEnabled();
        }

        private void BuildRows()
        {
            RowsPanel.Children.Clear();
            foreach (LayerPickItem item in _items)
                RowsPanel.Children.Add(BuildRow(item));
        }

        // 레이어 하나당: 정보 라벨 + 필터 텍스트박스 + (필터로 좁혀진) 유형 콤보박스.
        // NamerWindow의 "필터 텍스트박스 + 목록" 패턴을 한 행 안에 그대로 축소 적용한 것.
        private UIElement BuildRow(LayerPickItem item)
        {
            Border row = new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 8, 4, 8),
            };

            StackPanel stack = new StackPanel { Orientation = Orientation.Horizontal };
            row.Child = stack;

            TextBlock label = new TextBlock
            {
                Text = $"레이어 {item.Index + 1}: {item.MaterialName}, {item.ThicknessMm:F0}mm",
                Width = 220,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            stack.Children.Add(label);

            TextBox filterBox = new TextBox
            {
                Width = 140,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(filterBox);

            ComboBox resultCombo = new ComboBox
            {
                Width = 200,
                DisplayMemberPath = "Name",
                ItemsSource = _allTypes,
            };
            if (item.SelectedType != null) resultCombo.SelectedItem = item.SelectedType;
            stack.Children.Add(resultCombo);

            filterBox.TextChanged += (s, e) =>
            {
                string filter = filterBox.Text.Trim();
                List<ElementType> filtered = string.IsNullOrEmpty(filter)
                    ? _allTypes
                    : _allTypes.Where(t => t.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                resultCombo.ItemsSource = filtered;
                resultCombo.IsDropDownOpen = filtered.Count > 0;
            };

            resultCombo.SelectionChanged += (s, e) =>
            {
                item.SelectedType = resultCombo.SelectedItem as ElementType;
                UpdateOkEnabled();
            };

            return row;
        }

        private void UpdateOkEnabled()
        {
            OkButton.IsEnabled = _items.Count > 0 && _items.All(i => i.SelectedType != null);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = _items.Select(i => i.SelectedType!).ToList();
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
