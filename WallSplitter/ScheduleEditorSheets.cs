using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 도면목록표는 그려진 모델 형상이 아니다. 링크 뷰용 collector를 시트 조회에 사용하지 않는다.
    internal static class ScheduleEditorSheets
    {
        private sealed class SheetSource
        {
            internal Document Document = null!;
            internal ViewSheet Sheet = null!;
            internal UnifiedSource Source = null!;
            internal RevitLinkInstance? Instance;
            internal RevitLinkType? Link;
            internal string FileName = "";
            internal string Number = "";
            internal string Key => Source.Key + "|" + Sheet.UniqueId;
        }
        private static Parameter? Parameter(Document host, ScheduleField field, SheetSource item)
        {
            if (field.IsCalculatedField || field.IsCombinedParameterField || field.FieldType != ScheduleFieldType.Instance) return null;
            if (host.GetElement(field.ParameterId) is SharedParameterElement shared) return item.Sheet.get_Parameter(shared.GuidValue);
            if (int.TryParse(field.ParameterId.ToString(), out int id) && id < -1) return item.Sheet.get_Parameter((BuiltInParameter)id);
            return item.Source.IsHost ? item.Sheet.Parameters.Cast<Parameter>().SingleOrDefault(p => p.Id == field.ParameterId) : null;
        }
        private static string FileName(RevitLinkType link) =>
            link.get_Parameter(BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT)?.AsString() ?? Path.GetFileNameWithoutExtension(link.Name);
        private static string? Display(Document host, ScheduleField field, SheetSource item)
        {
            if (field.FieldType == ScheduleFieldType.RevitLinkType) return item.FileName;
            if (field.FieldType == ScheduleFieldType.RevitLinkInstance)
                return item.Instance == null ? "" : item.Instance.get_Parameter(BuiltInParameter.RVT_LINK_INSTANCE_NAME)?.AsString() ?? item.Instance.Name;
            var p = Parameter(host, field, item);
            return p == null ? null : ScheduleEditorParameters.Display(host, item.Document, field, p);
        }
        internal static ScheduleEditorSnapshot Read(UIApplication app, ViewSchedule schedule)
        {
            var host = app.ActiveUIDocument.Document;
            var definition = schedule.Definition;
            var sources = UnifiedEditService.Collect(host, false, app, false);
            var originals = definition.GetFieldOrder().Select(definition.GetField).ToList();
            var shown = originals.Where(f => !f.IsHidden).ToList();
            var data = new ScheduleEditorSnapshot { ScheduleId = schedule.UniqueId, Name = schedule.Name };
            foreach (var field in shown)
                data.Fields.Add(new ScheduleEditorField { Id = field.FieldId.ToString(), Name = field.ColumnHeading.Length > 0 ? field.ColumnHeading : field.GetName(),
                    Reason = field.IsCalculatedField || field.IsCombinedParameterField || field.DisplayType != ScheduleFieldDisplayType.Standard || field.FieldType != ScheduleFieldType.Instance
                        ? "계산식·합계·링크 정보 등은 표시 전용입니다." : "" });

            var candidates = new List<SheetSource>();
            // 전체 시트를 읽되 표에 실제로 나타나는 키만 선택한다. 필터를 임의로 재구현하지 않는다.
            foreach (ViewSheet sheet in new FilteredElementCollector(host).OfClass(typeof(ViewSheet)))
                candidates.Add(new SheetSource { Document = host, Sheet = sheet, Number = sheet.SheetNumber, Source = sources[0] });
            if (definition.IncludeLinkedFiles)
            {
                foreach (RevitLinkInstance instance in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)))
                {
                    var link = host.GetElement(instance.GetTypeId()) as RevitLinkType;
                    var doc = instance.GetLinkDocument();
                    if (link == null || doc == null) continue;
                    string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(link.GetExternalFileReference().GetAbsolutePath());
                    var source = sources.FirstOrDefault(s => s.LinkUniqueId == link.UniqueId) ?? sources.FirstOrDefault(s => !s.IsHost && LinkedModelEditService.SamePath(s.Path, path));
                    if (source == null) continue;
                    if (source.Editable) doc = LinkedModelEditService.FindOpenTarget(app, path) ?? doc;
                    foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
                        candidates.Add(new SheetSource { Document = doc, Sheet = sheet, Number = sheet.SheetNumber, Source = source, Instance = instance, Link = link, FileName = FileName(link) });
                }
            }
            if (candidates.Count > 20000) throw new InvalidOperationException("시트 후보가 20,000개를 넘습니다.");
            var sortFields = definition.GetSortGroupFields().Select(s => definition.GetField(s.FieldId)).ToList();
            var filters = definition.GetFilters().Select(f => definition.GetField(f.FieldId)).ToList();
            var identity = (definition.IsItemized ? shown : sortFields).Where(f => !f.IsCalculatedField && !f.IsCombinedParameterField && f.DisplayType == ScheduleFieldDisplayType.Standard &&
                (f.FieldType == ScheduleFieldType.Instance || f.FieldType == ScheduleFieldType.RevitLinkInstance || f.FieldType == ScheduleFieldType.RevitLinkType)).ToList();
            var cells = new List<Dictionary<string, string>>();
            var counts = new List<int>();
            string numberId = "", fileId = "";
            using (var tx = new Transaction(host, "도면목록표 읽기 — 임시 복제 후 취소"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("도면목록표 조회 시작 실패");
                try
                {
                    var copy = (ViewSchedule)host.GetElement(schedule.Duplicate(ViewDuplicateOption.Duplicate));
                    copy.ViewTemplateId = ElementId.InvalidElementId;
                    var d = copy.Definition; d.ShowTitle = false; d.ShowHeaders = false; d.ShowGrandTotal = false;
                    var sort = d.GetSortGroupFields();
                    foreach (var s in sort) { s.ShowHeader = false; s.ShowFooter = false; s.ShowBlankLine = false; }
                    d.SetSortGroupFields(sort);
                    foreach (var f in identity) d.GetField(f.FieldId).IsHidden = false;
                    ScheduleField Ensure(ScheduleFieldType type, BuiltInParameter parameter)
                    {
                        var f = d.GetFieldOrder().Select(d.GetField).FirstOrDefault(f => f.FieldType == type && f.ParameterId == new ElementId(parameter))
                            ?? d.AddField(type, new ElementId(parameter));
                        f.IsHidden = false; return f;
                    }
                    // 개별 행의 정렬은 바꾸지 않고 식별용 열만 임시로 읽는다. 원본에는 추가하지 않는다.
                    if (definition.IsItemized)
                    {
                        numberId = Ensure(ScheduleFieldType.Instance, BuiltInParameter.SHEET_NUMBER).FieldId.ToString();
                        if (definition.IncludeLinkedFiles) fileId = Ensure(ScheduleFieldType.RevitLinkType, BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT).FieldId.ToString();
                    }
                    var countField = d.GetFieldOrder().Select(d.GetField).FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count) ?? d.AddField(ScheduleFieldType.Count);
                    countField.IsHidden = false;
                    host.Regenerate();
                    var visible = d.GetFieldOrder().Select(d.GetField).Where(f => !f.IsHidden).ToList();
                    var section = copy.GetTableData().GetSectionData(SectionType.Body);
                    if (section.NumberOfRows > 5000 || section.NumberOfColumns != visible.Count) throw new InvalidOperationException("도면목록표 행/열 구성을 확인할 수 없습니다.");
                    for (int r = section.FirstRowNumber; r <= section.LastRowNumber; r++)
                    {
                        var row = new Dictionary<string, string>();
                        for (int c = 0; c < visible.Count; c++) row[visible[c].FieldId.ToString()] = copy.GetCellText(SectionType.Body, r, section.FirstColumnNumber + c);
                        if (!int.TryParse(row[countField.FieldId.ToString()], out int count) || count < 1) throw new InvalidOperationException("도면목록표의 데이터 행을 확인할 수 없습니다.");
                        cells.Add(row); counts.Add(count);
                    }
                }
                finally { tx.RollBack(); }
            }
            var parameterCache = new Dictionary<string, UnifiedRow>();
            var catalogs = new Dictionary<string, Dictionary<string, string>>();
            var guarded = new HashSet<string>();
            var byNumber = candidates.GroupBy(m => m.Number).ToDictionary(g => g.Key, g => g.ToList());
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var pool = numberId.Length == 0 ? candidates : byNumber.TryGetValue(cell[numberId], out var sheets) ? sheets : new List<SheetSource>();
                var matches = pool.Where(item =>
                    (fileId.Length == 0 || item.FileName == cell[fileId])).ToList();
                // 다른 표시값이 있는 동일 번호(시트 컬렉션 등)는 추가 직접 필드로 구분한다.
                foreach (var field in identity)
                {
                    string id = field.FieldId.ToString();
                    var comparable = matches.Select(item => new { item, value = Display(host, field, item) }).ToList();
                    if (comparable.Any(v => v.value == null)) continue;
                    var filtered = comparable.Where(v => v.value == cell[id]).Select(v => v.item).ToList();
                    if (!definition.IsItemized || filtered.Count > 0) matches = filtered;
                }
                var unique = matches.GroupBy(m => m.Key).Select(g => g.First()).ToList();
                string error = "";
                if (definition.IsItemized && unique.Count != 1) error = "이 행의 원본 시트를 고유하게 구분할 수 없습니다.";
                if (!definition.IsItemized && (matches.Count != counts[i] || sortFields.Any(f => !identity.Contains(f)) ||
                    identity.Any(f => matches.Any(m => Display(host, f, m) == null)))) error = "묶인 행의 원본 시트/정렬 기준/개수를 확인할 수 없습니다.";
                if (unique.Count == 0) error = "이 행에 해당하는 원본 시트가 없습니다. 링크 상태를 확인하세요.";
                if (error.Length == 0 && filters.Concat(sortFields).Any(f => f.FieldType != ScheduleFieldType.RevitLinkType && f.FieldType != ScheduleFieldType.RevitLinkInstance && unique.Any(m => Parameter(host, f, m) == null)))
                    error = "이 행의 필터/정렬 원본값을 재검증할 수 없습니다.";
                if (error.Length == 0 && !definition.IsItemized && identity.Any(f =>
                    f.FieldType == ScheduleFieldType.Instance && unique.Select(m => ScheduleEditorParameters.RawKey(Parameter(host, f, m)!, m.Source.Key)).Distinct().Count() > 1))
                    error = "묶인 행에 표시만 같고 원시값이 다른 시트가 있습니다.";
                var row = new ScheduleEditorRow { Number = i + 1, ElementCount = error.Length == 0 ? unique.Count : counts[i], Reason = error,
                    Models = error.Length == 0 ? string.Join(", ", unique.Select(m => m.Source.Name).Distinct()) : "소속 확인 불가",
                    Identity = error.Length == 0 ? ScheduleEditorLogic.Key(unique.Select(m => m.Key).OrderBy(k => k)) : "" };
                foreach (var p in cell) row.Cells[p.Key] = p.Value;
                if (error.Length == 0)
                {
                    foreach (var item in unique)
                    {
                        if (guarded.Add(item.Key))
                        {
                            var guard = new ScheduleEditorGuard { SourceKey = item.Source.Key, ElementId = item.Sheet.UniqueId, TypeId = item.Document.GetElement(item.Sheet.GetTypeId())?.UniqueId ?? "" };
                            var number = item.Sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER);
                            if (number != null) guard.Values.Add(ProjectInformationService.Snapshot(item.Document, item.Sheet, number, item.Source));
                            // 순서/필터/표시값을 원본에서도 재검증한다. 링크 정보는 최종 재조회 fingerprint로 검증한다.
                            foreach (var field in originals.Concat(filters).Concat(sortFields).GroupBy(f => f.FieldId.ToString()).Select(g => g.First()))
                            {
                                var p = Parameter(host, field, item);
                                if (p != null) guard.Values.Add(ProjectInformationService.Snapshot(item.Document, item.Sheet, p, item.Source));
                            }
                            data.Guards.Add(guard);
                        }
                    }
                    foreach (var field in shown)
                    {
                        string id = field.FieldId.ToString(); var targets = new List<UnifiedRow>();
                        foreach (var item in unique)
                        {
                            var p = Parameter(host, field, item);
                            if (p == null) { row.Errors[id] = "이 모델의 동일 매개변수를 확인할 수 없습니다. 링크 사용자 매개변수는 공유 GUID가 필요합니다."; break; }
                            if (definition.IsItemized && p.StorageType == StorageType.String && (p.AsString() ?? "") != cell[id])
                            { row.Errors[id] = "일람표 표시와 원본의 현재 값이 다릅니다. 링크를 갱신한 뒤 다시 조회하세요."; break; }
                            var target = ProjectInformationService.Snapshot(item.Document, item.Sheet, p, item.Source);
                            ScheduleEditorParameters.ConfigureReference(item.Document, p, target, catalogs);
                            if (!parameterCache.TryGetValue(target.Key, out var cached)) { cached = target; parameterCache.Add(target.Key, cached); }
                            targets.Add(cached);
                        }
                        row.Targets[id] = targets;
                    }
                }
                data.Rows.Add(row);
            }
            ScheduleEditorParameters.ConfigureEditors(data);
            return data;
        }
    }
}
