namespace SunnyParameterManager.Core;

// 룸 검토는 값을 쓰지 않는다. 이름은 실제 Revit 매개변수명이며 순서는 모델명·이름을 제외한 표의 열 순서다.
public static class RoomReviewRules
{
    public const string UnplacedNotice = "일람표에서 삭제 요망";

    public sealed record UnplacedRoom(string ElementId, string Number, string Name, string RealName);

    public static List<UnplacedRoom> UnplacedRooms(ModelSnapshot model)
    {
        if (model.RoomIdentityVersion < 3)
            throw new InvalidOperationException(model.Title + " · 미배치 룸의 배치 상태를 확인하려면 RVT를 다시 조회하세요.");
        return model.Owners.Concat(model.Rows)
            .Where(r => r.Scope == "인스턴스" && r.CategoryKey == RoomUsePlanner.RoomCategory)
            .GroupBy(r => r.OwnerId)
            .Where(g => g.Any(r => r.RoomUnplaced))
            .Select(g =>
            {
                var identity = g.FirstOrDefault(r => r.Name.Length == 0) ?? g.First();
                string Text(string field)
                {
                    var rows = g.Where(r => r.Name == field).Take(2).ToList();
                    return rows.Count == 1 && rows[0].HasValue ? (rows[0].Display.Length > 0 ? rows[0].Display : rows[0].Raw).Trim() : "";
                }
                return new UnplacedRoom(g.Select(r => r.ElementId).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? g.Key,
                    Text("번호"), identity.ElementName, Text("실명"));
            })
            .OrderBy(r => r.Number, StringComparer.Ordinal)
            .ThenBy(r => r.ElementId, StringComparer.Ordinal).ToList();
    }

    public static readonly string[] Fields = ["용도", "지상지하", "층", "설명", "번호", "실번호", "기존 실번호",
        "실명", "자산번호", "LOD", "공간분류코드", "구역", "실ID", "실내재료마감-걸레받이",
        "실내재료마감-바닥", "실내재료마감-벽", "실내재료마감-천장", "작성기준"];

    // 사용자가 지정한 필수 입력 항목의 공란은 엑셀에서 해당 셀만 강하게 표시한다.
    public static readonly string[] RequiredFields = ["용도", "지상지하", "층", "번호", "실명", "공간분류코드",
        "실내재료마감-걸레받이", "실내재료마감-바닥", "실내재료마감-벽", "실내재료마감-천장"];

    public static void Validate(RuleSet? file)
    {
        if (file?.Version != 12 || !file.RoomReview || string.IsNullOrWhiteSpace(file.Name) || file.SampleOnly ||
            file.Rules?.Count != 0 || file.RoomUse != null || file.Kac != null || file.Space != null || file.Spec != null ||
            file.Cleanup || file.MaterialAudit || file.TypeCodeFix || file.ProjectBasePointAudit || file.RebuildCentral ||
            file.ProjectParameterTransfer || file.PreserveWorksets?.Count != 0)
            throw new InvalidOperationException("룸 검토 룰셋은 버전12의 읽기 전용 RoomReview만 사용합니다.");
    }
}
