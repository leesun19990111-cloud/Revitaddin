using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 리본의 "표시/숨김" 토글 버튼 - 현재 활성 문서(프로젝트 파일)의 빠른 토글 커스텀 툴바를 껐다 켠다.
    // 설정이 프로젝트 파일 경로별로 저장되므로(ToggleTypeAssignmentPersistenceCommand의 전역 설정과 다름),
    // 반드시 현재 활성 문서를 기준으로 읽고 써야 한다.
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
                settings.Save(doc);
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
