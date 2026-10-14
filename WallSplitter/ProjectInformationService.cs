using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 창에는 매개변수/문서를 넘기지 않고 값만 보관한다. 표시용 '없음'은 실제 값과 분리한다.
    internal sealed class ProjectInformationValue
    {
        internal string Identity = "", Name = "", DataType = "", Raw = "", ReadOnlyReason = "";
        internal int Storage;
        internal bool YesNo, Integer;
        // 일람표 전용 참조 편집. 문서별 UniqueId만 사용하고 이름으로 자동 매핑하지 않는다.
        internal string ReferenceKind = "", OriginalReference = "", SelectedReference = "", UnitLabel = "";
        internal Dictionary<string, string> ReferenceChoices = new();
    }

    internal static class ProjectInformationService
    {
        private static string Identity(Parameter p) => p.IsShared ? "guid:" + p.GUID.ToString("D") : "id:" + p.Id;
        private static string Raw(Parameter p) => !p.HasValue ? "unset" : ((int)p.StorageType) + ":" + (p.StorageType switch
        {
            StorageType.String => p.AsString() ?? "",
            StorageType.Integer => p.AsInteger().ToString(CultureInfo.InvariantCulture),
            StorageType.Double => p.AsDouble().ToString("R", CultureInfo.InvariantCulture),
            StorageType.ElementId => p.AsElementId().ToString(),
            _ => ""
        });
        private static string Value(Document doc, Parameter p)
        {
            if (!p.HasValue) return "";
            if (p.Id == new ElementId(BuiltInParameter.ELEM_PARTITION_PARAM) && p.StorageType == StorageType.Integer && doc.IsWorkshared)
                return doc.GetWorksetTable().GetWorkset(new WorksetId(p.AsInteger()))?.Name ?? p.AsValueString() ?? "";
            if (p.StorageType == StorageType.String) return p.AsString() ?? "";
            if (p.StorageType == StorageType.Integer && p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                return p.AsInteger() == 0 ? "아니요" : "예";
            if (p.StorageType == StorageType.ElementId)
                return p.AsValueString() ?? doc.GetElement(p.AsElementId())?.Name ?? "";
            return p.AsValueString() ?? (p.StorageType == StorageType.Integer ? p.AsInteger().ToString(CultureInfo.CurrentCulture) : "");
        }
        internal static UnifiedRow Snapshot(Document doc, Element owner, Parameter p, UnifiedSource source)
        {
            var v = new ProjectInformationValue { Identity = Identity(p), Name = p.Definition.Name, Storage = (int)p.StorageType,
                DataType = p.Definition.GetDataType().TypeId, Raw = Raw(p), YesNo = p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo,
                Integer = p.Definition.GetDataType() == SpecTypeId.Int.Integer };
            v.ReadOnlyReason = doc.IsLinked ? "원본 로컬 미연결 — 같은 Revit에 원본 로컬을 연 뒤 다시 조회하세요." : p.IsReadOnly ? "읽기 전용 — Revit에서 변경 불가" :
                p.StorageType == StorageType.ElementId || p.StorageType == StorageType.None ? "Revit 전용 선택/설정 항목 — 기본 특성에서 수정" :
                p.StorageType == StorageType.Integer && !v.YesNo && !v.Integer ? "Revit 전용 열거값 — 기본 특성에서 수정" : "";
            string text = Value(doc, p);
            return new UnifiedRow { Source = source, UniqueId = owner.UniqueId, Category = -1, ProjectValue = v,
                Family = v.Name, Original = text, Working = text, Detail = v.ReadOnlyReason.Length > 0 ? v.ReadOnlyReason : v.YesNo ? "예 / 아니요" :
                    p.StorageType == StorageType.Double ? "프로젝트 단위로 입력" : v.Integer ? "정수" : "텍스트 (빈 값 허용)" };
        }
        internal static List<UnifiedRow> Read(Document doc, UnifiedSource source)
        {
            var rows = new List<UnifiedRow>();
            ProjectInfo? info = doc.ProjectInformation;
            if (info == null) return rows;
            foreach (Parameter parameter in info.Parameters)
            {
                if (parameter.Definition == null) continue;
                var value = new ProjectInformationValue { Identity = Identity(parameter), Name = parameter.Definition.Name,
                    Storage = (int)parameter.StorageType, DataType = parameter.Definition.GetDataType().TypeId, Raw = Raw(parameter),
                    YesNo = parameter.Definition.GetDataType() == SpecTypeId.Boolean.YesNo,
                    Integer = parameter.Definition.GetDataType() == SpecTypeId.Int.Integer };
                value.ReadOnlyReason = doc.IsLinked ? "원본 로컬 미연결 — 같은 Revit에 원본 로컬을 연 뒤 다시 조회하세요." : parameter.IsReadOnly ? "읽기 전용 — Revit에서 변경 불가" :
                    parameter.StorageType == StorageType.ElementId || parameter.StorageType == StorageType.None ? "Revit 전용 선택/설정 항목 — 기본 프로젝트 정보에서 수정" :
                    parameter.StorageType == StorageType.Integer && !value.YesNo && !value.Integer ? "Revit 전용 열거값 — 기본 프로젝트 정보에서 수정" : "";
                string text = Value(doc, parameter);
                rows.Add(new UnifiedRow { Source = source, UniqueId = info.UniqueId, Category = (int)NamerWindow.NamerCategory.ProjectInformation,
                    ProjectValue = value, Family = value.Name, Original = text, Working = text,
                    Detail = value.ReadOnlyReason.Length > 0 ? value.ReadOnlyReason : value.YesNo ? "예 / 아니요" :
                        parameter.StorageType == StorageType.Double ? "프로젝트 단위로 입력" : value.Integer ? "정수" : "텍스트 (빈 값 허용)" });
            }
            return rows.OrderBy(r => r.Family).ThenBy(r => r.Key).ToList();
        }
        internal static Parameter Validate(Document doc, UnifiedRow row, bool forWrite = true)
        {
            var expected = row.ProjectValue ?? throw new InvalidOperationException("프로젝트 정보 식별자 없음");
            var info = row.Category == (int)NamerWindow.NamerCategory.ProjectInformation ? doc.ProjectInformation : doc.GetElement(row.UniqueId);
            if (info == null || info.UniqueId != row.UniqueId) throw new InvalidOperationException("매개변수 대상이 변경되었습니다.");
            var matches = info.Parameters.Cast<Parameter>().Where(p => Identity(p) == expected.Identity).ToList();
            // 일부 기본 매개변수는 Parameters 열거에서 빠져도 전용 접근자로 조회된다.
            if (expected.Identity.StartsWith("id:", StringComparison.Ordinal) && int.TryParse(expected.Identity.Substring(3), out int builtin) && builtin < 0)
            {
                var direct = info.get_Parameter((BuiltInParameter)builtin);
                if (direct != null) matches = new List<Parameter> { direct };
            }
            if (matches.Count != 1) throw new InvalidOperationException(expected.Name + ": 매개변수가 삭제되거나 중복되었습니다.");
            Parameter p = matches[0];
            if ((forWrite && (p.IsReadOnly || !row.Editable)) || p.Definition.Name != expected.Name || (int)p.StorageType != expected.Storage ||
                p.Definition.GetDataType().TypeId != expected.DataType || Raw(p) != expected.Raw)
                throw new InvalidOperationException(expected.Name + ": 조회 이후 값/정의/편집 권한이 변경되었습니다. 다시 조회하세요.");
            // 같은 내부 값이라도 단위 표시가 바뀌면 입력값을 잘못 해석할 수 있다.
            if (Value(doc, p) != row.Original) throw new InvalidOperationException(expected.Name + ": 표시 단위가 변경되었습니다. 다시 조회하세요.");
            if (expected.ReferenceKind == "작업세트" && (p.StorageType != StorageType.Integer ||
                p.Id != new ElementId(BuiltInParameter.ELEM_PARTITION_PARAM) || !doc.IsWorkshared ||
                (doc.GetWorksetTable().GetWorkset(new WorksetId(p.AsInteger()))?.UniqueId.ToString("D") ?? "") != expected.OriginalReference))
                throw new InvalidOperationException(expected.Name + ": 원래 작업세트가 변경되었습니다. 다시 조회하세요.");
            return p;
        }
        internal static string? InputError(UnifiedRow row)
        {
            var value = row.ProjectValue;
            if (value == null || !row.Changed) return null;
            if (!row.Editable) return value.Name + ": " + value.ReadOnlyReason;
            if (value.ReferenceKind.Length > 0) return null; // 작업세트는 정수 직접 입력이 아니라 모델별 선택값이다.
            if (value.YesNo && !TryBoolean(row.Working, out _)) return value.Name + ": 예/아니요 또는 1/0으로 입력하세요.";
            if (value.Integer && !int.TryParse(row.Working, NumberStyles.Integer, CultureInfo.CurrentCulture, out _)) return value.Name + ": 정수를 입력하세요.";
            if (value.Storage == (int)StorageType.Double && string.IsNullOrWhiteSpace(row.Working)) return value.Name + ": 단위가 있는 숫자를 입력하세요. 숫자 항목은 빈 값으로 지울 수 없습니다.";
            return null;
        }
        private static bool TryBoolean(string text, out int value)
        {
            string normalized = text.Trim().ToLowerInvariant();
            value = normalized == "예" || normalized == "yes" || normalized == "true" || normalized == "1" ? 1 : 0;
            return value == 1 || normalized == "아니요" || normalized == "no" || normalized == "false" || normalized == "0";
        }
        internal static void Apply(Document doc, List<UnifiedRow> rows, List<ChangeLogEntry> logs, bool schedule = false)
        {
            if (rows.Count == 0) return;
            var parameters = rows.Select(r => Validate(doc, r)).ToList();
            foreach (var row in rows) { string? error = InputError(row); if (error != null) throw new InvalidOperationException(error); }
            var pending = new List<ChangeLogEntry>();
            using var tx = new Transaction(doc, schedule ? "일람표 에디터 매개변수 변경" : "NAMER 프로젝트 정보 변경");
            if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("프로젝트 정보 변경 시작 실패");
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; var p = parameters[i]; var value = row.ProjectValue!;
                bool success;
                if (p.StorageType == StorageType.String) success = p.Set(row.Working);
                else if (schedule && value.ReferenceKind == "작업세트")
                {
                    if (!doc.IsWorkshared || doc.IsLinked || p.StorageType != StorageType.Integer ||
                        p.Id != new ElementId(BuiltInParameter.ELEM_PARTITION_PARAM))
                        throw new InvalidOperationException(value.Name + ": 작업세트 변경 대상이 아닙니다.");
                    using var collector = new FilteredWorksetCollector(doc);
                    var workset = collector.OfKind(WorksetKind.UserWorkset).FirstOrDefault(w => w.UniqueId.ToString("D") == value.SelectedReference);
                    if (workset == null || !value.ReferenceChoices.TryGetValue(value.SelectedReference, out string? expectedName) || workset.Name != expectedName)
                        throw new InvalidOperationException(value.Name + ": 선택한 작업세트가 삭제되거나 이름이 변경되었습니다. 다시 조회하세요.");
                    success = p.Set(workset.Id.IntegerValue);
                }
                else if (schedule && value.ReferenceKind.Length > 0)
                {
                    var target = doc.GetElement(value.SelectedReference);
                    if ((value.ReferenceKind != "재료" && value.ReferenceKind != "레벨") || target == null || !value.ReferenceChoices.TryGetValue(target.UniqueId, out string? expectedName) ||
                        target.Name != expectedName || (value.ReferenceKind == "재료" ? target is not Material : target is not Level))
                        throw new InvalidOperationException(value.Name + ": 선택한 참조가 삭제되거나 변경되었습니다. 다시 조회하세요.");
                    success = p.Set(target.Id);
                }
                else if (value.YesNo) { TryBoolean(row.Working, out int b); success = p.Set(b); }
                else if (value.Integer) success = p.Set(int.Parse(row.Working, CultureInfo.CurrentCulture));
                else success = p.SetValueString(row.Working);
                if (!success) throw new InvalidOperationException(value.Name + ": 값을 적용하지 못했습니다. 입력 형식과 편집 권한을 확인하세요.");
                pending.Add(new ChangeLogEntry { Timestamp = DateTime.Now, SourceDocumentTitle = doc.Title,
                    Kind = schedule ? ChangeKind.ScheduleParameter : ChangeKind.ProjectInformation, Category = NamerWindow.NamerCategory.ProjectInformation,
                    Key = value.Name, OldValue = row.Original, NewValue = Value(doc, p) });
            }
            if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("프로젝트 정보 변경이 취소되었습니다.");
            logs.AddRange(pending);
        }
    }
}
