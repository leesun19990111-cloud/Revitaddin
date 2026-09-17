using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 리본 "룸경계 ON/OFF" 버튼. 창이 모달이고 트랜잭션은 창이 "켜기/끄기"를 누른 시점에 직접 열기 때문에
    // Manual이다 - 그냥 닫으면 아무것도 바뀌지 않는다(RoomSeparatorCommand와 같은 구조).
    [Transaction(TransactionMode.Manual)]
    public class RoomBoundingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "열려 있는 문서가 없습니다.";
                return Result.Failed;
            }

            RoomBoundingWindow window = new RoomBoundingWindow(doc);
            new WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
