using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    internal static class ScheduleEditorService
    {
        private sealed class Candidate
        {
            internal Document Document = null!;
            internal Element Element = null!;
            internal UnifiedSource Source = null!;
            internal string Placement = "";
        }
        private static (Element Owner, Parameter Parameter)? Resolve(Document host, ScheduleField field, Candidate candidate)
        {
            if (field.IsCalculatedField || field.IsCombinedParameterField || field.ParameterId == ElementId.InvalidElementId ||
                (field.FieldType != ScheduleFieldType.Instance && field.FieldType != ScheduleFieldType.ElementType)) return null;
            var shared = host.GetElement(field.ParameterId) as SharedParameterElement;
            bool builtin = long.TryParse(field.ParameterId.ToString(), out long number) && number < 0;
            if (!candidate.Source.IsHost && !builtin && shared == null) return null;
            Parameter? Find(Element owner) => shared != null ? owner.get_Parameter(shared.GuidValue) :
                owner.Parameters.Cast<Parameter>().SingleOrDefault(p => p.Id == field.ParameterId);
            Element owner = candidate.Element;
            if (field.FieldType == ScheduleFieldType.ElementType) owner = candidate.Document.GetElement(owner.GetTypeId());
            if (owner == null) return null;
            Parameter? p = Find(owner);
            if (p == null && field.FieldType == ScheduleFieldType.Instance)
            {
                owner = candidate.Document.GetElement(candidate.Element.GetTypeId());
                if (owner != null) p = Find(owner);
            }
            return p == null || owner == null ? null : (owner, p);
        }

        internal static ScheduleEditorSnapshot Read(UIApplication app, ViewSchedule schedule)
        {
            Document host = app.ActiveUIDocument.Document;
            var definition = schedule.Definition;
            if (definition.IsKeySchedule || definition.IsMaterialTakeoff || definition.HasEmbeddedSchedule)
                throw new InvalidOperationException("첫 버전은 일반 요소 일람표만 지원합니다. 키 일람표·재료 수량 산출·내포된 일람표는 편집하지 않습니다.");
            if (host.IsReadOnly || host.IsModifiable) throw new InvalidOperationException("읽기 전용/편집 중 문서에서는 일람표를 조회할 수 없습니다.");
            var data = new ScheduleEditorSnapshot { ScheduleId = schedule.UniqueId, Name = schedule.Name };
            var sources = UnifiedEditService.Collect(host, false, app, false);
            var candidates = new List<Candidate>();
            foreach (Element e in new FilteredElementCollector(host, schedule.Id).WhereElementIsNotElementType())
                if (e is not RevitLinkInstance && e is not RevitLinkType)
                    candidates.Add(new Candidate { Document = host, Element = e, Source = sources[0], Placement = "host" });
            if (definition.IncludeLinkedFiles)
            {
#if REVIT2023
                throw new InvalidOperationException("Revit 2023은 링크 포함 일람표의 정확한 행 조회를 지원하지 않습니다. 이 일람표는 Revit 2024 이상에서 실행하세요.");
#else
                foreach (RevitLinkInstance instance in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)))
                {
                    var link = host.GetElement(instance.GetTypeId()) as RevitLinkType;
                    Document? linked = instance.GetLinkDocument();
                    if (link == null || linked == null) continue;
                    string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(link.GetExternalFileReference().GetAbsolutePath());
                    var source = sources.FirstOrDefault(s => s.LinkUniqueId == link.UniqueId) ?? sources.FirstOrDefault(s => !s.IsHost && s.Path == path);
                    if (source == null) throw new InvalidOperationException("링크 소속을 확인할 수 없습니다: " + link.Name);
                    foreach (Element e in new FilteredElementCollector(host, schedule.Id, instance.Id).WhereElementIsNotElementType())
                        candidates.Add(new Candidate { Document = linked, Element = e, Source = source, Placement = instance.UniqueId });
                }
#endif
            }
            if (candidates.Count == 0 || candidates.Count > 20000) throw new InvalidOperationException("조회 대상이 없거나 20,000개를 넘습니다. 일람표 필터로 범위를 줄여 주세요.");
            var originalFields = definition.GetFieldOrder().Select(definition.GetField).ToList();
            var shownFields = originalFields.Where(f => !f.IsHidden).ToList();
            foreach (var field in shownFields)
                data.Fields.Add(new ScheduleEditorField { Id = field.FieldId.ToString(), Name = field.ColumnHeading.Length > 0 ? field.ColumnHeading : field.GetName(),
                    IsType = field.FieldType == ScheduleFieldType.ElementType,
                    Reason = field.IsCalculatedField || field.IsCombinedParameterField || field.DisplayType != ScheduleFieldDisplayType.Standard ||
                        (field.FieldType != ScheduleFieldType.Instance && field.FieldType != ScheduleFieldType.ElementType)
                        ? "계산식·합성·관련 요소·합계 필드는 직접 편집할 수 없습니다." : "" });
            // 표시 단위와 원시값을 함께 검증한다. 모호한 행은 순서로 추측하지 않는다.
            bool Direct(ScheduleField f) => !f.IsCalculatedField && !f.IsCombinedParameterField && f.DisplayType == ScheduleFieldDisplayType.Standard && candidates.All(c => Resolve(host, f, c) is { } p && p.Parameter.StorageType != StorageType.None);
            var identityFields = definition.IsItemized ? shownFields.Where(Direct).ToList()
                : definition.GetSortGroupFields().Select(s => definition.GetField(s.FieldId)).ToList();
            string mappingError = (definition.IsItemized && identityFields.Count == 0) || identityFields.Any(f => !Direct(f))
                ? "행과 요소를 정확히 연결할 직접 매개변수 필드가 부족합니다. 계산식/관련 요소 기준은 표시만 합니다." : "";
            var cells = new List<Dictionary<string, string>>();
            var counts = new List<int>();
            // 원본 뷰/요소는 바꾸지 않는다. 임시 복제에서 머리글·합계·빈 구분행만 제외하고 원래 정렬을 유지한다.
            using (var tx = new Transaction(host, "일람표 행 읽기 — 임시 복제 후 취소"))
            {
                tx.Start();
                try
                {
                    var copy = (ViewSchedule)host.GetElement(schedule.Duplicate(ViewDuplicateOption.Duplicate));
                    copy.ViewTemplateId = ElementId.InvalidElementId;
                    var d = copy.Definition; d.ShowTitle = false; d.ShowHeaders = false; d.ShowGrandTotal = false;
                    var sort = d.GetSortGroupFields();
                    foreach (var s in sort) { s.ShowHeader = false; s.ShowFooter = false; s.ShowBlankLine = false; }
                    d.SetSortGroupFields(sort);
                    foreach (var id in identityFields.Select(f => f.FieldId)) d.GetField(id).IsHidden = false;
                    var countField = d.GetFieldOrder().Select(d.GetField).FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count) ?? d.AddField(ScheduleFieldType.Count);
                    countField.IsHidden = false;
                    host.Regenerate();
                    var visible = d.GetFieldOrder().Select(d.GetField).Where(f => !f.IsHidden).ToList();
                    var section = copy.GetTableData().GetSectionData(SectionType.Body);
                    if (section.NumberOfColumns != visible.Count || section.NumberOfRows > 5000) throw new InvalidOperationException("일람표 열 구성이 일치하지 않거나 데이터 행이 5,000개를 넘습니다.");
                    for (int r = section.FirstRowNumber; r <= section.LastRowNumber; r++)
                    {
                        var row = new Dictionary<string, string>();
                        for (int c = 0; c < visible.Count; c++) row[visible[c].FieldId.ToString()] = copy.GetCellText(SectionType.Body, r, section.FirstColumnNumber + c);
                        if (!int.TryParse(row[countField.FieldId.ToString()], out int count) || count < 1) throw new InvalidOperationException("데이터 행과 합계/머리글을 구분할 수 없어 중단했습니다.");
                        counts.Add(count);
                        cells.Add(row);
                    }
                }
                finally { tx.RollBack(); }
            }
            var groups = cells.Select(_ => new List<int>()).ToList();
            if (mappingError.Length == 0) try
            {
                // 어떤 표시 필드가 특수 서식이어도 다른 검증 가능한 식별 필드를 사용할 수 있다.
                var columns = new Dictionary<string, List<string>>();
                foreach (var f in identityFields.ToList())
                {
                    string id = f.FieldId.ToString();
                    try
                    {
                        var values = candidates.Select(c => ScheduleEditorParameters.Display(host, c.Document, f, Resolve(host, f, c)!.Value.Parameter)).ToList();
                        var expected = values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
                        var actual = cells.Select((r, i) => new { value = r[id], count = counts[i] }).GroupBy(p => p.value).ToDictionary(g => g.Key, g => g.Sum(p => p.count));
                        if (expected.Count != actual.Count || expected.Any(p => !actual.TryGetValue(p.Key, out int n) || n != p.Value))
                            throw new InvalidOperationException(f.GetName() + ": 표시값/요소 분포 불일치");
                        columns.Add(id, values);
                    }
                    catch when (definition.IsItemized) { identityFields.Remove(f); }
                }
                if (definition.IsItemized && identityFields.Count == 0) throw new InvalidOperationException("표시값이 정확히 대응하는 식별 필드가 없습니다.");
                var candidateKeys = candidates.Select((c, i) => ScheduleEditorLogic.Key(identityFields.Select(f => columns[f.FieldId.ToString()][i]))).ToList();
                var rawKeys = candidates.Select(c => ScheduleEditorLogic.Key(identityFields.Select(f => ScheduleEditorParameters.RawKey(Resolve(host, f, c)!.Value.Parameter, c.Source.Key)))).ToList();
                if (candidateKeys.Select((key, i) => new { key, raw = rawKeys[i] }).GroupBy(p => p.key).Any(g => g.Select(p => p.raw).Distinct().Count() > 1))
                    throw new InvalidOperationException("같은 표시값에 서로 다른 원시값이 있습니다. 숫자 반올림/동명 참조로 대상을 구분할 수 없습니다.");
                var displayedKeys = cells.Select(row => ScheduleEditorLogic.Key(identityFields.Select(f => row[f.FieldId.ToString()]))).ToList();
                groups = ScheduleEditorLogic.Match(displayedKeys, candidateKeys, definition.IsItemized);
                if (groups.Where((g, i) => g.Count != counts[i]).Any()) throw new InvalidOperationException("행 표시 개수와 요소 개수가 다릅니다.");
            }
            catch (Exception ex) { mappingError = "표시만 가능: " + ex.GetBaseException().Message; }
            var dependencyFields = identityFields.Concat(definition.GetSortGroupFields().Select(f => definition.GetField(f.FieldId)))
                .Concat(definition.GetFilters().Select(f => definition.GetField(f.FieldId))).GroupBy(f => f.FieldId.ToString()).Select(g => g.First()).ToList();
            foreach (var member in candidates)
            {
                var guard = new ScheduleEditorGuard { SourceKey = member.Source.Key, ElementId = member.Element.UniqueId,
                    TypeId = member.Document.GetElement(member.Element.GetTypeId())?.UniqueId ?? "" };
                foreach (var f in dependencyFields)
                {
                    var resolved = Resolve(host, f, member);
                    if (resolved == null) { mappingError = "표시만 가능 — 필터/정렬 조건을 적용 전에 재검증할 수 없습니다: " + f.GetName(); continue; }
                    guard.Values.Add(ProjectInformationService.Snapshot(member.Document, resolved.Value.Owner, resolved.Value.Parameter, member.Source));
                }
                data.Guards.Add(guard);
            }
            var parameterCache = new Dictionary<string, UnifiedRow>();
            var referenceCatalogs = new Dictionary<string, Dictionary<string, string>>();
            for (int i = 0; i < cells.Count; i++)
            {
                var members = mappingError.Length == 0 ? groups[i].Select(index => candidates[index]).ToList() : new List<Candidate>();
                var row = new ScheduleEditorRow { Number = i + 1, Reason = mappingError, ElementCount = members.Count == 0 ? counts[i] : members.Select(m => m.Source.Key + "|" + m.Element.UniqueId).Distinct().Count(),
                    Models = members.Count == 0 ? "소속 확인 불가" : string.Join(", ", members.Select(m => m.Source.Name).Distinct()),
                    Identity = ScheduleEditorLogic.Key(members.Select(m => m.Source.Key + "|" + m.Placement + "|" + m.Element.UniqueId).OrderBy(s => s)) };
                foreach (var pair in cells[i]) row.Cells[pair.Key] = pair.Value;
                foreach (var f in shownFields)
                {
                    string id = f.FieldId.ToString(); var targets = new List<UnifiedRow>();
                    foreach (var member in members)
                    {
                        var resolved = Resolve(host, f, member);
                        if (resolved == null) { row.Errors[id] = "직접 편집 가능한 동일 매개변수를 확인할 수 없습니다. 링크 사용자 매개변수는 공유 GUID가 필요합니다."; break; }
                        var snapshot = ProjectInformationService.Snapshot(member.Document, resolved.Value.Owner, resolved.Value.Parameter, member.Source);
                        ScheduleEditorParameters.ConfigureReference(member.Document, resolved.Value.Parameter, snapshot, referenceCatalogs);
                        if (!parameterCache.TryGetValue(snapshot.Key, out var target)) { target = snapshot; parameterCache.Add(target.Key, target); }
                        if (resolved.Value.Owner is ElementType) data.Fields.First(df => df.Id == id).IsType = true;
                        targets.Add(target);
                    }
                    row.Targets[id] = targets.Distinct().ToList();
                }
                data.Rows.Add(row);
            }
            ScheduleEditorParameters.ConfigureEditors(data);
            return data;
        }
        internal static void ValidateGuards(Document doc, ScheduleEditorSnapshot data, string sourceKey)
        {
            foreach (var guard in data.Guards.Where(g => g.SourceKey == sourceKey))
            {
                Element element = doc.GetElement(guard.ElementId) ?? throw new InvalidOperationException("조회 이후 일람표 요소가 삭제되었습니다.");
                if ((doc.GetElement(element.GetTypeId())?.UniqueId ?? "") != guard.TypeId) throw new InvalidOperationException("조회 이후 일람표 요소의 유형이 변경되었습니다.");
                foreach (var value in guard.Values) ProjectInformationService.Validate(doc, value, false);
            }
        }
    }
}
