using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 리본의 "표시/숨김" 토글 버튼 - 커스텀 툴바를 껐다 켠다. 2026-09-03부터 커스텀 버튼 설정 전체가
    // PC 전역이라(QuickToggleSettings 주석 참고) 이 표시 여부도 프로젝트가 아니라 이 PC 기준이다 -
    // 문서를 요구하는 건 토글 뒤 열려 있는 툴바를 그 문서 기준으로 다시 그리기 위해서일 뿐이다.
    [Transaction(TransactionMode.ReadOnly)]
    public class QuickToggleVisibilityToggleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document? doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "먼저 프로젝트 파일을 여세요.";
                return Result.Failed;
            }

            QuickToggleSettings settings = QuickToggleSettings.Load(doc);
            settings.ToolbarVisible = !settings.ToolbarVisible;

            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            QuickToggleToolbar.Instance?.ForceReloadSettings(doc);
            QuickToggleToolbar.Instance?.RefreshState();
            App.UpdateQuickToggleVisibilityLabel(settings.ToolbarVisible);

            return Result.Succeeded;
        }
    }
}
