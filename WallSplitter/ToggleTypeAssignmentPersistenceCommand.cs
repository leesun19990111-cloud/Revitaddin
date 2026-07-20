using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 리본의 "단일/복수" 버튼: '유형 직접 지정' 모드에서 지정한 유형을 다음 벽에도 이어서 쓸지 전환한다.
    // 문서를 건드리지 않고 %APPDATA% 설정 파일만 읽고 쓰므로 ReadOnly로 충분하다.
    [Transaction(TransactionMode.ReadOnly)]
    public class ToggleTypeAssignmentPersistenceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            NamingSettings settings = NamingSettings.Load();
            settings.TypeAssignmentPersistence = settings.TypeAssignmentPersistence == TypeAssignmentPersistence.Single
                ? TypeAssignmentPersistence.Multiple
                : TypeAssignmentPersistence.Single;

            try
            {
                settings.Save();
            }
            catch (System.Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            // 지속 방식 자체가 바뀌었으니, 세션에 남아있던 "직전 선택"은 더 이상 의미가 없다.
            TypeAssignmentSession.Reset();

            App.UpdateTypeAssignmentToggleLabel(settings.TypeAssignmentPersistence);
            return Result.Succeeded;
        }
    }
}
