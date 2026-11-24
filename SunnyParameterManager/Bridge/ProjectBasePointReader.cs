using Autodesk.Revit.DB;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge;

// 두 기준점의 화면 표시 매개변수를 읽는다. 기준점을 옮기지 않는다.
internal static class ProjectBasePointReader
{
    internal static ProjectBasePointAudit Read(Document doc)
    {
        var result = new ProjectBasePointAudit { FormatVersion = 3 };
        ProjectLocation? site;
        try
        {
            site = doc.ActiveProjectLocation;
            result.SiteName = site?.Name ?? "";
        }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex)
        {
            result.Status = result.SurveyStatus = "활성 공유대지 조회 실패: " + ex.Message;
            return result;
        }
        try
        {
            var point = BasePoint.GetProjectBasePoint(doc);
            if (point == null || site == null)
            {
                result.Status = "프로젝트 기준점 또는 활성 공유대지를 찾을 수 없음";
            }
            else
            {
                (result.NorthSouthMillimeters, result.EastWestMillimeters, result.ElevationMillimeters) = ReadCoordinates(point, "프로젝트 기준점");
                var angle = point.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM);
                if (angle == null || !angle.HasValue)
                    throw new InvalidOperationException("프로젝트 기준점의 진북각도 표시값이 없습니다.");
                var rawAngle = angle.AsDouble() * 180.0 / Math.PI;
                if (!double.IsFinite(rawAngle))
                    throw new InvalidOperationException("프로젝트 기준점의 진북각도가 유효한 숫자가 아닙니다.");
                var normalizedAngle = (rawAngle % 360.0 + 360.0) % 360.0;
                var roundedAngle = Math.Round(normalizedAngle, 2, MidpointRounding.AwayFromZero);
                result.TrueNorthDegrees = roundedAngle == 360.0 ? 0.0 : roundedAngle;
                result.Status = "확인";
            }
        }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex)
        {
            result.NorthSouthMillimeters = result.EastWestMillimeters = result.ElevationMillimeters = result.TrueNorthDegrees = null;
            result.Status = "조회 실패: " + ex.Message;
        }
        try
        {
            var survey = BasePoint.GetSurveyPoint(doc);
            if (survey == null || site == null)
                result.SurveyStatus = "측량 기준점 또는 활성 공유대지를 찾을 수 없음";
            else
            {
                (result.SurveyNorthSouthMillimeters, result.SurveyEastWestMillimeters, result.SurveyElevationMillimeters) =
                    ReadCoordinates(survey, "측량 기준점");
                result.SurveyStatus = "확인";
            }
        }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex)
        {
            result.SurveyNorthSouthMillimeters = result.SurveyEastWestMillimeters = result.SurveyElevationMillimeters = null;
            result.SurveyStatus = "조회 실패: " + ex.Message;
        }
        return result;
    }

    private static (double NorthSouth, double EastWest, double Elevation) ReadCoordinates(BasePoint point, string label)
    {
        var ns = point.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM);
        var ew = point.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM);
        var elevation = point.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM);
        if (ns == null || ew == null || elevation == null || !ns.HasValue || !ew.HasValue || !elevation.HasValue)
            throw new InvalidOperationException(label + "의 공유대지 표시값이 없습니다.");
        var northSouth = UnitUtils.ConvertFromInternalUnits(ns.AsDouble(), UnitTypeId.Millimeters);
        var eastWest = UnitUtils.ConvertFromInternalUnits(ew.AsDouble(), UnitTypeId.Millimeters);
        var height = UnitUtils.ConvertFromInternalUnits(elevation.AsDouble(), UnitTypeId.Millimeters);
        if (!double.IsFinite(northSouth) || !double.IsFinite(eastWest) || !double.IsFinite(height))
            throw new InvalidOperationException(label + "의 좌표가 유효한 숫자가 아닙니다.");
        return (Math.Round(northSouth, 0, MidpointRounding.AwayFromZero),
            Math.Round(eastWest, 0, MidpointRounding.AwayFromZero),
            Math.Round(height, 0, MidpointRounding.AwayFromZero));
    }
}
