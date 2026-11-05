using Autodesk.Revit.DB;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge;

// 프로젝트 기준점의 내부 좌표를 활성 공유대지로 변환한다. 기준점을 옮기지 않고 읽기만 한다.
internal static class ProjectBasePointReader
{
    internal static ProjectBasePointAudit Read(Document doc)
    {
        var result = new ProjectBasePointAudit();
        try
        {
            var point = BasePoint.GetProjectBasePoint(doc);
            var site = doc.ActiveProjectLocation;
            result.SiteName = site?.Name ?? "";
            if (point == null || site == null)
            {
                result.Status = "프로젝트 기준점 또는 활성 공유대지를 찾을 수 없음";
                return result;
            }
            using var position = site.GetProjectPosition(point.Position);
            result.NorthSouthMeters = UnitUtils.ConvertFromInternalUnits(position.NorthSouth, UnitTypeId.Meters);
            result.EastWestMeters = UnitUtils.ConvertFromInternalUnits(position.EastWest, UnitTypeId.Meters);
            result.ElevationMeters = UnitUtils.ConvertFromInternalUnits(position.Elevation, UnitTypeId.Meters);
            result.TrueNorthDegrees = position.Angle * 180.0 / Math.PI;
            result.Status = "확인";
        }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex)
        {
            result.NorthSouthMeters = result.EastWestMeters = result.ElevationMeters = result.TrueNorthDegrees = null;
            result.Status = "조회 실패: " + ex.Message;
        }
        return result;
    }
}
