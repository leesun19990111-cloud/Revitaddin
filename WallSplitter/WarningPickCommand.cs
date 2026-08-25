using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 리본 "경고Pick" 버튼 - 현재 문서의 경고(Document.GetWarnings())를 조회하는 것뿐이라 문서
    // 트랜잭션이 필요 없다(QuickToggleSettingsCommand와 같은 이유로 ReadOnly).
    // 창 자체는 모드리스(WarningPickWindow.Show())라 이 Execute가 반환된 뒤에도 계속 열려 있을 수
    // 있고, 실제 선택/뷰이동은 WarningPickExternalEventHandler가 처리한다.
    [Transaction(TransactionMode.ReadOnly)]
    public class WarningPickCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            List<WarningPickTypeGroup> typeGroups = WarningPickTypeGroup.BuildTypeGroups(doc, doc.GetWarnings());

            if (WarningPickWindow.Instance == null)
            {
                WarningPickWindow window = new WarningPickWindow(uiapp, doc, typeGroups);
                window.Show();
            }
            else
            {
                // 이미 열려 있는 창을 재사용 - 그 사이 다른 문서로 전환했거나(대상 문서 갱신), 경고 상황이
                // 바뀌었을 수 있으므로(직접 고쳤거나 새로 생겼거나) 항상 최신 상태로 다시 채운다.
                WarningPickWindow.Instance.UpdateDocumentAndTypeGroups(doc, typeGroups);
                WarningPickWindow.Instance.Show();
                WarningPickWindow.Instance.Activate();
            }

            return Result.Succeeded;
        }
    }
}
