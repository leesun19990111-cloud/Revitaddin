using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 리본 "빠른 토글 설정" 버튼 - 문서를 읽기만 하고(뷰템플릿/필터/작업세트 목록 조회) 설정은 %APPDATA%
    // JSON 파일에만 쓰므로 문서 트랜잭션이 필요 없다.
    [Transaction(TransactionMode.ReadOnly)]
    public class QuickToggleSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;
            QuickToggleSettingsWindow window = new QuickToggleSettingsWindow(doc);

            // Revit 메인 창을 소유자로 지정 - 창이 Revit 뒤로 숨거나 작업 표시줄에 따로 뜨는 것을 방지
            new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = commandData.Application.MainWindowHandle,
            };

            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
