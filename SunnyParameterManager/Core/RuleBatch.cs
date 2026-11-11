namespace SunnyParameterManager.Core;

public static class RuleBatch
{
    public static QueryPlan Query(IReadOnlyList<RuleSet> files)
    {
        if (files.Count == 1) return QueryPlan.FromRules(files[0]);
        if (files.Count < 2 || files.Any(f => f.Version is not (1 or 2 or 3 or 11 or 12)) ||
            files.Count(f => f.RoomReview) > 1)
            throw new InvalidOperationException("여러 룰셋은 일반 값 입력·KAC 속성·공간파일·용도구분과 룸 검토 1개를 함께 선택할 수 있습니다. 전용 작업 룰셋은 단독으로 선택하세요.");
        var queries = files.Select(QueryPlan.FromRules).ToList();
        var query = new QueryPlan
        {
            Scopes = queries.SelectMany(q => q.Scopes).Distinct().ToList(),
            Names = queries.SelectMany(q => q.Names).Distinct().ToList(),
            Keys = queries.SelectMany(q => q.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            IncludeOuterMaterial = queries.Any(q => q.IncludeOuterMaterial),
            IncludeRoomUse = queries.Any(q => q.IncludeRoomUse),
            IncludeOtherElements = queries.Any(q => !q.IncludeRoomUse),
            RoomIdentityVersion = queries.Max(q => q.RoomIdentityVersion)
        };
        query.Validate();
        return query;
    }

    public static RulePlan Plan(IReadOnlyList<RuleSet> files, IReadOnlyList<ParameterRow> rows,
        IEnumerable<ValueEdit> pending, bool sample)
    {
        Query(files);
        var staged = pending.ToDictionary(e => e.Original.Key);
        if (staged.Values.Any(e => e.DefinitionFix != null || e.KacCleanup))
            throw new InvalidOperationException("매개변수 정의 또는 유형 정리 변경을 먼저 적용하거나 제외하세요.");
        var result = new RulePlan();
        var writers = files.Where(f => !f.RoomReview).ToList();
        RulePlan Evaluate(RuleSet file)
        {
            var relevant = file.RoomUse == null ? staged.Values.AsEnumerable() :
                staged.Values.Where(e => e.Original.CategoryKey == RoomUsePlanner.RoomCategory &&
                    e.Original.Scope == "인스턴스" && e.Field == "용도");
            try { return RulePlanner.Plan(file, rows, relevant, sample); }
            catch (InvalidOperationException ex)
            { throw new InvalidOperationException($"{file.Name}: {ex.Message}", ex); }
        }
        foreach (var file in writers)
        {
            var plan = Evaluate(file);
            result.Notes.Add(file.Name);
            result.Notes.AddRange(plan.Notes);
            foreach (var edit in plan.Edits)
            {
                if (staged.TryGetValue(edit.Original.Key, out var prior))
                {
                    if (edit.Mode != prior.Mode || edit.Value != prior.Value || edit.ReferenceKey != prior.ReferenceKey)
                        throw new InvalidOperationException($"{file.Name}: {edit.Model} / {edit.Field}의 변경값이 충돌합니다.");
                    edit.AddParameter ??= prior.AddParameter;
                    edit.MaterialSource ??= prior.MaterialSource;
                    if (edit.RoomName.Length == 0) edit.RoomName = prior.RoomName;
                }
                staged[edit.Original.Key] = edit;
                result.Edits.Add(edit);
            }
        }
        // 먼저 평가한 룰셋의 동일값 제안도 나중 룰셋이 덮어쓰지 못하도록 전체 변경과 다시 대조한다.
        foreach (var file in writers) Evaluate(file);
        var unique = result.Edits.Select(e => e.Original.Key).Distinct().Select(k => staged[k]).ToList();
        result.Edits.Clear();
        result.Edits.AddRange(unique);
        return result;
    }
}
