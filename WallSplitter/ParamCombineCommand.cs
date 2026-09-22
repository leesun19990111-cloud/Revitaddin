using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitDocument = Autodesk.Revit.DB.Document;

namespace WallSplitter
{
    // 리본 "매개변수 조합" 버튼. 창은 모달이고, 트랜잭션은 창이 "지금 전체 적용"을 누른 시점에 직접 연다
    // (RoomBoundingCommand와 같은 구조) - 그냥 닫으면 모델은 그대로다.
    [Transaction(TransactionMode.Manual)]
    public class ParamCombineCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            RevitDocument? doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "열려 있는 문서가 없습니다.";
                return Result.Failed;
            }

            ParamCombineWindow window = new ParamCombineWindow(doc);
            new WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
            window.ShowDialog();

            // 규칙(감시할 카테고리)이나 실시간 켜짐 여부가 바뀌었을 수 있으므로 열려 있는 모든 문서의
            // 트리거를 다시 붙이고, 리본 토글 버튼 표시도 실제 설정에 맞춘다.
            ParamCombineUpdater.RefreshTriggers(uiapp.Application);
            App.UpdateParamCombineToggleLabel(ParamCombineSettings.Current.AutoUpdate);
            return Result.Succeeded;
        }
    }
}
