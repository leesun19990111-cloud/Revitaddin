using System.Collections.Generic;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDocument = Autodesk.Revit.DB.Document;

namespace WallSplitter
{
    // 리본 "결합 금지·허용" 버튼. 창이 모달이고 트랜잭션은 창이 "결합 금지/허용"을 누른 시점에 직접 열기 때문에
    // Manual이다 - 그냥 닫으면 아무것도 바뀌지 않는다(RoomBoundingCommand와 같은 구조).
    [Transaction(TransactionMode.Manual)]
    public class BatchJoinCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument? uidoc = uiapp.ActiveUIDocument;
            RevitDocument? doc = uidoc?.Document;
            if (uidoc == null || doc == null)
            {
                message = "열려 있는 문서가 없습니다.";
                return Result.Failed;
            }

            // 창이 모달이라 열려 있는 동안 선택을 바꿀 수 없다 - 열리는 시점의 선택을 그대로 넘긴다.
            ICollection<ElementId> selection = uidoc.Selection.GetElementIds();

            BatchJoinWindow window = new BatchJoinWindow(doc, selection, doc.ActiveView);
            new WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
