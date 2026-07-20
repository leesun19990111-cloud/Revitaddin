using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            SettingsWindow window = new SettingsWindow();

            // Revit 메인 창을 소유자로 지정 - 설정 창이 Revit 뒤로 숨거나 작업 표시줄에 따로 뜨는 것을 방지
            new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = commandData.Application.MainWindowHandle,
            };

            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
