#if REVIT2017
using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    internal static class LegacySplitGeometry
    {
        // 2017은 Wall.SketchId가 없다. 삭제될 종속 스케치를 확인하고 검사 트랜잭션은 항상 되돌린다.
        internal static bool IsUnsketchedWall(Document doc, Wall wall)
        {
            var sketches = new FilteredElementCollector(doc).OfClass(typeof(Sketch)).ToElementIds();
            if (sketches.Count == 0) return true;

            using var probe = new Transaction(doc, "벽 프로파일 분리 가능 여부 확인");
            if (probe.Start() != TransactionStatus.Started) return false;
            bool unsketched;
            try
            {
                var removed = doc.Delete(wall.Id);
                unsketched = removed.Contains(wall.Id) && !removed.Intersect(sketches).Any();
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                unsketched = false;
            }
            if (probe.RollBack() != TransactionStatus.RolledBack)
                throw new InvalidOperationException("벽 프로파일 검사 트랜잭션을 되돌리지 못했습니다.");
            return unsketched;
        }

        internal static bool IsFlatFloorWithoutInserts(Floor floor)
        {
            if (floor.FindInserts(true, true, true, true).Count > 0) return false;
            var faces = HostObjectUtils.GetTopFaces(floor);
            return faces.Count > 0 && faces.All(reference =>
                floor.GetGeometryObjectFromReference(reference) is PlanarFace face &&
                face.FaceNormal.IsAlmostEqualTo(XYZ.BasisZ));
        }

        internal static Floor CopyFloor(Document doc, Floor source, FloorType type)
        {
            // 복사로 내부 루프까지 유지한다. NewFloor의 단일 CurveArray로 경계를 재작성하지 않는다.
            var copies = ElementTransformUtils.CopyElement(doc, source.Id, XYZ.Zero);
            var floor = copies.Select(doc.GetElement).OfType<Floor>().Single();
            floor.Pinned = false;
            floor.FloorType = type;
            var structural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
            if (structural != null && structural.AsInteger() != 0 && (structural.IsReadOnly || !structural.Set(0)))
                throw new InvalidOperationException($"바닥 {source.Id}: 복사한 바닥의 구조 속성을 바꿀 수 없습니다.");
            return floor;
        }
    }
}
#endif
