using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

// 용도 변경과 독립된 읽기 전용 점검. 한 Room의 문제 필드마다 한 행을 남긴다.
public sealed record RoomSpaceFinding(string Model, string ModelPath, string ElementId,
    string RoomName, string Field, string CurrentValue, string Issue);

public static class RoomSpaceAudit
{
    public static List<RoomSpaceFinding> Inspect(IEnumerable<ModelSnapshot> models)
    {
        var findings = new List<RoomSpaceFinding>();
        foreach (var model in models)
        {
            var roomOwners = model.Owners.Concat(model.Rows)
                .Where(r => r.Scope == "인스턴스" && r.CategoryKey == RoomUsePlanner.RoomCategory)
                .GroupBy(r => r.OwnerId, StringComparer.Ordinal);
            foreach (var room in roomOwners)
            {
                var rows = room.ToList();
                var name = rows.FirstOrDefault(r => r.Name == "실명");
                var roomName = Value(name);
                var elementId = rows.Select(r => r.ElementId).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? room.Key;
                foreach (var field in RoomUsePlanner.RequiredAuditFields)
                {
                    var matches = rows.Where(r => r.Name == field).ToList();
                    var value = matches.Count == 1 ? Value(matches[0]) : "";
                    var issue = matches.Count switch
                    {
                        0 => "매개변수 없음",
                        > 1 => "동명 매개변수 여러 개",
                        _ when value.Length == 0 => "빈값",
                        _ when string.Equals(value, "ND", StringComparison.OrdinalIgnoreCase) => "ND",
                        _ when field == "층" && !Regex.IsMatch(value, "^[0-9]{2}$") => "층 형식 오류: 두 자리 숫자 필요",
                        _ => ""
                    };
                    if (issue.Length > 0)
                        findings.Add(new RoomSpaceFinding(model.Title, model.Path, elementId, roomName,
                            field, value, issue));
                }
            }
        }
        return findings;
    }

    private static string Value(ParameterRow? row) => row == null || !row.HasValue
        ? "" : (row.Display.Length > 0 ? row.Display : row.Raw).Trim();
}
