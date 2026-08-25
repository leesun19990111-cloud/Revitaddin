using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Clipper2Lib;

namespace WallSplitter
{
    // 타공은 원본 스케치를 직접 바꿀 수 있으므로, 호스트 자체에 복원에 필요한 최소 정보를 함께 저장한다.
    // 프로젝트를 다른 PC에서 열어도 기록이 남고, 별도 설정 파일이 유실되어 복원이 막히는 일을 피한다.
    internal static class PatternPunchRecordStore
    {
        private static readonly Guid SchemaGuid = new Guid("5D7E6B31-0566-4B6D-9D35-D8E39A6CF7D5");
        private const string FieldName = "RecordsJson";
        private const int MaximumRecordsPerHost = 20;

        // 호출자가 이미 열어 둔 트랜잭션(또는 아직 Assimilate하지 않은 TransactionGroup) 안에서만 호출한다.
        // 타공 자체를 커밋/Assimilate하는 트랜잭션이 끝난 "뒤"에 별도 트랜잭션으로 기록하면,
        // 사용자가 되돌리기를 한 번만 눌러도 기록만 사라지고 타공 형상은 그대로 남아 안전 복원이 무력화된다.
        internal static void AppendEntity(Element host, PatternPunchPlan plan,
            PatternPunchTarget target, Paths64 punchPaths, PunchExecutionResult result)
        {
            try
            {
                List<PatternPunchRecord> records = Read(host).ToList();
                records.Add(new PatternPunchRecord
                {
                    SetId = Guid.NewGuid().ToString("N"),
                    PatternName = plan.PatternName,
                    TargetLabel = target.Label,
                    CreatedUtc = DateTime.UtcNow,
                    ViewScale = plan.ViewScale,
                    BeforeProfileJson = result.BeforeProfileJson,
                    AfterProfileHash = result.AfterProfileHash,
                    CreatedElementIds = result.CreatedElementIds,
                    OriginalPanelTypeId = result.OriginalPanelTypeId,
                    PunchPathJson = SerializePaths(punchPaths),
                });
                if (records.Count > MaximumRecordsPerHost)
                    records = records.Skip(records.Count - MaximumRecordsPerHost).ToList();
                Write(host, records);
            }
            catch
            {
                // 타공 자체가 성공한 뒤 기록 저장 문제만으로 전체 명령을 실패시키지 않는다.
                // 복원 버튼은 기록이 없는 호스트를 명확히 안내한다.
            }
        }

        internal static IReadOnlyList<PatternPunchRecord> Read(Element host)
        {
            Schema? schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return Array.Empty<PatternPunchRecord>();
            Entity entity = host.GetEntity(schema);
            if (!entity.IsValid()) return Array.Empty<PatternPunchRecord>();
            string? json = entity.Get<string>(schema.GetField(FieldName));
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PatternPunchRecord>();
            try
            {
                return JsonSerializer.Deserialize<List<PatternPunchRecord>>(json) ?? new List<PatternPunchRecord>();
            }
            catch
            {
                return Array.Empty<PatternPunchRecord>();
            }
        }

        internal static void RemoveLast(Element host)
        {
            List<PatternPunchRecord> records = Read(host).ToList();
            if (records.Count == 0) return;
            records.RemoveAt(records.Count - 1);
            Write(host, records);
        }

        private static void Write(Element host, IReadOnlyList<PatternPunchRecord> records)
        {
            Schema schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set(schema.GetField(FieldName), JsonSerializer.Serialize(records));
            host.SetEntity(entity);
        }

        private static Schema GetOrCreateSchema()
        {
            Schema? schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("SunnyToolsPatternPunchRecords");
            builder.SetDocumentation("Sunny Tools 패턴 타공의 안전 복원 기록");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldName, typeof(string));
            return builder.Finish();
        }

        internal static string SerializePaths(Paths64 paths)
        {
            var value = paths.Select(path => path.Select(point => new[] { point.X, point.Y }).ToList()).ToList();
            return JsonSerializer.Serialize(value);
        }

        internal static Paths64 DeserializePaths(string json)
        {
            var result = new Paths64();
            List<List<long[]>>? value = JsonSerializer.Deserialize<List<List<long[]>>>(json);
            if (value == null) return result;
            foreach (List<long[]> rawPath in value)
            {
                var path = new Path64();
                foreach (long[] point in rawPath)
                    if (point.Length >= 2) path.Add(new Point64(point[0], point[1]));
                if (path.Count >= 3) result.Add(path);
            }
            return result;
        }

        internal static string HashPaths(Paths64 paths)
        {
            // 불리언 연산 결과의 시작점·순서가 달라도 같은 형상은 같은 해시가 되도록 각 루프를 정규화한다.
            var normalized = new List<string>();
            foreach (Path64 path in paths)
            {
                if (path.Count == 0) continue;
                var points = path.Select(point => $"{point.X},{point.Y}").ToList();
                int start = Enumerable.Range(0, points.Count).OrderBy(index => points[index], StringComparer.Ordinal).First();
                string forward = string.Join(";", Enumerable.Range(0, points.Count).Select(i => points[(start + i) % points.Count]));
                string reverse = string.Join(";", Enumerable.Range(0, points.Count).Select(i => points[(start - i + points.Count) % points.Count]));
                normalized.Add(string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse);
            }
            normalized.Sort(StringComparer.Ordinal);
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join("|", normalized));
            using SHA256 sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }

    internal sealed class PatternPunchRecord
    {
        public string SetId { get; set; } = "";
        public string PatternName { get; set; } = "";
        public string TargetLabel { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public int ViewScale { get; set; }
        public string BeforeProfileJson { get; set; } = "";
        public string AfterProfileHash { get; set; } = "";
        public List<long> CreatedElementIds { get; set; } = new List<long>();
        public long OriginalPanelTypeId { get; set; } = -1;
        public string PunchPathJson { get; set; } = "";
    }
}
