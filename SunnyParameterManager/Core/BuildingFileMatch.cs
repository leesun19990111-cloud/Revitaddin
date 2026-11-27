using System.Text;
using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

internal sealed record BuildingDecision(string? Value, string Reason, SpaceBuilding? Entry = null);
internal sealed partial record BuildingFileMatch(string Airport, string Code, string Name)
{
    [GeneratedRegex(@"\A(CJU|PUS|MWX|GMP)[-_]([0-9][A-Z][0-9]{3})[-_]([0-9]+)[-_](.+?)[-_](?:SA|SP)(?:\s*\([^()]+\))?\.rvt\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CodeForm();

    internal static BuildingFileMatch? Parse(string path)
    {
        var m = CodeForm().Match(Path.GetFileName(path.Replace('\\', '/')));
        return m.Success ? new(m.Groups[1].Value.ToUpperInvariant(),
            m.Groups[2].Value.ToUpperInvariant() + "-" + m.Groups[3].Value, m.Groups[4].Value) : null;
    }

    internal static string NameKey(string text) =>
        new string(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    internal BuildingDecision Resolve(IEnumerable<SpaceBuilding> table, string missingValue)
    {
        var airport = table.Where(b => b.Airport == Airport).ToList();
        var key = NameKey(Name);
        var named = airport.Where(b => NameKey(b.Name) == key || b.NameAliases.Any(n => NameKey(n) == key)).ToList();
        var matches = (named.Count > 0 ? named : airport).Where(b => SpaceRulePlanner.BuildingValue(b) == Code).ToList();
        if (matches.Count > 0)
        {
            var rows = string.Join(", ", matches.Select(b => b.SourceRow));
            // 동일 번호를 공유하는 여러 건물은 번호만 확정할 수 있다. 자산번호에는 단일 건물 근거가 필요하다.
            return new(Code, $"중앙파일명 {Airport}-{Code} · 원본 {rows}행", named.Count > 0 && matches.Count == 1 ? matches[0] : null);
        }
        if (named.Count > 0 && named.All(b => (b.ManagementCode.Length == 0 || b.Ownership.Length == 0) &&
            (b.ManagementCode.Length == 0 || b.ManagementCode == Airport + Code.Split('-')[0])))
            return new(missingValue.Length == 0 ? null : missingValue,
                $"원본 {string.Join(", ", named.Select(b => b.SourceRow))}행 관리코드·소유구분 누락", named.Count == 1 ? named[0] : null);
        return new(null, named.Count > 0 ? $"파일명 {Code}와 같은 건물의 원본 관리코드·소유구분 불일치" :
            $"엑셀에 {Airport}-{Code}와 일치하는 관리코드·소유구분 없음");
    }
}
