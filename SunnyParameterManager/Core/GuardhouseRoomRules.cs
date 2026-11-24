using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

public sealed record GuardhouseRoomFinding(string Model, string ElementId, string Number, string Name, string RealName, string Fields);

public static class GuardhouseRoomRules
{
    public const string NameKey = "builtin:-1006900";
    public const string NumberKey = "builtin:-1006901";
    public const string LevelKey = "builtin:-1006916";
    private static readonly Regex FloorToken = new(
        @"(?<![\p{L}\p{N}])(?:지상\s*([12])\s*층|([12])\s*F)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Validate(RuleSet? file)
    {
        if (file?.Version != 13 || !file.GuardhouseRooms || string.IsNullOrWhiteSpace(file.Name) || file.Rules?.Count != 0 ||
            file.SampleOnly || file.Kac != null || file.Space != null || file.Spec != null || file.Cleanup ||
            file.PreserveWorksets?.Count != 0 || file.MaterialAudit || file.TypeCodeFix || file.ProjectBasePointAudit ||
            file.RebuildCentral || file.ProjectParameterTransfer || file.RoomUse != null || file.RoomReview)
            throw new InvalidOperationException("초소 룸 정리 룰셋은 버전13과 GuardhouseRooms만 사용합니다.");
    }

    public static RulePlan Plan(RuleSet file, IEnumerable<ParameterRow> available, IEnumerable<ValueEdit> pending)
    {
        Validate(file);
        var result = new RulePlan();
        var assignments = new Dictionary<string, string>();
        var rooms = available.Where(r => r.Scope == "인스턴스" && r.CategoryKey == RoomUsePlanner.RoomCategory)
            .GroupBy(r => r.ModelPath, StringComparer.OrdinalIgnoreCase);
        foreach (var model in rooms)
        {
            var modelName = Path.GetFileNameWithoutExtension(model.Key);
            if (!IsGuardhouseModel(model.Key))
            {
                result.Notes.Add($"{modelName} · 파일명에서 초소 또는 망루를 확인할 수 없어 제외");
                continue;
            }
            foreach (var room in model.GroupBy(r => r.OwnerId))
                PlanRoom(file, room.ToList(), result, assignments);
        }
        var deletions = result.Edits.Where(e => e.DeleteUnplacedRoom)
            .ToDictionary(e => (e.Original.ModelPath.ToUpperInvariant(), e.Original.OwnerId));
        foreach (var edit in pending)
        {
            var row = edit.Original;
            var deletion = deletions.GetValueOrDefault((row.ModelPath.ToUpperInvariant(), row.OwnerId));
            if (deletion != null && edit.DeleteUnplacedRoom && deletion.Original.Key == row.Key) continue;
            bool isInput = row.Scope == "인스턴스" && row.CategoryKey == RoomUsePlanner.RoomCategory &&
                (row.ParameterKey is NameKey or NumberKey or LevelKey || row.Name is "실명" or "지상지하" or "층");
            if (deletion != null || edit.DeleteUnplacedRoom ||
                isInput && (!assignments.TryGetValue(row.Key, out var value) || edit.Mode != "입력" || edit.Value != value))
                throw new InvalidOperationException($"{row.ModelTitle} / 룸 {row.ElementId} / {row.Name}: 기존 변경 예정이 초소 룸 정리 결과와 다릅니다. 해당 변경을 제외하고 다시 검토하세요.");
        }
        int deleteCount = result.Edits.Count(e => e.DeleteUnplacedRoom);
        result.Notes.Insert(0, $"초소 룸 {result.MatchedOwners:N0}개 검토 · 값 변경 {result.Edits.Count - deleteCount:N0}건 · 미배치 룸 삭제 {deleteCount:N0}개");
        return result;
    }

    public static bool IsGuardhouseModel(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("초소", StringComparison.Ordinal) || name.Contains("망루", StringComparison.Ordinal);
    }

    private static bool CopyRoomName(string name) => name is "단초소" or "TVOR초소" or "필로티";

    private static bool IsOtherName(string value)
    {
        var name = value.Trim();
        return name.Length > 0 && !CopyRoomName(name) && name is not ("초소" or "캐노피" or "처마") &&
            !name.Equals("ND", StringComparison.OrdinalIgnoreCase);
    }

    private static void PlanRoom(RuleSet file, List<ParameterRow> rows, RulePlan plan, Dictionary<string, string> assignments)
    {
        plan.MatchedOwners++;
        var first = rows[0];
        var label = $"{first.ModelTitle} / 룸 {first.ElementId}";
        ParameterRow? Find(string name, string? key = null)
        {
            var matches = rows.Where(r => key == null ? r.Name == name : r.ParameterKey == key).Take(2).ToList();
            if (matches.Count == 1) return matches[0];
            plan.Notes.Add($"{label} · {name}: {(matches.Count == 0 ? "매개변수 없음" : "동명 매개변수 여러 개")}");
            return null;
        }

        var realName = Find("실명");
        var name = Find("이름", NameKey);
        var number = Find("번호", NumberKey);
        string Text(ParameterRow? row) => row?.HasValue == true ? row.Raw : "";
        var realText = Text(realName);
        var nameText = Text(name);
        bool nameDiffers = IsOtherName(nameText);
        bool realNameDiffers = IsOtherName(realText);
        if (nameDiffers || realNameDiffers)
        {
            var fields = nameDiffers && realNameDiffers ? "이름, 실명" : nameDiffers ? "이름" : "실명";
            plan.GuardhouseRoomFindings.Add(new(Path.GetFileName(first.ModelPath), first.ElementId, Text(number), nameText, realText, fields));
        }
        if (first.RoomUnplaced)
        {
            var identity = number ?? name ?? rows.FirstOrDefault(r => r.ParameterKey.Length > 0);
            if (identity == null)
                throw new InvalidOperationException($"{label} · 미배치 룸의 식별 정보를 읽을 수 없습니다. 다시 조회하세요.");
            plan.Edits.Add(new ValueEdit
            {
                Original = identity, DeleteUnplacedRoom = true, Value = "일람표에서도 제거",
                Reason = "레벨: 배치 안 됨", RoomName = nameText
            });
            return;
        }
        static string NormalizeAlias(string value) => value.Trim() switch
        {
            "내부" => "초소",
            "외부" => "캐노피",
            _ => value
        };
        var normalizedName = NormalizeAlias(nameText);
        var trimmedName = normalizedName.Trim();
        var effectiveName = realName?.Storage != "String" ? "" :
            CopyRoomName(trimmedName) ? trimmedName :
            string.IsNullOrWhiteSpace(realText) ? normalizedName : NormalizeAlias(realText);
        var trimmed = effectiveName.Trim();
        var canopy = trimmed is "캐노피" or "처마";
        var guardhouse = trimmed is "초소" or "단초소" or "TVOR초소" || trimmed.Equals("ND", StringComparison.OrdinalIgnoreCase);
        var finalName = trimmed == "처마" ? "캐노피" : effectiveName;
        if (realName != null && realName.Storage != "String")
        {
            plan.Notes.Add($"{label} · 실명: 텍스트 매개변수가 아닙니다.");
            canopy = guardhouse = false;
        }

        var sources = new List<ParameterRow>();
        if (realName != null) sources.Add(realName);
        if (name != null) sources.Add(name);
        string? floor = canopy ? "01" : null;
        if (guardhouse)
        {
            var level = Find("레벨", LevelKey);
            if (level != null) sources.Add(level);
            var levels = FloorToken.Matches(level?.Display ?? "")
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .Distinct().ToList();
            if (levels.Count == 1) floor = "0" + levels[0];
            else plan.Notes.Add($"{label} · 레벨 '{level?.Display}'에서 지상 1층 또는 2층을 확인할 수 없어 기존 층 유지");
        }
        if (realName?.Storage == "String" && string.IsNullOrWhiteSpace(finalName))
            plan.Notes.Add($"{label} · 복사할 이름이 없어 실명 유지");
        else if (realName?.Storage == "String" && finalName.Length > 0)
            Set(realName, finalName);
        if (canopy && normalizedName.Trim() == "처마") normalizedName = "캐노피";
        if (normalizedName != nameText) Set(name, normalizedName);
        if (canopy || guardhouse) Set(Find("지상지하"), "F");
        if (floor != null) Set(Find("층"), floor);
        Set(number, "001");

        void Set(ParameterRow? target, string value)
        {
            if (target == null) return;
            assignments[target.Key] = value;
            if (target.Storage == "String" && target.HasValue && target.Raw == value) return;
            var error = target.Storage == "String" ? EditPlanner.Validate(target, value) : "텍스트 매개변수가 아닙니다.";
            if (error.Length > 0)
            {
                plan.Notes.Add($"{label} · {target.Name}: {error}");
                return;
            }
            plan.Edits.Add(new ValueEdit
            {
                Original = target, Value = value, Reason = "룰셋: " + file.Name, RoomName = finalName,
                GuardhouseRoomSources = sources
            });
        }
    }
}
