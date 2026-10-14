using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    internal static class ScheduleEditorParameters
    {
        internal static string Display(Document host, Document owner, ScheduleField field, Parameter p)
        {
            if (!p.HasValue) return "";
            if (p.Id == new ElementId(BuiltInParameter.ELEM_PARTITION_PARAM) && p.StorageType == StorageType.Integer && owner.IsWorkshared)
                return owner.GetWorksetTable().GetWorkset(new WorksetId(p.AsInteger()))?.Name ?? p.AsValueString() ?? "";
            if (p.StorageType == StorageType.String) return p.AsString() ?? "";
            if (p.StorageType == StorageType.Double)
            {
                var spec = p.Definition.GetDataType();
                if (!UnitUtils.IsMeasurableSpec(spec)) throw new InvalidOperationException("숫자 단위 식별 불가");
                using var options = new FormatValueOptions();
                using var format = field.GetFormatOptions();
                if (!format.UseDefault) options.SetFormatOptions(format);
                return UnitFormatUtils.Format(host.GetUnits(), spec, p.AsDouble(), false, options);
            }
            if (p.StorageType == StorageType.ElementId)
                return p.AsValueString() ?? owner.GetElement(p.AsElementId())?.Name ?? "";
            return p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.CurrentCulture);
        }
        internal static string RawKey(Parameter p, string source)
        {
            if (!p.HasValue) return "unset";
            // 작업세트의 숫자 ID는 모델마다 다르므로 다른 모델의 동명 작업세트를 합치지 않는다.
            if (p.Id == new ElementId(BuiltInParameter.ELEM_PARTITION_PARAM))
                return "Workset:" + source + ":" + p.AsInteger().ToString(CultureInfo.InvariantCulture);
            return p.StorageType + ":" + (p.StorageType switch
            {
                StorageType.String => p.AsString() ?? "",
                StorageType.Double => p.AsDouble().ToString("R", CultureInfo.InvariantCulture),
                StorageType.Integer => p.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.ElementId => source + ":" + p.AsElementId(),
                _ => throw new InvalidOperationException("지원하지 않는 행 식별값")
            });
        }
        internal static void ConfigureReference(Document doc, Parameter p, UnifiedRow row, Dictionary<string, Dictionary<string, string>> catalogs)
        {
            var v = row.ProjectValue!;
            if (p.Id == new ElementId(BuiltInParameter.ELEM_PARTITION_PARAM) && p.StorageType == StorageType.Integer)
            {
                if (doc.IsLinked) { v.ReadOnlyReason = "원본 로컬 열기 필요 — 같은 Revit에 연 뒤 다시 조회하세요."; return; }
                if (p.IsReadOnly || !doc.IsWorkshared) return;
                v.ReferenceKind = "작업세트";
                using var collector = new FilteredWorksetCollector(doc);
                string worksetKey = row.Source.Key + "|작업세트";
                if (!catalogs.TryGetValue(worksetKey, out var worksets))
                {
                    worksets = collector.OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.UniqueId.ToString("D"), w => w.Name);
                    catalogs.Add(worksetKey, worksets);
                }
                v.ReferenceChoices = worksets;
                v.OriginalReference = doc.GetWorksetTable().GetWorkset(new WorksetId(p.AsInteger()))?.UniqueId.ToString("D") ?? "";
                v.SelectedReference = v.OriginalReference;
                v.ReadOnlyReason = worksets.Count == 0 ? "선택 가능한 사용자 작업세트가 없습니다." : "";
                return;
            }
            if (p.StorageType == StorageType.Double)
            {
                try { v.UnitLabel = LabelUtils.GetLabelForUnit(p.GetUnitTypeId()); } catch { v.UnitLabel = "프로젝트 단위"; }
            }
            if (p.StorageType != StorageType.ElementId || p.IsReadOnly) return;
            string builtin = int.TryParse(p.Id.ToString(), out int id) ? Enum.GetName(typeof(BuiltInParameter), id) ?? "" : "";
            bool material = p.Definition.GetDataType() == SpecTypeId.Reference.Material;
            bool level = new[] { "LEVEL_PARAM", "FAMILY_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "WALL_BASE_CONSTRAINT", "WALL_HEIGHT_TYPE", "ROOF_BASE_LEVEL_PARAM", "STAIRS_BASE_LEVEL_PARAM", "STAIRS_TOP_LEVEL_PARAM" }.Contains(builtin);
            if (!material && !level) return;
            v.ReferenceKind = material ? "재료" : "레벨";
            v.OriginalReference = doc.GetElement(p.AsElementId())?.UniqueId ?? "";
            v.SelectedReference = v.OriginalReference;
            string key = row.Source.Key + "|" + v.ReferenceKind;
            if (!catalogs.TryGetValue(key, out var choices))
            {
                choices = new FilteredElementCollector(doc).OfClass(material ? typeof(Material) : typeof(Level)).ToDictionary(e => e.UniqueId, e => e.Name);
                catalogs.Add(key, choices);
            }
            v.ReferenceChoices = choices;
            v.ReadOnlyReason = "";
        }
        internal static void ConfigureEditors(ScheduleEditorSnapshot data)
        {
            foreach (var field in data.Fields)
            {
                var targets = data.Rows.SelectMany(r => r.Targets.TryGetValue(field.Id, out var list) ? list : new List<UnifiedRow>()).Distinct().ToList();
                var values = targets.Select(t => t.ProjectValue!).Where(v => v != null).ToList();
                field.ConnectionFailed = field.Reason.Length == 0 && values.Count == 0;
                if (field.Reason.Length > 0 || values.Count == 0 || targets.All(t => !t.Editable))
                {
                    field.Editor = ScheduleEditorKind.ReadOnly;
                    field.Hint = field.Reason.Length > 0 ? field.Reason : values.Count == 0 ? "행과 요소의 대응을 확인할 수 없어 표시만 합니다." : string.Join(" / ", targets.Select(t => t.Source.BlockReason + t.ProjectValue?.ReadOnlyReason).Distinct());
                    continue;
                }
                // 잠긴 행의 참조 편집기는 구성하지 않는다. 그 행 때문에 정상 행까지 막지 않는다.
                var editableValues = targets.Where(t => t.Editable).Select(t => t.ProjectValue!).ToList();
                if (editableValues.Select(v => v.Storage + "|" + v.DataType + "|" + v.ReferenceKind).Distinct().Count() != 1)
                {
                    field.Editor = ScheduleEditorKind.ReadOnly; field.Reason = field.Hint = "모델별 데이터 종류가 달라 일괄 편집할 수 없습니다."; continue;
                }
                var value = editableValues[0];
                field.Editor = value.ReferenceKind.Length > 0 ? ScheduleEditorKind.Reference : value.YesNo ? ScheduleEditorKind.Boolean : value.Integer ? ScheduleEditorKind.Integer :
                    value.Storage == (int)StorageType.Double ? ScheduleEditorKind.Number : ScheduleEditorKind.Text;
                field.Hint = field.Editor switch
                {
                    ScheduleEditorKind.Reference => value.ReferenceKind + " — 선택 행의 소속 모델별로 직접 선택합니다." + (value.ReferenceKind == "레벨" ? " 레벨 변경은 위치/높이에 영향을 줄 수 있습니다." : value.ReferenceKind == "작업세트" ? " 요소의 소속 작업세트가 바뀝니다. 작업세트 생성·이름 변경·강제 소유권 취득은 하지 않습니다." : ""),
                    ScheduleEditorKind.Boolean => "예 / 아니요를 선택하세요.",
                    ScheduleEditorKind.Integer => "정수 입력 (예: 12). 빈 값으로 지울 수 없습니다.",
                    ScheduleEditorKind.Number => "숫자 입력 · 단위를 함께 입력할 수 있습니다. 생략 시 모델별 프로젝트 단위: " + string.Join(" / ", targets.Select(t => t.Source.Name + " = " + t.ProjectValue!.UnitLabel).Distinct()),
                    _ => field.IsIfcEntity ? "IFC 엔티티 이름 입력 (예: IfcWall, IfcSlab). 비우면 기존 지정 삭제. IFC 내보내기 여부/미리 정의된 유형은 자동 변경하지 않습니다. 스키마 적합성은 실제 IFC 내보내기에서 확인하세요."
                        : "텍스트 입력 · 비우면 기존 내용을 지웁니다."
                };
            }
        }
    }
}
