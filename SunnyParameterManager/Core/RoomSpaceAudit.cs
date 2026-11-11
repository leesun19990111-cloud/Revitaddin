using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

// 용도 변경과 독립된 읽기 전용 점검. 한 Room의 문제 필드마다 한 행을 남긴다.
public sealed record RoomSpaceFinding(string Model, string ModelPath, string ElementId,
    string RevitName, string Number, string RoomName, string Field, string CurrentValue, string Issue);

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
                var identity = rows.FirstOrDefault(r => r.Name.Length == 0) ?? rows[0];
                var revitName = identity.ElementName.Trim();
                var numberRows = rows.Where(r => r.Name == "번호").ToList();
                var number = numberRows.Count == 1 ? Value(numberRows[0]) : "";
                var elementId = rows.Select(r => r.ElementId).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? room.Key;
                var fields = rows.Where(r => r.Name.Length > 0).ToLookup(r => r.Name, StringComparer.Ordinal);
                string SingleValue(string field) => fields[field].Count() == 1 ? Value(fields[field].First()) : "";
                var codeParts = new[] { "용도", "지상지하", "층", "번호" }.Select(SingleValue).ToArray();
                foreach (var field in RoomReviewRules.Fields.Concat(["모델명", "이름"]))
                {
                    var count = field is "모델명" or "이름" ? 1 : fields[field].Count();
                    var value = field switch { "모델명" => model.Title.Trim(), "이름" => revitName, _ => SingleValue(field) };
                    var issue = count switch
                    {
                        0 => "매개변수 없음",
                        > 1 => "동명 매개변수 여러 개",
                        _ when value.Length == 0 => "빈값",
                        _ when field is ("실명" or "번호" or "지상지하" or "층") &&
                            string.Equals(value, "ND", StringComparison.OrdinalIgnoreCase) => "ND",
                        _ when field == "층" && !Regex.IsMatch(value, "^[0-9]{2}$") => "층 형식 오류: 두 자리 숫자 필요",
                        _ when field == "번호" && !Regex.IsMatch(value, "^[0-9]{3}$") => "번호 형식 오류: 세 자리 숫자 필요",
                        _ when field == "실번호" && codeParts.All(p => p.Length > 0) &&
                            value != string.Concat(codeParts) => "실번호 불일치: " + string.Concat(codeParts),
                        _ => ""
                    };
                    if (field == "실명" && issue == "매개변수 없음")
                        issue += " — 용도 변경 제외";
                    if (issue.Length > 0)
                        findings.Add(new RoomSpaceFinding(model.Title, model.Path, elementId, revitName, number, roomName,
                            field, value, issue));
                }
            }
        }
        return findings;
    }

    private static string Value(ParameterRow? row) => row == null || !row.HasValue
        ? "" : (row.Display.Length > 0 ? row.Display : row.Raw).Trim();
}
