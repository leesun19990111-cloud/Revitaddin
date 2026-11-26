using System.Text.RegularExpressions;

namespace SunnyParameterManager.Core;

public sealed class TypePropertyReviewSettings
{
    public const string DefaultSummaryName = "미작성 매개변수 검토";
    public const string DefaultTypeNamePattern = @"\AKAC-[AS]-[A-Z]{2}[0-9]{4}(?=\z|[_\-\s])";
    public const string DefaultTypeNameMessage = "KAC-A- 또는 KAC-S- 다음에 영문 대문자 2자와 숫자 4자가 필요합니다.";

    public List<string> TypePrefixes { get; set; } = ["KAC-A-", "KAC-S-"];
    public bool IncludeUnplaced { get; set; }
    public string TypeNamePattern { get; set; } = DefaultTypeNamePattern;
    public string TypeNameMessage { get; set; } = DefaultTypeNameMessage;
    public bool ShowSummary { get; set; } = true;
    public string SummarySheetName { get; set; } = DefaultSummaryName;
    public List<TypePropertyReviewColumn> Columns { get; set; } = CreateDefaultColumns();

    private static List<TypePropertyReviewColumn> CreateDefaultColumns() =>
    [
        new() { Name = "다음으로 IFC로 내보내기", Source = "Ifc", Check = "IfcMapping", Width = 30, SummaryWidth = 30 },
        new() { Name = "작업세트", Source = "Workset", Scope = "인스턴스", Check = "ContainsByTypePrefix", Width = 26, SummaryWidth = 12,
            ValuesByTypePrefix = new() { ["KAC-A-"] = "건축", ["KAC-S-"] = "구조" } },
        new() { Name = "공항코드", Scope = "프로젝트 정보", Check = "AirportCode", Width = 14, SummaryWidth = 13 },
        new() { Name = "건축물번호", Scope = "프로젝트 정보", Check = "BuildingNumber", Width = 18, SummaryWidth = 16 },
        new() { Name = "제작정보", Check = "Required", Width = 17, SummaryWidth = 14 },
        new() { Name = "IsExternal", Width = 16, SummaryWidth = 13 },
        new() { Name = "층", Check = "Pattern", Pattern = @"\A(?:(?:지상|지하)[0-9]+층|지붕층|PIT층|옥탑(?:[0-9]+)?층)\z", IgnoreCase = true,
            Message = "지상/지하 숫자층, 지붕층, PIT층 또는 옥탑층 형식 필요", Width = 20, SummaryWidth = 9 },
        new() { Name = "KBIMS-부위코드", Check = "KbimsMapping", Width = 21, SummaryWidth = 20 },
        new() { Name = "조달청표준공사코드", Check = "ProcurementMapping", Width = 25, SummaryWidth = 26 },
        new() { Name = "재료명", Check = "Required", Width = 26, SummaryWidth = 12 }
    ];

    internal void Validate(TypePropertyReviewProfile profile)
    {
        if (!ValidNames(TypePrefixes, 100) || Columns?.Count is not (>= 1 and <= 50) ||
            TypeNamePattern == null || TypeNameMessage == null || TypeNameMessage.Length > 250 ||
            string.IsNullOrWhiteSpace(SummarySheetName) || SummarySheetName.Length > 31 ||
            SummarySheetName.StartsWith('\'') || SummarySheetName.EndsWith('\'') ||
            SummarySheetName.Any(c => char.IsControl(c) || "[]:*?/\\".Contains(c)) ||
            SummarySheetName.Equals("History", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("유형속성검토 설정의 열, 유형 접두어 또는 요약 시트 이름을 확인하세요.");

        if (TypeNamePattern.Length != 0) CreatePattern(TypeNamePattern, false, "유형명");
        var names = new HashSet<string>(
            ["모델명", "카테고리", "패밀리", "유형명", "유형 ID", "배치 수", "점검 사유", "문제 셀 합계"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var column in Columns)
        {
            if (column == null || !ValidName(column.Name) || !names.Add(column.Name) ||
                column.Scope is not ("유형·인스턴스" or "유형" or "인스턴스" or "프로젝트 정보") ||
                column.Source is not ("Parameter" or "Workset" or "Ifc") ||
                (column.Source != "Parameter" && (column.Scope == "프로젝트 정보" || column.Parameter != null)) ||
                (column.Source == "Workset" && column.Scope != "인스턴스") ||
                (column.Parameter != null && !ValidName(column.Parameter)) ||
                column.Width is < 8 or > 120 || column.SummaryWidth is < 8 or > 80 || column.Message?.Length > 250)
                throw new InvalidOperationException("유형속성검토 열의 이름, 읽기 범위, 출처 또는 너비가 올바르지 않습니다.");

            bool valid = column.Check switch
            {
                "Display" or "Required" => true,
                "Equals" or "Contains" => ValidName(column.Value),
                "AllowedValues" => column.AllowedValues?.Count > 0 && ValidNames(column.AllowedValues, 500),
                "Pattern" => !string.IsNullOrWhiteSpace(column.Pattern),
                "ContainsByTypePrefix" => column.ValuesByTypePrefix?.Count is >= 1 and <= 100 &&
                    ValidNames(column.ValuesByTypePrefix.Keys, 100) && column.ValuesByTypePrefix.Values.All(ValidName),
                "AirportCode" => column.Scope == "프로젝트 정보",
                "BuildingNumber" => column.Scope == "프로젝트 정보" && profile.Buildings.Count > 0,
                "IfcMapping" => column.Scope != "프로젝트 정보" && profile.Ifc.Count > 0,
                "KbimsMapping" or "ProcurementMapping" => column.Scope != "프로젝트 정보" && profile.Classification.Count > 0,
                _ => false
            };
            if (!valid) throw new InvalidOperationException(column.Name + " · 점검 방식 또는 비교 값을 확인하세요.");
            if (column.Check == "Pattern") CreatePattern(column.Pattern, column.IgnoreCase, column.Name);
        }
    }

    internal static Regex CreatePattern(string? pattern, bool ignoreCase, string field)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 1000)
            throw new InvalidOperationException(field + " · 정규식이 비어 있거나 1,000자를 초과했습니다.");
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException(field + " · 정규식 형식을 확인하세요.", error);
        }
    }

    internal static bool ValidNames(IEnumerable<string>? values, int limit) => values != null &&
        values.Take(limit + 1).Count() <= limit && values.All(ValidName) &&
        values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count();

    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 250 && value == value.Trim();
}

public sealed class TypePropertyReviewColumn
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "Parameter";
    public string Scope { get; set; } = "유형·인스턴스";
    public string? Parameter { get; set; }
    public string Check { get; set; } = "Display";
    public string? Value { get; set; }
    public List<string>? AllowedValues { get; set; }
    public string? Pattern { get; set; }
    public bool IgnoreCase { get; set; }
    public Dictionary<string, string>? ValuesByTypePrefix { get; set; }
    public string? Message { get; set; }
    public int Width { get; set; } = 20;
    public int SummaryWidth { get; set; } = 20;

    internal string ElementKey => Source == "Parameter" ? TypePropertyReviewQuery.NamedElementKey(Field) : Field;

    internal string Field => Source switch
    {
        "Workset" => TypePropertyReviewQuery.WorksetField,
        "Ifc" => TypePropertyReviewQuery.IfcField,
        _ => Parameter ?? Name
    };
}

public sealed class TypePropertyReviewQuery
{
    public const string WorksetField = "작업세트";
    public const string IfcField = "다음으로 IFC로 내보내기";
    public List<string> TypePrefixes { get; set; } = [];
    // 같은 이름의 일반 매개변수와 기본 IFC·작업세트를 같은 셀 값으로 합치지 않는다.
    public static string NamedElementKey(string name) =>
        name is WorksetField or IfcField || name.StartsWith('@') ? "@" + name : name;

    public List<string> ElementParameters { get; set; } = [];
    public List<string> ProjectParameters { get; set; } = [];
    public bool IncludeUnplaced { get; set; }
    public bool ReadIfc { get; set; }
    public bool ReadWorkset { get; set; }

    internal static TypePropertyReviewQuery FromSettings(TypePropertyReviewSettings settings) => new()
    {
        TypePrefixes = settings.TypePrefixes.ToList(),
        IncludeUnplaced = settings.IncludeUnplaced,
        ElementParameters = ParameterNames(settings, project: false),
        ProjectParameters = ParameterNames(settings, project: true),
        ReadIfc = settings.Columns.Any(c => c.Source == "Ifc"),
        ReadWorkset = settings.Columns.Any(c => c.Source == "Workset")
    };

    private static List<string> ParameterNames(TypePropertyReviewSettings settings, bool project) => settings.Columns
        .Where(c => c.Source == "Parameter" && (c.Scope == "프로젝트 정보") == project)
        .Select(c => c.Field).Distinct(StringComparer.Ordinal).ToList();

    internal void Validate()
    {
        if (!TypePropertyReviewSettings.ValidNames(TypePrefixes, 100) ||
            !TypePropertyReviewSettings.ValidNames(ElementParameters, 50) ||
            !TypePropertyReviewSettings.ValidNames(ProjectParameters, 50))
            throw new InvalidOperationException("유형속성검토에 필요한 조회 항목이나 범위가 올바르지 않습니다.");
    }

    public bool IncludesAttributes(string name) => TypePrefixes.Count == 0 ||
        TypePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal bool Covers(TypePropertyReviewQuery required) =>
        (!required.IncludeUnplaced || IncludeUnplaced) && (!required.ReadIfc || ReadIfc) && (!required.ReadWorkset || ReadWorkset) &&
        required.ElementParameters.All(ElementParameters.Contains) && required.ProjectParameters.All(ProjectParameters.Contains) &&
        (TypePrefixes.Count == 0 || required.TypePrefixes.Count > 0 && required.TypePrefixes.All(IncludesAttributes));
}
