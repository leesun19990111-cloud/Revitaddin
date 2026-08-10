using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // NAMER/재료 지정이 자동으로 쌓아 온 ChangeLog를, 이름 매칭으로 다른 문서에 그대로 재현한다.
    // NamerWindow/MaterialAssignWindow와 같은 "모달 창 하나 띄우고 닫히면 Execute가 이어서 실제 반영을
    // 수행" 구조이지만, 이 커맨드는 반영 대상 문서가 하나가 아닐 수 있으므로 ChangeReplayEngine을
    // 대상 문서 개수만큼 반복 호출한 뒤 결과를 하나의 요약으로 모은다.
    [Transaction(TransactionMode.Manual)]
    public class ModelSyncCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document activeDoc = uiDoc.Document;

            // 링크로만 열린 문서(IsLinked)와 패밀리 편집 문서(IsFamilyDocument)는 뷰/시트/유형/재료 개념이
            // 이 도구가 다루는 프로젝트 문서와 다르므로("패밀리"에는 ViewSheet/ViewSchedule이 같은 방식으로
            // 존재하지 않음) 대상에서 제외한다.
            List<Document> eligibleDocs = commandData.Application.Application.Documents
                .Cast<Document>()
                .Where(d => !d.IsLinked && !d.IsFamilyDocument)
                .ToList();

            ModelSyncWindow window = new ModelSyncWindow(activeDoc, eligibleDocs);
            new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
            bool? dialogResult = window.ShowDialog();
            if (dialogResult != true || window.SelectedEntries == null || window.TargetDocuments == null)
                return Result.Cancelled;

            var summaries = new List<ReplaySummary>();
            foreach (Document targetDoc in window.TargetDocuments)
            {
                ReplaySummary summary = ChangeReplayEngine.RunForDocument(
                    commandData.Application.MainWindowHandle, targetDoc, window.SelectedEntries);
                summaries.Add(summary);
            }

            ShowSummary(summaries);
            return Result.Succeeded;
        }

        private static void ShowSummary(List<ReplaySummary> summaries)
        {
            var sb = new StringBuilder();
            foreach (ReplaySummary summary in summaries)
            {
                sb.AppendLine($"[{summary.DocumentTitle}]");

                if (summary.CancelledByUser)
                {
                    sb.AppendLine("  사용자가 이 문서에 대한 적용을 취소했습니다.");
                }
                else if (!summary.TransactionCommitted)
                {
                    sb.AppendLine("  트랜잭션이 반영되지 않았습니다 (모델에 변경 없음).");
                }
                else
                {
                    sb.AppendLine($"  적용됨: {summary.AppliedCount}개");
                    if (summary.Skipped.Count > 0) sb.AppendLine($"  건너뜀: {summary.Skipped.Count}개");
                    if (summary.Failed.Count > 0) sb.AppendLine($"  실패: {summary.Failed.Count}개");
                }

                foreach ((ChangeLogEntry entry, string reason) in summary.Skipped.Take(10))
                    sb.AppendLine($"    - 건너뜀: '{entry.Key}' ({reason})");
                if (summary.Skipped.Count > 10) sb.AppendLine($"    ... 외 건너뜀 {summary.Skipped.Count - 10}개");

                foreach ((ChangeLogEntry entry, string reason) in summary.Failed.Take(10))
                    sb.AppendLine($"    - 실패: '{entry.Key}' ({reason})");
                if (summary.Failed.Count > 10) sb.AppendLine($"    ... 외 실패 {summary.Failed.Count - 10}개");

                sb.AppendLine();
            }

            TaskDialog.Show("모델간 변경 반영", sb.ToString().TrimEnd());
        }
    }
}
