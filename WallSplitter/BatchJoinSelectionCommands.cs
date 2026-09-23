using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDocument = Autodesk.Revit.DB.Document;

namespace WallSplitter
{
    // 리본 "일괄 결합금지/허용" 패널의 작은 버튼 두 개 - 창을 열지 않고 **지금 선택한 요소**의 양쪽 끝을 바로
    // 금지/허용한다. 창(BatchJoinWindow)은 유형 단위로 고르거나 한쪽 끝만 고를 때 쓰고, 이 두 명령은
    // "여러 개 골라놓고 바로 거는" 가장 잦은 작업을 클릭 한 번으로 끝내기 위한 것이다.
    internal static class BatchJoinSelectionRunner
    {
        public static Result Run(ExternalCommandData commandData, ref string message, bool allow)
        {
            UIDocument? uidoc = commandData.Application.ActiveUIDocument;
            RevitDocument? doc = uidoc?.Document;
            if (uidoc == null || doc == null)
            {
                message = "열려 있는 문서가 없습니다.";
                return Result.Failed;
            }

            List<Element> targets = new List<Element>();
            int unsupported = 0;
            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                Element? element = doc.GetElement(id);
                if (element == null) continue;
                if (BatchJoinService.IsSupported(element)) targets.Add(element);
                else unsupported++;
            }

            if (targets.Count == 0)
            {
                TaskDialog.Show("일괄 결합금지/허용",
                    unsupported > 0
                        ? "고른 요소 중에 벽·보·가새가 없습니다.\n끝 결합을 설정할 수 있는 것은 벽과 구조 프레임(보·가새)뿐입니다."
                        : "먼저 벽이나 보를 선택한 뒤 눌러주세요.\n유형 단위로 한 번에 걸려면 '결합 금지·허용' 버튼으로 창을 여세요.");
                return Result.Succeeded;
            }

            BatchJoinService.ApplyResult result;
            using (Transaction tx = new Transaction(doc, allow ? "일괄 결합허용" : "일괄 결합금지"))
            {
                tx.Start();
                result = BatchJoinService.Apply(targets, BatchJoinService.EndChoice.Both, allow);
                tx.Commit();
            }

            result.Unsupported += unsupported;
            TaskDialog.Show("일괄 결합금지/허용", result.Summary(allow));
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class BatchJoinDisallowSelectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            BatchJoinSelectionRunner.Run(commandData, ref message, false);
    }

    [Transaction(TransactionMode.Manual)]
    public class BatchJoinAllowSelectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            BatchJoinSelectionRunner.Run(commandData, ref message, true);
    }
}
