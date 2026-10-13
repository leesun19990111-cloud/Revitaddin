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
            // 첫 버전의 행 식별은 반올림/단위 해석이 필요 없는 텍스트 필드만 사용한다.
            var identityFields = definition.IsItemized ? shownFields.Where(f => candidates.All(c => Resolve(host, f, c)?.Parameter.StorageType == StorageType.String)).ToList()
                : definition.GetSortGroupFields().Select(s => definition.GetField(s.FieldId)).ToList();
            if ((definition.IsItemized && identityFields.Count == 0) || identityFields.Any(f => candidates.Any(c => Resolve(host, f, c)?.Parameter.StorageType != StorageType.String)))
                throw new InvalidOperationException("행을 정확히 식별할 텍스트 필드가 필요합니다. 개별 행은 고유한 마크 등 텍스트 필드를 표시하고, 묶인 행은 텍스트 필드로 정렬/그룹화해 주세요. 숫자·레벨·계산식 기준은 첫 버전에서 차단합니다.");
            var candidateKeys = candidates.Select(c => ScheduleEditorLogic.Key(identityFields.Select(f => Resolve(host, f, c)!.Value.Parameter.AsString() ?? ""))).ToList();
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
            var displayedKeys = cells.Select(row => ScheduleEditorLogic.Key(identityFields.Select(f => row[f.FieldId.ToString()]))).ToList();
            var groups = ScheduleEditorLogic.Match(displayedKeys, candidateKeys, definition.IsItemized);
            if (groups.Where((g, i) => g.Count != counts[i]).Any()) throw new InvalidOperationException("행에 표시된 실제 개수와 조회한 요소 개수가 다릅니다. 해당 행을 수정하지 않습니다.");
            var dependencyFields = identityFields.Concat(definition.GetFilters().Select(f => definition.GetField(f.FieldId))).Distinct().ToList();
            foreach (var member in candidates)
            {
                var guard = new ScheduleEditorGuard { SourceKey = member.Source.Key, ElementId = member.Element.UniqueId,
                    TypeId = member.Document.GetElement(member.Element.GetTypeId())?.UniqueId ?? "" };
                foreach (var f in dependencyFields)
                {
                    var resolved = Resolve(host, f, member);
                    if (resolved == null) throw new InvalidOperationException("행의 필터/정렬 조건을 적용 전에 재검증할 수 없습니다: " + f.GetName());
                    guard.Values.Add(ProjectInformationService.Snapshot(member.Document, resolved.Value.Owner, resolved.Value.Parameter, member.Source));
                }
                data.Guards.Add(guard);
            }
            var parameterCache = new Dictionary<string, UnifiedRow>();
            for (int i = 0; i < cells.Count; i++)
            {
                var members = groups[i].Select(index => candidates[index]).ToList();
                var row = new ScheduleEditorRow { Number = i + 1, ElementCount = members.Select(m => m.Source.Key + "|" + m.Element.UniqueId).Distinct().Count(),
                    Models = string.Join(", ", members.Select(m => m.Source.Name).Distinct()),
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
                        if (!parameterCache.TryGetValue(snapshot.Key, out var target)) { target = snapshot; parameterCache.Add(target.Key, target); }
                        if (resolved.Value.Owner is ElementType) data.Fields.First(df => df.Id == id).IsType = true;
                        targets.Add(target);
                    }
                    row.Targets[id] = targets.Distinct().ToList();
                }
                data.Rows.Add(row);
            }
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
