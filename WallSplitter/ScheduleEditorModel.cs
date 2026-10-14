using System;
using System.Collections.Generic;
using System.Linq;

namespace WallSplitter
{
    internal enum ScheduleEditorKind { Text, Integer, Number, Boolean, Reference, ReadOnly }
    internal sealed class ScheduleEditorField
    {
        internal string Id = "", Name = "", Reason = "";
        internal bool IsType, IsProjectInfo, IsIfcEntity;
        internal ScheduleEditorKind Editor;
        internal string Hint = "";
        public override string ToString() => Name + (Reason.Length > 0 || Editor == ScheduleEditorKind.ReadOnly ? " [읽기 전용]" : IsProjectInfo ? " [프로젝트 전체에 영향]" : IsType ? " [유형: 다른 행에도 영향]" : "");
    }
    internal sealed class ScheduleEditorRow
    {
        internal int Number;
        internal string Models = "", Identity = "", Reason = "";
        internal int ElementCount;
        internal List<int> RequiredRows = new();
        internal readonly Dictionary<string, List<string>> ParameterMappings = new();
        internal readonly Dictionary<string, string> Cells = new();
        internal readonly Dictionary<string, List<UnifiedRow>> Targets = new();
        internal readonly Dictionary<string, string> Errors = new();
    }
    internal sealed class ScheduleEditorSnapshot
    {
        internal string ScheduleId = "", Name = "";
        internal readonly List<ScheduleEditorField> Fields = new();
        internal readonly List<ScheduleEditorRow> Rows = new();
        internal readonly Dictionary<string, UnifiedRow> Pending = new();
        internal readonly List<ScheduleEditorGuard> Guards = new();
        internal readonly HashSet<string> ApprovedMappings = new();
    }
    internal sealed class ScheduleEditorGuard
    {
        internal string SourceKey = "", ElementId = "", TypeId = "";
        internal readonly List<UnifiedRow> Values = new();
    }
    internal static class ScheduleEditorLogic
    {
        internal sealed class RowMatch
        {
            internal List<int> Members = new(), RequiredRows = new();
            internal string Reason = "";
        }
        // 개별 대응을 추측하지 않는다. 동일 표시 행은 전체 집합/개수를 확인한 경우에만 함께 수정한다.
        internal static List<RowMatch> MatchRows(IList<string> displayed, IList<string> candidates, IList<string> raw, IList<int> counts, bool itemized)
        {
            var result = displayed.Select(_ => new RowMatch()).ToList();
            var pools = candidates.Select((key, i) => (key, i)).GroupBy(p => p.key).ToDictionary(g => g.Key, g => g.Select(p => p.i).ToList());
            foreach (var rows in displayed.Select((key, i) => (key, i)).GroupBy(p => p.key))
            {
                var indices = rows.Select(p => p.i).ToList();
                pools.TryGetValue(rows.Key, out var members); members ??= new List<int>();
                string error = members.Count != indices.Sum(i => counts[i]) ? "행 표시 개수와 실제 대상 개수가 다릅니다. 링크 상태와 일람표를 갱신해 주세요."
                    : members.Select(i => raw[i]).Distinct().Count() > 1 ? "같은 표시값에 서로 다른 원시값이 있습니다. 반올림/동명 참조를 구분할 수 없습니다."
                    : (!itemized && indices.Count > 1) || (itemized && indices.Any(i => counts[i] != 1)) ? "묶인 행의 개별 대응을 확인할 수 없습니다." : "";
                var required = indices.Count > 1 ? indices.Select(i => i + 1).ToList() : new List<int>();
                foreach (int index in indices)
                {
                    result[index].Reason = error;
                    if (error.Length > 0) continue;
                    result[index].Members = members;
                    result[index].RequiredRows = required;
                }
            }
            return result;
        }
        private static void ValidateSelection(ScheduleEditorSnapshot data, ScheduleEditorField field, HashSet<int> numbers)
        {
            foreach (var row in data.Rows.Where(r => numbers.Contains(r.Number)))
            {
                if (row.RequiredRows.Any(n => !numbers.Contains(n)))
                    throw new InvalidOperationException(row.Number + "행은 동일 표시 행 중 하나입니다. ‘동일 표시 행 함께 선택’ 후 적용하세요. 함께 선택할 행: " + string.Join(", ", row.RequiredRows));
                if (row.ParameterMappings.TryGetValue(field.Id, out var mappings) && mappings.Any(m => !data.ApprovedMappings.Contains(m)))
                    throw new InvalidOperationException("‘프로젝트 매개변수 연결 확인’에서 모델별 연결 항목을 승인한 뒤 적용하세요. 이름만으로 자동 수정하지 않습니다.");
            }
        }
        internal static string Key(IEnumerable<string> values) => string.Concat(values.Select(v => v.Length + ":" + v));
        internal static HashSet<int> ParseRows(string input, int count)
        {
            var result = new HashSet<int>();
            foreach (string part in input.Split(','))
            {
                var bounds = part.Trim().Split('-');
                if (bounds.Length > 2 || !int.TryParse(bounds[0], out int first) || first < 1 || first > count)
                    throw new InvalidOperationException("행 번호는 1부터 " + count + "까지입니다. 예: 1-10,12,15");
                int last = first;
                if (bounds.Length == 2 && (!int.TryParse(bounds[1], out last) || last < first || last > count))
                    throw new InvalidOperationException("행 범위를 확인하세요. 예: 1-10,12,15");
                for (int i = first; i <= last; i++) result.Add(i);
            }
            return result;
        }
        // 두 화면 행을 구분할 수 없는 키는 순서로 추측하지 않는다.
        internal static List<List<int>> Match(IList<string> displayed, IList<string> candidates, bool itemized)
        {
            if (displayed.Distinct(StringComparer.Ordinal).Count() != displayed.Count)
                throw new InvalidOperationException("같은 식별값의 행이 여러 개라 실제 요소를 구분할 수 없습니다. 고유한 마크 등 요소를 구분할 직접 매개변수 필드를 추가해 주세요.");
            var groups = candidates.Select((key, index) => (key, index)).GroupBy(p => p.key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(p => p.index).ToList(), StringComparer.Ordinal);
            if (groups.Count != displayed.Count || displayed.Any(key => !groups.ContainsKey(key)))
                throw new InvalidOperationException("일람표의 정렬/필터 행과 실제 요소가 일치하지 않습니다. 모호한 대상을 수정하지 않습니다.");
            var result = displayed.Select(key => groups[key]).ToList();
            if (itemized && result.Any(g => g.Count != 1)) throw new InvalidOperationException("개별 행과 실제 요소를 1:1로 구분할 수 없습니다. 고유한 직접 매개변수 필드를 추가하세요.");
            return result;
        }
        internal static void Stage(ScheduleEditorSnapshot data, ScheduleEditorField field, HashSet<int> numbers, string value)
        {
            if (field.Reason.Length > 0) throw new InvalidOperationException(field.Reason);
            ValidateSelection(data, field, numbers);
            var targets = new Dictionary<string, UnifiedRow>();
            foreach (var row in data.Rows.Where(r => numbers.Contains(r.Number)))
            {
                if (row.Reason.Length > 0) throw new InvalidOperationException(row.Number + "행: " + row.Reason);
                if (row.Errors.TryGetValue(field.Id, out var error)) throw new InvalidOperationException(row.Number + "행: " + error);
                if (!row.Targets.TryGetValue(field.Id, out var list) || list.Count == 0) throw new InvalidOperationException(row.Number + "행: 수정 대상을 확인할 수 없습니다.");
                foreach (var target in list)
                {
                    if (target.ProjectValue?.ReferenceKind.Length > 0) throw new InvalidOperationException("참조 필드는 모델별 목록에서 대상을 선택하세요.");
                    if (!target.Editable) throw new InvalidOperationException(row.Number + "행: " + target.Source.Name + " · " + target.Source.BlockReason + target.ProjectValue?.ReadOnlyReason);
                    targets[target.Key] = target;
                }
            }
            if (targets.Count == 0) throw new InvalidOperationException("변경할 행을 지정하세요.");
            // 먼저 전부 검증한다. 한 셀이라도 실패하면 기존 미리보기를 그대로 보존한다.
            foreach (var target in targets.Values)
            {
                string previous = target.Working; target.Working = value;
                string? error = ProjectInformationService.InputError(target); target.Working = previous;
                if (error != null) throw new InvalidOperationException(error);
            }
            foreach (var target in targets.Values) { target.Working = value; if (target.Changed) data.Pending[target.Key] = target; else data.Pending.Remove(target.Key); }
        }
        internal static void StageReferences(ScheduleEditorSnapshot data, ScheduleEditorField field, HashSet<int> numbers, Dictionary<string, string> choices)
        {
            if (field.Reason.Length > 0) throw new InvalidOperationException(field.Reason);
            ValidateSelection(data, field, numbers);
            var targets = new Dictionary<string, UnifiedRow>();
            foreach (var row in data.Rows.Where(r => numbers.Contains(r.Number)))
            {
                if (row.Reason.Length > 0) throw new InvalidOperationException(row.Reason);
                if (row.Errors.TryGetValue(field.Id, out string? error)) throw new InvalidOperationException(error);
                if (!row.Targets.TryGetValue(field.Id, out var list) || list.Count == 0) throw new InvalidOperationException("행의 수정 대상을 확인할 수 없습니다.");
                foreach (var target in list)
                {
                    var value = target.ProjectValue;
                    if (!target.Editable || value == null || value.ReferenceKind.Length == 0 ||
                        !choices.TryGetValue(target.Source.Key, out string? id) || !value.ReferenceChoices.ContainsKey(id))
                        throw new InvalidOperationException(target.Source.Name + ": 편집 가능 여부와 해당 모델의 참조 선택을 확인하세요.");
                    targets[target.Key] = target;
                }
            }
            if (targets.Count == 0) throw new InvalidOperationException("변경할 행을 지정하세요.");
            foreach (var target in targets.Values)
            {
                var value = target.ProjectValue!; value.SelectedReference = choices[target.Source.Key];
                target.Working = value.SelectedReference == value.OriginalReference ? target.Original : value.ReferenceChoices[value.SelectedReference];
                if (target.Changed) data.Pending[target.Key] = target; else data.Pending.Remove(target.Key);
            }
        }
    }
}
