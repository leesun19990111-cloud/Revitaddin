using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunnyParameterManager.Core;

public static class Protocol
{
    public const int Version = 1;
    public const string Release = "0.3.7";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public static string SessionsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunnyTools", "ParameterManager", "Sessions");
    public static string SessionFolder(string id) => Path.Combine(SessionsRoot, Guid.ParseExact(id, "N").ToString("N"));
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 전체 JSON 문자열을 한꺼번에 만들지 않고 파일로 순차 기록한다.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, value, Json);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("작업 파일의 내용이 비어 있습니다.");
    }
}

public sealed class ModelSnapshot
{
    public string Path { get; set; } = "";
    public string CanonicalPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string CentralPath { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Version { get; set; } = "2026";
    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
    // 적용 실패한 조회값은 원인 확인·재조회 전 다시 적용하지 않는다. 작업 파일에도 보존한다.
    public bool RequiresRequery { get; set; }
    public QueryPlan? Query { get; set; }
    // 선택 필드가 없는 대상도 누락 진단/실제 유형 연결에 남긴다. 편집 목록에는 섞지 않는다.
    public List<ParameterRow> Owners { get; set; } = [];
    public List<ParameterRow> Rows { get; set; } = [];
    public List<string> Notices { get; set; } = [];
    public Dictionary<string, List<ValueChoice>> Catalogs { get; set; } = [];
    // 속성규격 룰셋(버전5)으로 조회할 때만 채운다. 매개변수 정의(바인딩) 목록.
    public DefinitionSnapshot? Definitions { get; set; }
    public void ConnectCatalogs() { foreach (var row in Rows) row.Choices = Catalogs.GetValueOrDefault(row.ChoiceCatalog, []); }
}

public sealed class ParameterRow
{
    public string ModelPath { get; set; } = "";
    public string ModelTitle { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string Scope { get; set; } = "유형";
    public string Category { get; set; } = "";
    public string CategoryKey { get; set; } = "";
    public string Family { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string TypeOwnerId { get; set; } = "";
    public string ElementName { get; set; } = "";
    public string SheetNumber { get; set; } = "";
    [JsonIgnore] public string TargetName => Scope == "시트" ? (SheetNumber + " · " + ElementName).Trim(' ', '·') : Scope == "뷰" ? ElementName : TypeName;
    public string ParameterKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Storage { get; set; } = "";
    public string Editor { get; set; } = "";
    public string Unit { get; set; } = "";
    public bool HasValue { get; set; }
    public string Raw { get; set; } = "";
    public string Display { get; set; } = "";
    public string BlockReason { get; set; } = "";
    public string ChoiceCatalog { get; set; } = "";
    public MaterialEvidence? OuterMaterial { get; set; }
    public CleanupEvidence? Cleanup { get; set; }
    public bool IdentityDataParameter { get; set; }
    public GroupEditEvidence? GroupEdit { get; set; }
    // 프로젝트 정보 매개변수의 표시 그룹. 다른 모델에 같은 공유 매개변수를 추가할 때 기준으로만 쓴다.
    public string ParameterGroup { get; set; } = "";
    [JsonIgnore] public List<ValueChoice> Choices { get; set; } = [];
    [JsonIgnore] public bool Editable => BlockReason.Length == 0 && Editor.Length > 0;
    [JsonIgnore] public string Key => JsonSerializer.Serialize(new[] { ModelPath.ToUpperInvariant(), OwnerId, ParameterKey });
    [JsonIgnore] public string TypeKey => JsonSerializer.Serialize(new[] { Scope, CategoryKey, Family, TypeName });
    [JsonIgnore] public string FieldKey => JsonSerializer.Serialize(new[] { Name, DataType, Storage, Scope });
    [JsonIgnore] public string Status => Editable ? "편집 가능" : BlockReason;
    [JsonIgnore] public string ValueLabel => Display.Length == 0 ? "(없음)" : Display;
}

public sealed record ValueChoice(string Key, string Name);
// 그룹 설정을 바꾸지 않고 개별 편집 가능 여부와 소속을 재검증하는 근거.
public sealed record GroupEditEvidence
{
    public string Identity { get; set; } = "";
    public string Name { get; set; } = "";
    public string ElementId { get; set; } = "";
    public bool CanVary { get; set; }
    public string Reason { get; set; } = "";
}
// 조회 때 확인한 재료와 근거. 실제 저장 때 같은 근거인지 다시 검사한다.
public sealed class MaterialEvidence
{
    // 정상 조회 결과 재료를 확정할 수 없는 경우만 허용. 예외·이전 조회 누락은 제외한다.
    public bool FallbackAllowed { get; set; }
    public string Name { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Basis { get; set; } = "";
}
public sealed class ValueEdit
{
    public ParameterRow Original { get; set; } = new();
    public string Value { get; set; } = "";
    public string Mode { get; set; } = "입력";
    public string RawValue { get; set; } = "";
    public string ReferenceKey { get; set; } = "";
    public string Reason { get; set; } = "직접 입력";
    public MaterialEvidence? MaterialSource { get; set; }
    public bool KacCleanup { get; set; }
    // 대상 모델에 없는 공유 매개변수를 프로젝트 정보에 새로 연결한 뒤 값을 넣는다. 룰셋이 명시한 경우만 생성된다.
    public SharedParameterAddition? AddParameter { get; set; }
    // 속성규격 룰셋의 매개변수 정의 정리(그룹·카테고리·유형/인스턴스·추가·중복 병합). 값 변경과 섞지 않는다.
    public DefinitionFix? DefinitionFix { get; set; }
    [JsonIgnore] public string Model => Original.ModelTitle;
    [JsonIgnore] public string Type => Original.Scope is "뷰" or "시트" ? Original.Scope + " : " + Original.TargetName : Original.Family + " : " + Original.TypeName;
    [JsonIgnore] public string Field => Original.Name;
    [JsonIgnore] public string Before => Original.ValueLabel;
}

// 다른 모델에서 조회한 같은 공유 매개변수(GUID·이름·자료형·그룹)를 프로젝트 정보에 인스턴스 매개변수로 추가한다.
public sealed class SharedParameterAddition
{
    public const string OtherGroup = "(기타)";
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Group { get; set; } = "";
    public string Source { get; set; } = "";
}

// 프로젝트에 연결된 매개변수 정의 하나. Key는 공유 GUID(guid:) 또는 프로젝트 매개변수 요소(id:).
public sealed class ParameterDefinitionInfo
{
    public string Revision { get; set; } = "";
    public string ValueFingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string Guid { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string ElementUniqueId { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Group { get; set; } = "";
    public string Binding { get; set; } = "인스턴스";
    public List<string> Categories { get; set; } = [];
    // 값이 있는 요소 수. -1은 확인 불가이며 0으로 간주하지 않는다.
    public int ValueCount { get; set; } = -1;
}

public sealed class DefinitionSnapshot
{
    public List<ParameterDefinitionInfo> Definitions { get; set; } = [];
    // 매개변수를 연결할 수 있는 모든 카테고리(‘all’ 판정 기준).
    public List<string> AllCategories { get; set; } = [];
    public Dictionary<string, string> CategoryNames { get; set; } = [];
}

// 승인된 정의 정리 한 건. Bridge는 적용 직전에 현재 정의를 다시 확인하고, 확정 후 결과를 재검증한다.
public sealed class DefinitionFix
{
    public DefinitionRemovalEvidence? Removal { get; set; }
    public string Action { get; set; } = "";
    public string Name { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string ElementUniqueId { get; set; } = "";
    public string Guid { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Group { get; set; } = "";
    public string Binding { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public string SourceName { get; set; } = "";
    public string TargetKey { get; set; } = "";
    // 조회 당시 값이 있던 요소 수. 적용 직전과 다르면 다시 조회하게 한다.
    public int ValueCount { get; set; }
}

// 삭제를 승인한 당시의 정의와 값 지문. 다른 정의 정리 작업에는 사용하지 않는다.
public sealed class DefinitionRemovalEvidence
{
    public int Version { get; set; } = 1;
    public ParameterDefinitionInfo Source { get; set; } = new();
    public ParameterDefinitionInfo? Keeper { get; set; }
    public List<string> AllowedCategories { get; set; } = [];
}

public sealed class Request
{
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Token { get; set; } = "";
    public string Operation { get; set; } = "조회";
    public string Path { get; set; } = "";
    public string Identity { get; set; } = "";
    public string CanonicalPath { get; set; } = "";
    public QueryPlan? Query { get; set; }
    public bool Confirmed { get; set; }
    public bool LocalChangesConfirmed { get; set; }
    public List<ValueEdit> Edits { get; set; } = [];
    public LinkRemovalPlan? LinkRemoval { get; set; }
    public bool DependentsConfirmed { get; set; }
}

public sealed class Response
{
    public string Id { get; set; } = "";
    public bool Success { get; set; }
    public bool RequiresAttention { get; set; }
    // 적용 전 실패이고 문서가 모두 닫힌 경우에만 Worker가 최종 허용한다.
    public bool CanContinueAfterPreflightFailure { get; set; }
    // 조회 요청이고 문서가 모두 닫혔음을 작업 프로세스에서 확인한 경우만 허용한다.
    public bool CanRestartForRead { get; set; }
    public bool ReadRestartRequired { get; set; }
    public string Stage { get; set; } = "대기";
    public string Message { get; set; } = "";
    public string RecoveryPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public int Applied { get; set; }
    public bool CentralSynchronized { get; set; }
    public bool OwnershipReleased { get; set; }
    public ModelSnapshot? Snapshot { get; set; }
    public LinkRemovalPlan? LinkRemoval { get; set; }
}

public sealed class WorkerReady
{
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public string Release { get; set; } = Protocol.Release;
    public int ProcessId { get; set; }
    public string RevitVersion { get; set; } = "";
}
