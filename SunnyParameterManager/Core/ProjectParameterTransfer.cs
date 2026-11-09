namespace SunnyParameterManager.Core;

// 기준 모델의 모든 프로젝트 매개변수 정의를 대상별로 비교한다. 값은 전송하지 않는다.
public static class ProjectParameterTransfer
{
    public sealed record Decision(ParameterDefinitionInfo Source, string Status, string Reason);

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
            if (sourceDefs.Count(d => d.Name == def.Name) != 1)
            { result.Add(Block("기준 모델에 같은 이름의 정의가 여러 개 있습니다.")); continue; }
            var sameName = targetDefs.Where(d => d.Name == def.Name).ToList();
            var sameGuid = def.Guid.Length == 0 ? [] : targetDefs.Where(d => d.Guid.Equals(def.Guid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameName.Count > 1 || sameGuid.Count > 1 || sameGuid.Any(d => d.Name != def.Name))
            { result.Add(Block("대상에 같은 이름 또는 GUID의 서로 다른 정의가 있습니다.")); continue; }
            if (sameName.Count == 1)
            {
                result.Add(SameDefinition(def, sameName[0])
                    ? new(def, "이미 있음", "정의와 카테고리 연결이 일치합니다.")
                    : Block("동명 정의의 GUID·자료형·그룹·유형/인스턴스·카테고리 연결이 다릅니다. 기존 값 보존을 위해 자동 덮어쓰지 않습니다."));
                continue;
            }
            if (def.Categories.Any(c => !target.Definitions.AllCategories.Contains(c, StringComparer.Ordinal)))
            { result.Add(Block("대상에서 사용할 수 없는 카테고리: " + string.Join(", ", def.Categories.Except(target.Definitions.AllCategories)))); continue; }
            result.Add(new(def, "추가 예정", "일반·공유 프로젝트 매개변수 정의와 연결 카테고리를 복사합니다."));
        }
        return result;
    }

    public static bool SameDefinition(ParameterDefinitionInfo a, ParameterDefinitionInfo b) =>
        a.Name == b.Name && a.Guid.Equals(b.Guid, StringComparison.OrdinalIgnoreCase) &&
        a.DataType == b.DataType && a.Group == b.Group && a.Binding == b.Binding &&
        a.Categories.SequenceEqual(b.Categories, StringComparer.Ordinal);

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
