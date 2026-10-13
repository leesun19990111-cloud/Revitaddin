using System;
using System.Collections.Generic;
using System.Linq;

namespace WallSplitter
{
    internal sealed class ScheduleEditorField
    {
        internal string Id = "", Name = "", Reason = "";
        internal bool IsType;
        public override string ToString() => Name + (Reason.Length > 0 ? " [읽기 전용]" : IsType ? " [유형: 다른 행에도 영향]" : "");
    }
    internal sealed class ScheduleEditorRow
    {
        internal int Number;
        internal string Models = "", Identity = "", Reason = "";
        internal int ElementCount;
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
    }
    internal sealed class ScheduleEditorGuard
    {
        internal string SourceKey = "", ElementId = "", TypeId = "";
        internal readonly List<UnifiedRow> Values = new();
    }
    internal static class ScheduleEditorLogic
    {
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
                throw new InvalidOperationException("같은 식별값의 행이 여러 개라 실제 요소를 구분할 수 없습니다. 개별 행은 고유한 마크 등 텍스트 필드를 추가해 주세요.");
            var groups = candidates.Select((key, index) => (key, index)).GroupBy(p => p.key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(p => p.index).ToList(), StringComparer.Ordinal);
            if (groups.Count != displayed.Count || displayed.Any(key => !groups.ContainsKey(key)))
                throw new InvalidOperationException("일람표의 정렬/필터 행과 실제 요소가 일치하지 않습니다. 모호한 대상을 수정하지 않습니다.");
            var result = displayed.Select(key => groups[key]).ToList();
            if (itemized && result.Any(g => g.Count != 1)) throw new InvalidOperationException("개별 행과 실제 요소를 1:1로 구분할 수 없습니다. 고유한 텍스트 필드를 추가하세요.");
            return result;
        }
        internal static void Stage(ScheduleEditorSnapshot data, ScheduleEditorField field, HashSet<int> numbers, string value)
        {
            if (field.Reason.Length > 0) throw new InvalidOperationException(field.Reason);
            var targets = new Dictionary<string, UnifiedRow>();
            foreach (var row in data.Rows.Where(r => numbers.Contains(r.Number)))
            {
                if (row.Reason.Length > 0) throw new InvalidOperationException(row.Number + "행: " + row.Reason);
                if (row.Errors.TryGetValue(field.Id, out var error)) throw new InvalidOperationException(row.Number + "행: " + error);
                if (!row.Targets.TryGetValue(field.Id, out var list) || list.Count == 0) throw new InvalidOperationException(row.Number + "행: 수정 대상을 확인할 수 없습니다.");
                foreach (var target in list)
                {
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
    }
}
