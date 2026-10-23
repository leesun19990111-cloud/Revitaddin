using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace SunnyParameterManager.Bridge
{
    // 수명주기 대역 시험은 정의 정리 경로를 실행하지 않는다. 호출되면 시험 실패로 드러나게 한다.
    internal static class DefinitionFixer
    {
        public static System.Collections.Generic.List<ModelEngine.WriteAction> Prepare(RevitApplication app, Document doc, System.Collections.Generic.IReadOnlyCollection<ValueEdit> edits) =>
            throw new System.InvalidOperationException("수명주기 대역에서는 매개변수 정의 정리를 지원하지 않습니다.");
    }
}
