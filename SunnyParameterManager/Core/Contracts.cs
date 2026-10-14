using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunnyParameterManager.Core;

public static class Protocol
{
    public const int Version = 1;
    public const string Release = "0.1.0";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public static string SessionsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunnyTools", "ParameterManager", "Sessions");
    public static string SessionFolder(string id) => Path.Combine(SessionsRoot, Guid.ParseExact(id, "N").ToString("N"));
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("작업 파일의 내용이 비어 있습니다.");
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
    public List<ParameterRow> Rows { get; set; } = [];
    public List<string> Notices { get; set; } = [];
    public Dictionary<string, List<ValueChoice>> Catalogs { get; set; } = [];
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
    [JsonIgnore] public List<ValueChoice> Choices { get; set; } = [];
    [JsonIgnore] public bool Editable => BlockReason.Length == 0 && Editor.Length > 0;
    [JsonIgnore] public string Key => JsonSerializer.Serialize(new[] { ModelPath.ToUpperInvariant(), OwnerId, ParameterKey });
    [JsonIgnore] public string TypeKey => JsonSerializer.Serialize(new[] { Scope, CategoryKey, Family, TypeName });
    [JsonIgnore] public string FieldKey => JsonSerializer.Serialize(new[] { Name, DataType, Storage, Scope });
    [JsonIgnore] public string Status => Editable ? "편집 가능" : BlockReason;
    [JsonIgnore] public string ValueLabel => Display.Length == 0 ? "(없음)" : Display;
}

public sealed record ValueChoice(string Key, string Name);
public sealed class ValueEdit
{
    public ParameterRow Original { get; set; } = new();
    public string Value { get; set; } = "";
    public string Mode { get; set; } = "입력";
    public string RawValue { get; set; } = "";
    public string ReferenceKey { get; set; } = "";
    public string Reason { get; set; } = "직접 입력";
    [JsonIgnore] public string Model => Original.ModelTitle;
    [JsonIgnore] public string Type => Original.Family + " : " + Original.TypeName;
    [JsonIgnore] public string Field => Original.Name;
    [JsonIgnore] public string Before => Original.ValueLabel;
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
    public bool Confirmed { get; set; }
    public bool LocalChangesConfirmed { get; set; }
    public List<ValueEdit> Edits { get; set; } = [];
}

public sealed class Response
{
    public string Id { get; set; } = "";
    public bool Success { get; set; }
    public bool RequiresAttention { get; set; }
    public string Stage { get; set; } = "대기";
    public string Message { get; set; } = "";
    public string RecoveryPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public int Applied { get; set; }
    public ModelSnapshot? Snapshot { get; set; }
}

public sealed class WorkerReady
{
    public int ProtocolVersion { get; set; } = Protocol.Version;
    public string Release { get; set; } = Protocol.Release;
    public int ProcessId { get; set; }
    public string RevitVersion { get; set; } = "";
}
