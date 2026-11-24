using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace SunnyParameterManager.Bridge
{
    // 수명주기 대역 시험은 매개변수 추가 경로를 실행하지 않는다. 호출되면 시험 실패로 드러나게 한다.
    internal static class SharedParameterAdder
    {
        public static ModelEngine.WriteAction Prepare(RevitApplication app, Document doc, ParameterReader reader, ValueEdit edit) =>
            throw new System.InvalidOperationException("수명주기 대역에서는 매개변수 추가를 지원하지 않습니다.");
    }
}
