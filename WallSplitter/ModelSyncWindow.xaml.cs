using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Microsoft.Win32;

namespace WallSplitter
{
    public partial class ModelSyncWindow : Window
    {
        private sealed class LogRow
        {
            public ChangeLogEntry Entry = null!;
            public CheckBox CheckBox = null!;
        }

        private readonly Document _activeDoc;
        private readonly List<Document> _allOpenDocs;
        private List<ChangeLogEntry> _sessionEntries;
        private readonly List<LogRow> _rows = new List<LogRow>();
        private readonly Dictionary<Document, CheckBox> _targetCheckBoxes = new Dictionary<Document, CheckBox>();

        // ChangeLogEntry가 internal 타입이므로(NamerWindow.NamerCategory와 같은 이유) 이 속성도 internal이어야
        // 한다 - 창 자체는 public이지만(다른 WPF 창들과 같은 관례), 이 속성은 같은 어셈블리(ModelSyncCommand)에서만 쓰인다.
        internal List<ChangeLogEntry>? SelectedEntries { get; private set; }
        public List<Document>? TargetDocuments { get; private set; }

        // ChangeLog와 같은 옵션(들여쓰기 + enum을 문자열로) - 내보내기/가져오기 파일도 change-log.json과
        // 같은 모양이어야 서로 호환된다.
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public ModelSyncWindow(Document activeDoc, List<Document> allOpenEligibleDocs)
        {
            InitializeComponent();
            _activeDoc = activeDoc;
            _allOpenDocs = allOpenEligibleDocs;
            _sessionEntries = ChangeLog.Load().Entries.OrderBy(e => e.Timestamp).ToList();

            BuildTargetDocumentRows();
            RenderRows();
        }

        // Revit API는 같은 열린 문서에 대해 호출마다 다른 Document 래퍼를 돌려줄 수 있고 Document는
        // Equals를 값 비교로 재정의하지 않는다(경고Pick에서 CONFIRMED LIVE BUG로 확인). 참조 비교만
        // 믿으면 "현재 문서"가 표시/기본 체크되지 않으므로 경로(저장 안 된 문서는 제목)로도 비교한다.
        private static bool IsSameDocument(Document a, Document b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.PathName) || !string.IsNullOrEmpty(b.PathName))
                return string.Equals(a.PathName, b.PathName, StringComparison.OrdinalIgnoreCase);
            return string.Equals(a.Title, b.Title, StringComparison.Ordinal);
        }

        private void BuildTargetDocumentRows()
        {
            TargetDocsPanel.Children.Clear();
            _targetCheckBoxes.Clear();

            foreach (Document doc in _allOpenDocs)
            {
                bool isActive = IsSameDocument(doc, _activeDoc);
                var checkBox = new CheckBox
                {
                    Content = isActive ? $"{doc.Title} (현재 문서)" : doc.Title,
                    IsChecked = isActive,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                _targetCheckBoxes[doc] = checkBox;
                TargetDocsPanel.Children.Add(checkBox);
            }

            if (_allOpenDocs.Count == 0)
                TargetDocsPanel.Children.Add(new TextBlock
                {
                    Text = "적용할 수 있는 열려 있는 문서가 없습니다.",
                    Foreground = Theme.TextSecondary,
                });
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RenderRows();

        private void RenderRows()
        {
            ItemsPanel.Children.Clear();
            _rows.Clear();

            string filter = FilterBox.Text.Trim();
            IEnumerable<ChangeLogEntry> filtered = _sessionEntries;
            if (!string.IsNullOrEmpty(filter))
            {
                filtered = filtered.Where(e =>
                    Contains(e.Key, filter) || Contains(e.OldValue, filter) ||
                    Contains(e.NewValue, filter) || Contains(e.SourceDocumentTitle, filter));
            }

            foreach (ChangeLogEntry entry in filtered) ItemsPanel.Children.Add(BuildRow(entry));
            UpdateCountText();
        }

        private static bool Contains(string? haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;

        private UIElement BuildRow(ChangeLogEntry entry)
        {
            var row = new LogRow { Entry = entry };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 3) };

            var checkBox = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            checkBox.Checked += (_, _) => UpdateCountText();
            checkBox.Unchecked += (_, _) => UpdateCountText();
            row.CheckBox = checkBox;
            panel.Children.Add(checkBox);

            var text = new TextBlock
            {
                Text = DescribeEntry(entry),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            panel.Children.Add(text);

            _rows.Add(row);
            return panel;
        }

        private static string DescribeEntry(ChangeLogEntry entry)
        {
            string when = entry.Timestamp.ToString("yyyy-MM-dd HH:mm");
            string kindLabel = entry.Kind switch
            {
                ChangeKind.Rename => $"이름 변경({CategoryLabel(entry.Category)})",
                ChangeKind.MaterialAssign => "재료 지정",
                ChangeKind.MaterialDelete => "재료 삭제",
                ChangeKind.MaterialIdentityEdit => $"재료 {(entry.Field == IdentityField.MaterialClass ? "클래스" : "설명")} 변경",
                _ => entry.Kind.ToString(),
            };

            string detail = entry.Kind switch
            {
                ChangeKind.MaterialDelete => $"'{entry.Key}' 삭제",
                ChangeKind.MaterialAssign => $"'{entry.Key}' 유형: '{entry.OldValue}' → '{entry.NewValue}'",
                _ => $"'{entry.Key}' → '{entry.NewValue}'",
            };

            return $"[{when}] {entry.SourceDocumentTitle} | {kindLabel} | {detail}";
        }

        private static string CategoryLabel(NamerWindow.NamerCategory? category) => category switch
        {
            NamerWindow.NamerCategory.View => "뷰",
            NamerWindow.NamerCategory.Sheet => "시트",
            NamerWindow.NamerCategory.Family => "패밀리",
            NamerWindow.NamerCategory.Type => "유형",
            NamerWindow.NamerCategory.Legend => "범례",
            NamerWindow.NamerCategory.Schedule => "일람표",
            NamerWindow.NamerCategory.Material => "재료",
            NamerWindow.NamerCategory.ViewTemplate => "뷰 템플릿",
            _ => "?",
        };

        private void UpdateCountText()
        {
            int checkedCount = _rows.Count(r => r.CheckBox.IsChecked == true);
            CountText.Text = $"{_rows.Count}개 표시 중, 선택됨 {checkedCount}개 (전체 기록 {_sessionEntries.Count}개)";
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (LogRow row in _rows) row.CheckBox.IsChecked = true;
            UpdateCountText();
        }

        private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (LogRow row in _rows) row.CheckBox.IsChecked = false;
            UpdateCountText();
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            List<ChangeLogEntry> selected = _rows.Where(r => r.CheckBox.IsChecked == true).Select(r => r.Entry).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("내보낼 항목을 먼저 선택하세요.", "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog { Filter = "JSON 파일 (*.json)|*.json", FileName = "wallsplitter-changes.json" };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string json = JsonSerializer.Serialize(selected, JsonOptions);
                File.WriteAllText(dialog.FileName, json, Encoding.UTF8);
                MessageBox.Show($"{selected.Count}개 항목을 내보냈습니다.", "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"내보내기 실패: {ex.Message}", "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "JSON 파일 (*.json)|*.json" };
            if (dialog.ShowDialog() != true) return;

            try
            {
                string json = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                List<ChangeLogEntry>? imported = JsonSerializer.Deserialize<List<ChangeLogEntry>>(json, JsonOptions);
                if (imported == null || imported.Count == 0)
                {
                    MessageBox.Show("가져올 항목이 없습니다.", "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 가져온 항목은 이 창의 세션에서만 보인다 - 이 컴퓨터의 %APPDATA% 로그(자동 누적 기록)에는
                // 합치지 않는다. 다른 사람/다른 모델의 변경 이력을 내 로컬 기록과 섞으면 "이건 내가 이
                // 컴퓨터에서 만든 변경인지, 가져온 것인지" 구분이 나중에 흐려지기 때문이다.
                var existingIds = new HashSet<string>(_sessionEntries.Select(e => e.Id));
                List<ChangeLogEntry> newOnes = imported.Where(e => !existingIds.Contains(e.Id)).ToList();
                _sessionEntries = _sessionEntries.Concat(newOnes).OrderBy(e => e.Timestamp).ToList();
                RenderRows();

                MessageBox.Show($"{newOnes.Count}개 항목을 가져왔습니다 (중복 {imported.Count - newOnes.Count}개 제외).",
                    "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"가져오기 실패: {ex.Message}", "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            List<ChangeLogEntry> selected = _rows.Where(r => r.CheckBox.IsChecked == true).Select(r => r.Entry).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("삭제할 항목을 먼저 선택하세요.", "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show($"선택한 {selected.Count}개 항목을 기록에서 삭제할까요? (모델에는 영향 없습니다)", "모델간 변경 반영",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var idsToRemove = new HashSet<string>(selected.Select(e => e.Id));
            _sessionEntries = _sessionEntries.Where(e => !idsToRemove.Contains(e.Id)).ToList();

            // 이 컴퓨터의 로컬 기록(change-log.json)에서도 같은 항목을 지운다 - 가져오기로만 들어온(로컬
            // 기록에는 없는) 항목이 섞여 있어도 RemoveAll은 존재하지 않는 id에 대해 그냥 아무 일도 안 하므로 안전하다.
            ChangeLog log = ChangeLog.Load();
            log.Entries.RemoveAll(e => idsToRemove.Contains(e.Id));
            // WPF 이벤트 핸들러에서 예외가 새어나가면 Revit 프로세스가 그대로 죽는다.
            try { log.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show($"로컬 기록 파일을 갱신하지 못했습니다: {ex.Message}\n\n목록에서는 삭제되었지만 다음에 창을 열면 다시 나타날 수 있습니다.",
                    "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RenderRows();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            List<ChangeLogEntry> selected = _rows.Where(r => r.CheckBox.IsChecked == true).Select(r => r.Entry).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("적용할 변경사항을 먼저 선택하세요.", "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<Document> targets = _targetCheckBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("적용할 대상 문서를 먼저 선택하세요.", "모델간 변경 반영", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectedEntries = selected;
            TargetDocuments = targets;
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
