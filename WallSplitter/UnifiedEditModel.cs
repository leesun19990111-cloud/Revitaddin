using System;
using System.Collections.Generic;
using System.Linq;

namespace WallSplitter
{
    // UI는 Revit 객체를 보관하지 않는다. 모델 키 + UniqueId로 문서 간 충돌을 방지한다.
    internal sealed class UnifiedSource
    {
        internal string Key = "", Name = "", Path = "", LinkUniqueId = "", BlockReason = "";
        internal string OpenDocumentPath = "";
        internal bool IsHost;
        internal readonly List<UnifiedRow> Rows = new();
        internal readonly Dictionary<string, string> Materials = new();
        internal bool Editable => BlockReason.Length == 0;
        public override string ToString() => Name;
    }

    internal sealed class UnifiedRow
    {
        internal UnifiedSource Source = null!;
        internal string UniqueId = "", Original = "", Working = "", Family = "", ParentId = "", Detail = "";
        internal int Category;
        internal bool Selected, Expanded, IsFamily, IsMaterial, IsInstance;
        internal int SlotKind, LayerIndex;
        internal string ParameterId = "", SlotLabel = "";
        // 요소 UniqueId → 재료 UniqueId. 미지정은 빈 문자열.
        internal readonly Dictionary<string, string> OriginalMaterials = new();
        internal readonly List<string> MaterialNames = new();
        internal string NewMaterial = "";
        internal ProjectInformationValue? ProjectValue;
        internal bool Editable => Source.Editable && (ProjectValue == null || ProjectValue.ReadOnlyReason.Length == 0);
        internal string Key => Source.Key + "|" + UniqueId + (ProjectValue != null ? "|project|" + ProjectValue.Identity : IsMaterial ? "|" + SlotKind + "|" + LayerIndex + "|" + ParameterId : "");
        internal string Display(string value) => ProjectValue != null && value.Length == 0 ? "없음" : value;
        internal bool Changed => IsMaterial
            ? NewMaterial.Length > 0 && OriginalMaterials.Values.Any(v => v != NewMaterial)
            : Working != Original;
        internal string Value => IsMaterial
            ? (NewMaterial.Length > 0 && Source.Materials.TryGetValue(NewMaterial, out string? name) ? name : Original)
            : Working;
    }

    internal static class UnifiedEditLogic
    {
        internal static bool Matches(string value, string query, int mode)
        {
            if (query.Length == 0) return true;
            var comparison = StringComparison.CurrentCultureIgnoreCase;
            bool contains = value.IndexOf(query, comparison) >= 0;
            bool equal = string.Equals(value, query, comparison);
            return mode switch { 1 => !contains, 2 => equal, 3 => !equal,
                4 => value.StartsWith(query, comparison), 5 => value.EndsWith(query, comparison), _ => contains };
        }

        internal static bool MatchesMaterials(UnifiedRow row, string query, int mode)
        {
            var names = row.NewMaterial.Length > 0 ? new[] { row.Value } : row.MaterialNames.ToArray();
            if (query.Length == 0) return true;
            return mode == 1 ? names.All(n => Matches(n, query, mode))
                : mode == 3 ? names.All(n => Matches(n, query, mode))
                : names.Any(n => Matches(n, query, mode));
        }

        internal static string Rename(string name, int mode, string first, string second)
        {
            switch (mode)
            {
                case 0: return first.Length == 0 ? name : name.Replace(first, second);
                case 1:
                    return int.TryParse(first, out int position) && position > 0
                        ? name.Insert(Math.Min(position - 1, name.Length), second) : name;
                case 2:
                    int index = first.Length == 0 ? -1 : name.IndexOf(first, StringComparison.Ordinal);
                    return index < 0 ? name : name.Substring(index + first.Length) + first + name.Substring(0, index);
                case 3:
                    if (!int.TryParse(first, out int start) || !int.TryParse(second, out int end)
                        || start < 1 || end < start || start > name.Length) return name;
                    return name.Remove(start - 1, Math.Min(end, name.Length) - start + 1);
                case 4: return first;
                default: return name;
            }
        }

        // 각 모델에서 명시적으로 고른 재료만 허용한다. 동명 재료 자동 매핑/복사는 하지 않는다.
        internal static bool StageMaterials(IEnumerable<UnifiedRow> rows,
            Dictionary<string, string> choices, out string error)
        {
            var selected = rows.Where(r => r.Selected && r.Source.Editable).ToList();
            foreach (var row in selected)
                if (!choices.TryGetValue(row.Source.Key, out string? id) || !row.Source.Materials.ContainsKey(id))
                {
                    error = row.Source.Name + ": 지정할 재료를 먼저 선택하세요. 이번 적용은 모두 취소했습니다.";
                    return false;
                }
            foreach (var row in selected) row.NewMaterial = choices[row.Source.Key];
            error = "";
            return true;
        }
    }
}
