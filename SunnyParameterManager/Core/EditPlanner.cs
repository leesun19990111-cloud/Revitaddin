using System.Globalization;

namespace SunnyParameterManager.Core;

public static class EditPlanner
{
    public static string Validate(ParameterRow row, string value)
    {
        if (!row.Editable) return row.BlockReason.Length > 0 ? row.BlockReason : "지원하지 않는 편집 형식입니다.";
        if (row.Editor == "텍스트") return "";
        if (row.Editor == "정수") return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? "" : "정수를 입력하세요.";
        if (row.Editor is "선택" or "예/아니요")
            return row.Choices.Count(c => c.Name == value) == 1 ? "" : "이 모델의 선택 목록에서 유일한 값을 선택하세요.";
        if (row.Editor == "숫자")
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? "" : "유한한 숫자를 입력하세요. 소수점은 점(.)입니다.";
        if (row.Editor == "단위 숫자") return string.IsNullOrWhiteSpace(value) ? "단위를 포함한 숫자를 입력하세요." : "";
        return "지원하지 않는 편집 형식입니다.";
    }

    // 선택 집합 전체를 검사한 다음에만 반환한다. 일부 성공처럼 보이는 묵시적 건너뛰기를 하지 않는다.
    public static List<ValueEdit> Stage(IEnumerable<ParameterRow> selected, string value)
    {
        var rows = selected.DistinctBy(r => r.Key).ToList();
        if (rows.Count == 0) throw new InvalidOperationException("편집할 행을 선택하세요.");
        var errors = rows.Select(r => (r, error: Validate(r, value))).Where(x => x.error.Length > 0).ToList();
        if (errors.Count > 0) throw new InvalidOperationException($"{errors.Count}개 행을 편집할 수 없습니다.\n" + string.Join("\n", errors.Take(8).Select(x => $"{x.r.ModelTitle} / {x.r.TypeName} / {x.r.Name}: {x.error}")));
        return rows.Where(r => r.Display != value || !r.HasValue).Select(r => new ValueEdit { Original = r, Value = value, ReferenceKey = r.Editor == "선택" ? r.Choices.Single(c => c.Name == value).Key : "" }).ToList();
    }

    public static List<ValueEdit> CopyTypeValues(ModelSnapshot source, IEnumerable<ParameterRow> targets, bool approveNamedParameters)
    {
        var edits = new List<ValueEdit>();
        var errors = new List<string>();
        var index = source.Rows.GroupBy(r => r.TypeKey).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var target in targets.DistinctBy(r => r.Key))
        {
            if (string.Equals(target.ModelPath, source.Path, StringComparison.OrdinalIgnoreCase)) { errors.Add("기준 모델 자체가 대상에 포함되어 있습니다."); continue; }
            if (target.Scope is "뷰" or "시트") { errors.Add("뷰·시트는 이름만으로 모델 간 자동 연결하지 않습니다. 직접 입력을 사용하세요."); continue; }
            if (target.Scope == "인스턴스") { errors.Add("인스턴스는 유형명이 같아도 동일 요소가 아니므로 자동 복사하지 않습니다. 직접 일괄 입력을 사용하세요."); continue; }
            if (!target.Editable) { errors.Add($"{target.TypeName}: {target.BlockReason}"); continue; }
            var candidates = index.GetValueOrDefault(target.TypeKey, []).Where(r => SameParameter(r, target, approveNamedParameters)).ToList();
            if (candidates.Count != 1) { errors.Add($"{target.ModelTitle} / {target.TypeName} / {target.Name}: 기준값 {candidates.Count}개 — 유일하게 연결되지 않습니다."); continue; }
            var s = candidates[0];
            if (!s.HasValue) { errors.Add($"{s.TypeName} / {s.Name}: 기준값이 없습니다. 값 지우기는 직접 입력으로 확인하세요."); continue; }
            if (s.Editor != target.Editor) { errors.Add($"{s.Name}: 값 형식이 다릅니다."); continue; }
            if (target.Editor is "선택" or "예/아니요")
            {
                var error = Validate(target, s.Display);
                if (error.Length > 0) { errors.Add($"{target.ModelTitle}: {error}"); continue; }
            }
            var reference = target.Editor == "선택";
            if (target.HasValue && (reference ? target.Display == s.Display : target.Raw == s.Raw)) continue;
            edits.Add(new ValueEdit { Original = target, Value = s.Display, Mode = reference ? "입력" : "원시값", RawValue = reference ? "" : s.Raw, ReferenceKey = reference ? target.Choices.Single(c => c.Name == s.Display).Key : "", Reason = "기준 모델: " + source.Title });
        }
        if (errors.Count > 0) throw new InvalidOperationException("복사를 보류했습니다. 연결되지 않은 대상을 제외하고 다시 선택하세요.\n" + string.Join("\n", errors.Distinct().Take(12)));
        return edits;
    }

    public static bool SameParameter(ParameterRow a, ParameterRow b, bool approveNamed)
    {
        if (a.DataType != b.DataType || a.Storage != b.Storage || a.Scope != b.Scope) return false;
        if (a.ParameterKey.StartsWith("guid:") || b.ParameterKey.StartsWith("guid:")) return a.ParameterKey == b.ParameterKey;
        if (a.ParameterKey.StartsWith("builtin:") || b.ParameterKey.StartsWith("builtin:")) return a.ParameterKey == b.ParameterKey;
        return approveNamed && a.Name == b.Name;
    }

    public static void CheckFresh(ParameterRow expected, ParameterRow actual)
    {
        if (expected.OwnerId != actual.OwnerId || expected.ParameterKey != actual.ParameterKey || expected.Scope != actual.Scope ||
            expected.Name != actual.Name || expected.DataType != actual.DataType || expected.Storage != actual.Storage || expected.Editor != actual.Editor ||
            expected.TypeKey != actual.TypeKey || expected.TypeOwnerId != actual.TypeOwnerId || expected.Unit != actual.Unit || expected.HasValue != actual.HasValue || expected.Raw != actual.Raw || !actual.Editable)
            throw new InvalidOperationException($"{expected.TypeName} / {expected.Name}: 조회 후 대상·값·단위 또는 편집 권한이 달라졌습니다. 다시 조회하세요.");
    }
}
