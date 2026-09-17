using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 리본 "룸 구분선" 버튼. 창이 모달이라 여기서 트랜잭션을 열지 않고 창이 "만들기"를 누른 시점에
    // 직접 연다 - 그래서 Manual이다(창을 그냥 닫으면 아무것도 바뀌지 않는다).
    [Transaction(TransactionMode.Manual)]
    public class RoomSeparatorCommand : IExternalCommand
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

            RoomSeparatorWindow window = new RoomSeparatorWindow(doc);
            // Revit 메인 창을 소유자로 지정 - 그러지 않으면 창이 Revit 뒤로 숨을 수 있다(다른 창과 같은 처리).
            new WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
