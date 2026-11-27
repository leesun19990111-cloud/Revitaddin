using System.Text;
using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

public sealed class TypePropertyReviewProfile
{
    public List<KacClassification> Classification { get; set; } = [];
    public List<KacIfcMapping> Ifc { get; set; } = [];
    public List<SpaceBuilding> Buildings { get; set; } = [];
    public List<string> Sources { get; set; } = [];
    public TypePropertyReviewSettings? Settings { get; set; }
}

public sealed class TypePropertyReviewSnapshot
{
    public const int CurrentFormatVersion = 2;
    public int FormatVersion { get; set; }
    public Dictionary<string, TypePropertyValue> ProjectValues { get; set; } = [];
    public List<TypePropertyEntry> Types { get; set; } = [];
}

public sealed class TypePropertyEntry
{
    public string Name { get; set; } = "";
    public string Family { get; set; } = "";
    public string Category { get; set; } = "";
    public string ElementId { get; set; } = "";
    public int InstanceCount { get; set; }
    public Dictionary<string, TypePropertyValue> TypeValues { get; set; } = [];
    public Dictionary<string, HashSet<TypePropertyValue>> InstanceValues { get; set; } = [];
}

public sealed record TypePropertyValue(string? Text = null, bool Ambiguous = false);
public sealed record TypePropertyCell(string Value, string Issue = "");
public sealed record TypePropertyRow(TypePropertyEntry Type, string NameIssue, List<TypePropertyCell> Cells);

public sealed partial class TypePropertyReview
{
    private readonly TypePropertyReviewQuery _query;
    private readonly Regex? _typeNamePattern;
    private readonly List<(TypePropertyReviewColumn Config, Regex? Pattern)> _columns;
    private readonly Dictionary<string, KacClassification> _classification;
    private readonly List<KacIfcMapping> _ifc;
    private readonly ILookup<string, SpaceBuilding> _buildings;

    public TypePropertyReviewSettings Settings { get; }

    public TypePropertyReview(TypePropertyReviewProfile profile)
    {
        Settings = profile.Settings ?? new TypePropertyReviewSettings();
        _query = TypePropertyReviewQuery.FromSettings(Settings);
        _typeNamePattern = Settings.TypeNamePattern.Length == 0 ? null :
            TypePropertyReviewSettings.CreatePattern(Settings.TypeNamePattern, false, "유형명");
        _columns = Settings.Columns.Select(c => (c, c.Check == "Pattern"
            ? TypePropertyReviewSettings.CreatePattern(c.Pattern, c.IgnoreCase, c.Name)
            : null)).ToList();
        _classification = profile.Classification.ToDictionary(x => x.Code, StringComparer.Ordinal);
        _ifc = profile.Ifc.OrderByDescending(x => x.Prefix.Length).ToList();
        _buildings = profile.Buildings.ToLookup(x => x.Airport, StringComparer.Ordinal);
    }

    public static void Validate(RuleSet file)
    {
        var p = file.TypePropertyReview;
        if (file.Version != 14 || p == null || string.IsNullOrWhiteSpace(file.Name) || file.SampleOnly ||
            file.Rules?.Count != 0 || file.Kac != null || file.Space != null || file.Spec != null || file.RoomUse != null ||
            file.Cleanup || file.MaterialAudit || file.TypeCodeFix || file.ProjectBasePointAudit || file.RebuildCentral ||
            file.ProjectParameterTransfer || file.RoomReview || file.GuardhouseRooms || file.PreserveWorksets?.Count != 0 ||
            p.Classification?.Count is not (>= 0 and <= 10000) || p.Ifc?.Count is not (>= 0 and <= 200) ||
            p.Buildings?.Count is not (>= 0 and <= 5000) || p.Sources?.Count is not (>= 0 and <= 10) ||
            (p.Settings == null && p.Sources.Count == 0) ||
            p.Sources.Any(string.IsNullOrWhiteSpace) ||
            p.Classification.Any(c => c == null || c.Code == null || !CodeFormat().IsMatch(c.Code) || c.Kbims == null || c.Procurement == null) ||
            p.Classification.DistinctBy(c => c.Code).Count() != p.Classification.Count ||
            p.Ifc.Any(i => i == null || string.IsNullOrWhiteSpace(i.Prefix) || i.Value == null || !IfcFormat().IsMatch(i.Value)) ||
            p.Ifc.DistinctBy(i => i.Prefix).Count() != p.Ifc.Count ||
            p.Buildings.Any(b => b == null || b.Airport is not ("CJU" or "MWX" or "PUS" or "GMP") ||
                string.IsNullOrWhiteSpace(b.Name) || b.Number == null || b.ManagementCode == null || b.Ownership == null ||
                b.SourceRow < 1 || b.NameAliases == null || b.NameAliases.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidOperationException("유형속성검토 룰셋의 버전과 IFC·유형 분류·건물 기준표를 확인하세요.");
        (p.Settings ?? new TypePropertyReviewSettings()).Validate(p);
    }

    public IEnumerable<TypePropertyRow> Inspect(ModelSnapshot model)
    {
        if (model.TypePropertyReview is not { FormatVersion: TypePropertyReviewSnapshot.CurrentFormatVersion } snapshot)
            throw new InvalidOperationException(model.Title + " · 유형속성검토 룰셋으로 다시 조회하세요.");

        var path = string.IsNullOrWhiteSpace(model.CentralPath) ? model.Path : model.CentralPath;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var prefix = fileName.Length >= 3 ? fileName[..3].ToUpperInvariant() : "";
        var airport = prefix is "CJU" or "MWX" or "PUS" or "GMP" ? prefix : "";
        var (building, buildingIssue) = _columns.Any(c => c.Config.Check == "BuildingNumber") ? FindBuilding(airport, fileName) : (null, "");

        var types = snapshot.Types.Where(t => Settings.IncludeUnplaced || t.InstanceCount > 0)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Family, StringComparer.Ordinal)
            .ThenBy(t => t.ElementId, StringComparer.Ordinal);
        foreach (var type in types)
        {
            var nameIssue = _typeNamePattern?.IsMatch(type.Name) == false
                ? Issue(Settings.TypeNameMessage, "유형명 형식이 기준에 맞지 않습니다.") : "";
            var matchesScope = _query.IncludesAttributes(type.Name);
            var code = matchesScope ? KacRulePlanner.TypeCode(type.Name.ToUpperInvariant()) : "";
            var ifc = _ifc.FirstOrDefault(i => code.StartsWith(i.Prefix, StringComparison.Ordinal))?.Value;
            var classification = _classification.GetValueOrDefault(code);
            yield return new(type, nameIssue, _columns.Select(c => InspectColumn(c.Config, c.Pattern)).ToList());

            TypePropertyCell InspectColumn(TypePropertyReviewColumn column, Regex? pattern)
            {
                if (column.Scope == "인스턴스" && type.InstanceCount == 0)
                    return new(column.Source == "Workset" ? "미배치" : "");
                if (!matchesScope)
                    return column.Source == "Workset" ? Check(Values(type, column)) : new("");

                IEnumerable<TypePropertyValue> values = column.Scope == "프로젝트 정보"
                    ? [snapshot.ProjectValues.GetValueOrDefault(column.Field) ?? new()]
                    : Values(type, column);
                return column.Check == "Display" ? Check(values) : Check(values, Verify);

                string Verify(string? value)
                {
                    var comparison = column.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    var result = column.Check switch
                    {
                        "Required" => Blank(value) ? "값 누락" : "",
                        "Equals" => string.Equals(value, column.Value, comparison) ? "" : $"기준값 {column.Value} 필요",
                        "Contains" => value?.Contains(column.Value!, comparison) == true ? "" : $"‘{column.Value}’ 포함 필요",
                        "AllowedValues" => !Blank(value) && column.AllowedValues!.Any(v => string.Equals(v, value, comparison))
                            ? "" : "허용 목록에 있는 값이 필요합니다.",
                        "Pattern" => !Blank(value) && pattern!.IsMatch(value!) ? "" : "입력 형식이 기준에 맞지 않습니다.",
                        "ContainsByTypePrefix" => CheckPrefixValue(type.Name, value, column, comparison),
                        "AirportCode" => Expected(value, airport, "파일명 공항코드를 확인할 수 없음"),
                        "BuildingNumber" => Expected(value, building, buildingIssue),
                        "IfcMapping" => Expected(value, ifc, "유형 코드의 IFC 기준값 없음"),
                        "KbimsMapping" => Expected(value, classification?.Kbims, "유형 코드의 KBIMS 기준값 없음"),
                        "ProcurementMapping" => Expected(value, classification?.Procurement, "유형 코드의 조달청 기준값 없음"),
                        _ => throw new InvalidOperationException(column.Name + " · 지원하지 않는 점검 방식입니다.")
                    };
                    return result.Length == 0 ? "" : Issue(column.Message, result);
                }
            }
        }
    }

    private static string Issue(string? message, string fallback) => string.IsNullOrWhiteSpace(message) ? fallback : message;

    private static string CheckPrefixValue(string typeName, string? value, TypePropertyReviewColumn column, StringComparison comparison)
    {
        var expected = column.ValuesByTypePrefix!
            .Where(pair => typeName.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Key.Length).Select(pair => pair.Value).FirstOrDefault();
        if (expected == null) return "해당 유형 접두어의 기준값을 확인할 수 없습니다.";
        if (value?.Contains(expected, comparison) == true) return "";
        return column.Source == "Workset" ? $"‘{expected}’이 포함된 작업세트 필요" : $"‘{expected}’ 포함 필요";
    }

    private static IEnumerable<TypePropertyValue> Values(TypePropertyEntry type, TypePropertyReviewColumn column)
    {
        var typeValue = type.TypeValues.GetValueOrDefault(column.ElementKey);
        var instances = type.InstanceValues.GetValueOrDefault(column.ElementKey);
        if (column.Scope == "유형")
        {
            yield return typeValue ?? new();
            yield break;
        }
        if (column.Scope == "인스턴스")
        {
            foreach (var value in instances ?? [new()]) yield return value;
            yield break;
        }
        if (typeValue is { Text: not null } || typeValue?.Ambiguous == true) yield return typeValue;
        if (instances != null && instances.Any(v => v.Text != null || v.Ambiguous))
            foreach (var value in instances) yield return value;
        else if (typeValue?.Text == null && typeValue?.Ambiguous != true) yield return new();
    }

    private static TypePropertyCell Check(IEnumerable<TypePropertyValue> values, Func<string?, string>? verify = null)
    {
        var items = values.Distinct().OrderBy(v => v.Text, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Ambiguous).ToList();
        var display = items.Select(v => v.Ambiguous ? "동명 매개변수 여러 개" : Blank(v.Text) ? "(공란)" : v.Text!).Distinct().ToList();
        var shown = display.Count == 1 && display[0] == "(공란)" ? "" : string.Join(" / ", display.Take(8));
        if (display.Count > 8) shown += $" 외 {display.Count - 8:N0}개";
        var issue = verify == null ? "" : string.Join(", ", items
            .Select(v => v.Ambiguous ? "동명 매개변수가 여러 개 있습니다" : verify(v.Text))
            .Where(s => s.Length > 0).Distinct());
        if (issue.Length > 0 && items.Count > 0 && items.All(v => v.Text == null && !v.Ambiguous))
            issue += " · 매개변수 미조회/해당 범위에 없음";
        return new(shown, issue);
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string Expected(string? actual, string? expected, string unknown)
    {
        if (Blank(expected)) return Blank(actual) ? "값 누락 및 " + unknown : unknown;
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? "" :
            (Blank(actual) ? "공란" : "값 불일치") + $" (기준값 {expected})";
    }

    private (string? Value, string Issue) FindBuilding(string airport, string fileName)
    {
        if (airport.Length == 0) return (null, "파일명 공항코드를 확인할 수 없음");
        var coded = BuildingFileMatch.Parse(fileName + ".rvt");
        if (coded != null)
        {
            var decision = coded.Resolve(_buildings[airport], "ND");
            return (decision.Value, decision.Reason);
        }
        var stem = FileDiscipline().Replace(fileName[3..].TrimStart('-', '_', ' '), "");
        var match = FileBuildingPrefix().Match(stem);
        var name = match.Success ? stem[match.Length..] : stem;
        var nameKey = NameKey(name);
        if (nameKey.Length == 0) return (null, "파일명 건물명을 확인할 수 없음");

        var entries = _buildings[airport];
        var direct = entries.Where(b => NameKey(b.Name) == nameKey || b.NameAliases.Any(a => NameKey(a) == nameKey)).ToList();
        var matches = direct.Count > 0 ? direct : entries.Where(b => NameVariants(b.Name).Intersect(NameVariants(name), StringComparer.Ordinal).Any()).ToList();
        if (matches.Count > 1 && match.Success)
        {
            var number = match.Groups[1].Value.Replace('_', '-');
            var narrowed = matches.Where(b => NumberKey(b.Number) == NumberKey(number) ||
                b.ManagementCode.StartsWith(airport + number.Split('-')[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (narrowed.Count > 0) matches = narrowed;
        }
        if (matches.Count == 0) return (null, "엑셀에 같은 공항·건물명 없음");
        var expected = matches.Select(SpaceRulePlanner.BuildingValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (expected.Count != 1) return (null, "동일 건물명의 기준값이 여러 개입니다");
        return (expected[0], $"원본 {string.Join(", ", matches.Select(b => b.SourceRow))}행 관리코드·소유구분 확인 필요");
    }

    private static string NumberKey(string value) => string.Join("-", value.Split('-').Select(s => s.TrimStart('0')));
    private static string NameKey(string value) => new string(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static IEnumerable<string> NameVariants(string value)
    {
        yield return NameKey(value);
        foreach (var name in BuildingNameParts().Split(value))
            if (NameKey(name) is { Length: >= 2 } key) yield return key;
    }

    [GeneratedRegex(@"\A[A-Z]{2}[0-9]{4}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CodeFormat();
    [GeneratedRegex(@"\AIfc[A-Za-z0-9]+\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex IfcFormat();
    [GeneratedRegex(@"[-_\s]+(?:SA|SP|AR|ST|S|A)(?:\s*\([^()]+\))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex FileDiscipline();
    [GeneratedRegex(@"\A([0-9][A-Z][0-9]{3}(?:[-_][0-9]+)?|[0-9]+(?:[-_][0-9]+)?)[-_\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex FileBuildingPrefix();
    [GeneratedRegex(@"[()<>〈〉\[\]（）]", RegexOptions.CultureInvariant, 100)]
    private static partial Regex BuildingNameParts();
}
