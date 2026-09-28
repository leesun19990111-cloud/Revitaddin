// Revit 밖에서 설정 로드만 검사하기 위한 최소 대역. 실제 모델/API 호출은 하지 않는다.
namespace Autodesk.Revit.DB
{
    public class Document { public string PathName { get; set; } = ""; }
    public class ElementId { public long Value { get; set; } }
}
namespace WallSplitter
{
    // 실제 아이콘 열거 값과 같은 순서. 다른 버튼에 저장된 SectionBand 보존도 검사한다.
    public enum QuickToggleIconShape { Layers, Funnel, Lines, Star, Flag, Dot, Eye, Flower, Heart, Bolt, Sheet, Cube, Link, SectionBand }
}
