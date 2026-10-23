using Autodesk.Revit.DB;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge
{
    // 조회 대역 시험은 정의 목록을 읽지 않는다. 호출되면 시험 실패로 드러나게 한다.
    internal static class DefinitionReader
    {
        public static DefinitionSnapshot Read(Document doc, bool countValues = true) =>
            throw new System.InvalidOperationException("조회 대역에서는 매개변수 정의 조회를 지원하지 않습니다.");
    }
}
