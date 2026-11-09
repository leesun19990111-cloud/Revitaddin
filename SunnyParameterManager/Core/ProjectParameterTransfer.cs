namespace SunnyParameterManager.Core;

// 기준 모델의 모든 프로젝트 매개변수 정의를 대상별로 비교한다. 값은 전송하지 않는다.
public static class ProjectParameterTransfer
{
    public sealed record Decision(ParameterDefinitionInfo Source, string Status, string Reason, ParameterDefinitionInfo? Target = null);

    public static bool IsActionable(Decision decision) =>
        decision.Status is "추가 예정" or "설정 변경 예정" or "정의 교체 예정";

    public static QueryPlan DefinitionQuery() => new()
    {
        Scopes = ["프로젝트 정보"], Names = ["매개변수 정의 목록"], IncludeDefinitions = true, TransferOnly = true
    };

    public static IReadOnlyList<Decision> Plan(ModelSnapshot source, ModelSnapshot target)
    {
        if (source.Definitions == null || target.Definitions == null || source.Query?.TransferOnly != true || target.Query?.TransferOnly != true)
            throw new InvalidOperationException("기준 및 대상 모델의 프로젝트 매개변수 정의를 다시 조회하세요.");
        if (source.Identity == target.Identity || string.Equals(source.CanonicalPath, target.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("기준 파일과 같은 모델을 대상으로 전송할 수 없습니다.");
        var result = new List<Decision>();
        var sourceDefs = source.Definitions.Definitions;
        var targetDefs = target.Definitions.Definitions;
        foreach (var def in sourceDefs)
        {
            Decision Block(string reason) => new(def, "충돌", reason);
            if (string.IsNullOrWhiteSpace(def.ElementId) || string.IsNullOrWhiteSpace(def.ElementUniqueId) || def.Categories.Count == 0)
            { result.Add(Block("원본 정의의 요소 ID 또는 연결 카테고리를 확인할 수 없습니다.")); continue; }
            if (sourceDefs.Count(d => d.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase)) != 1)
            { result.Add(Block("기준 모델에 같은 이름의 정의가 여러 개 있습니다.")); continue; }
            var sameName = targetDefs.Where(d => d.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var sameGuid = def.Guid.Length == 0 ? [] : targetDefs.Where(d => d.Guid.Equals(def.Guid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameName.Count > 1 || sameGuid.Count > 1 || sameGuid.Any(d => !d.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase)))
            { result.Add(Block("대상에 같은 이름 또는 GUID의 서로 다른 정의가 있습니다.")); continue; }
            if (def.Categories.Any(c => !target.Definitions.AllCategories.Contains(c, StringComparer.Ordinal)))
            { result.Add(Block("대상에서 사용할 수 없는 카테고리: " + string.Join(", ", def.Categories.Except(target.Definitions.AllCategories)))); continue; }
            if (sameName.Count == 1)
            {
                var old = sameName[0];
                if (SameDefinition(def, old)) result.Add(new(def, "이미 있음", "정의와 카테고리 연결이 일치합니다.", old));
                else
                {
                    // 비공유 프로젝트 매개변수의 카테고리/유형 바인딩은 ReInsert가 거부된다.
                    // 그룹만 다르면 제자리 변경하고, 나머지는 원본 정의를 복사해 교체한다.
                    var sameConnection = old.Binding == def.Binding &&
                        old.Categories.SequenceEqual(def.Categories, StringComparer.Ordinal);
                    var compatible = def.Guid.Equals(old.Guid, StringComparison.OrdinalIgnoreCase) &&
                        def.DataType == old.DataType && (def.Guid.Length > 0 || sameConnection);
                    var detail = Differences(old, def);
                    result.Add(new(def, compatible ? "설정 변경 예정" : "정의 교체 예정",
                        detail + (compatible
                            ? " · 기준 파일의 연결 설정으로 변경합니다. 제외되는 카테고리의 기존 값은 사라질 수 있습니다."
                            : " · 대상 정의를 제거하고 기준 정의로 교체합니다. 이 매개변수에 입력된 대상 값은 사라집니다."), old));
                }
                continue;
            }
            result.Add(new(def, "추가 예정", "일반·공유 프로젝트 매개변수 정의와 연결 카테고리를 복사합니다."));
        }
        return result;
    }

    public static bool SameDefinition(ParameterDefinitionInfo a, ParameterDefinitionInfo b) =>
        a.Name == b.Name && a.Guid.Equals(b.Guid, StringComparison.OrdinalIgnoreCase) &&
        a.DataType == b.DataType && a.Group == b.Group && a.Binding == b.Binding &&
        a.Categories.SequenceEqual(b.Categories, StringComparer.Ordinal);

    private static string Differences(ParameterDefinitionInfo old, ParameterDefinitionInfo source)
    {
        var items = new List<string>();
        if (!old.Guid.Equals(source.Guid, StringComparison.OrdinalIgnoreCase)) items.Add("GUID 다름");
        if (old.DataType != source.DataType) items.Add("자료형 다름");
        if (old.Group != source.Group) items.Add("그룹 다름");
        if (old.Binding != source.Binding) items.Add($"유형/인스턴스 {old.Binding}→{source.Binding}");
        if (!old.Categories.SequenceEqual(source.Categories, StringComparer.Ordinal))
            items.Add($"연결 카테고리 {old.Categories.Count}→{source.Categories.Count}개");
        return string.Join(" · ", items);
    }

    // 조회 뒤 모델이 바뀌면 전송하지 않는다. 로컬 경로에서 새로 연 문서의 요소 ID는 유지된다.
    public static bool SameSnapshot(DefinitionSnapshot expected, DefinitionSnapshot actual)
    {
        if (expected.Definitions.Count != actual.Definitions.Count) return false;
        if (expected.Definitions.Any(d => string.IsNullOrWhiteSpace(d.ElementUniqueId)) ||
            actual.Definitions.Any(d => string.IsNullOrWhiteSpace(d.ElementUniqueId)) ||
            actual.Definitions.Select(d => d.ElementUniqueId).Distinct(StringComparer.Ordinal).Count() != actual.Definitions.Count)
            return false;
        var current = actual.Definitions.ToDictionary(d => d.ElementUniqueId, StringComparer.Ordinal);
        return expected.Definitions.All(d => current.TryGetValue(d.ElementUniqueId, out var now) &&
            d.ElementId == now.ElementId && d.Revision == now.Revision && SameDefinition(d, now));
    }
}
