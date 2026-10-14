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
            internal RevitLinkType? Link;
            internal RevitLinkInstance? Instance;
        }
        private static bool LinkField(ScheduleField field) =>
            field.FieldType == ScheduleFieldType.RevitLinkType && field.ParameterId == new ElementId(BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT) ||
            field.FieldType == ScheduleFieldType.RevitLinkInstance && field.ParameterId == new ElementId(BuiltInParameter.RVT_LINK_INSTANCE_NAME);
        private static string LinkValue(Candidate c, bool instance) => instance
            ? c.Instance?.get_Parameter(BuiltInParameter.RVT_LINK_INSTANCE_NAME)?.AsString() ?? c.Instance?.Name ?? ""
            : c.Link?.get_Parameter(BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT)?.AsString() ?? (c.Link == null ? "" : System.IO.Path.GetFileNameWithoutExtension(c.Link.Name));
        private static string Display(Document host, ScheduleField f, Candidate c) => LinkField(f)
            ? LinkValue(c, f.FieldType == ScheduleFieldType.RevitLinkInstance)
            : Resolve(host, f, c) is { } p ? ScheduleEditorParameters.Display(host, c.Document, f, p.Parameter) : "";
        private static ScheduleEditorParameterIdentity? ParameterIdentity(Document host, ScheduleField field, bool isHost)
        {
            if (field.IsCalculatedField || field.IsCombinedParameterField || field.ParameterId == ElementId.InvalidElementId ||
                (field.FieldType != ScheduleFieldType.Instance && field.FieldType != ScheduleFieldType.ElementType && field.FieldType != ScheduleFieldType.ProjectInfo)) return null;
            if (!long.TryParse(field.ParameterId.ToString(), out long id)) return null;
            var shared = host.GetElement(field.ParameterId) as SharedParameterElement;
            var definition = (host.GetElement(field.ParameterId) as ParameterElement)?.GetDefinition();
            if (!isHost && id > 0 && shared == null && definition == null) return null;
            return new ScheduleEditorParameterIdentity { Id = id, Guid = shared?.GuidValue.ToString() ?? "", Name = definition?.Name ?? "",
                DataType = definition?.GetDataType().TypeId ?? "", IsHost = isHost, IsType = field.FieldType == ScheduleFieldType.ElementType,
                IsProjectInfo = field.FieldType == ScheduleFieldType.ProjectInfo };
        }
        private static (Element Owner, Parameter Parameter)? Resolve(Document host, ScheduleField field, Candidate candidate)
        {
            var key = ParameterIdentity(host, field, candidate.Source.IsHost);
            return key == null ? null : Resolve(candidate.Document, candidate.Element, key);
        }
        private static (Element Owner, Parameter Parameter)? Resolve(Document document, Element element, ScheduleEditorParameterIdentity key)
        {
            Parameter? Find(Element owner)
            {
                if (key.Guid.Length > 0) return owner.get_Parameter(Guid.Parse(key.Guid));
                if (key.Id < 0) return owner.get_Parameter((BuiltInParameter)key.Id);
                var matches = owner.Parameters.Cast<Parameter>().Where(p => key.IsHost ? p.Id.ToString() == key.Id.ToString() :
                    !p.IsShared && p.Definition.Name == key.Name && p.Definition.GetDataType().TypeId == key.DataType &&
                    document.GetElement(p.Id) is ParameterElement).ToList();
                return matches.Count == 1 ? matches[0] : null;
            }
            Element? owner = element;
            if (key.IsProjectInfo) owner = document.ProjectInformation;
            if (key.IsType) owner = document.GetElement(element.GetTypeId());
            if (owner == null) return null;
            Parameter? p = Find(owner);
            if (p == null && !key.IsType && !key.IsProjectInfo)
            {
                owner = document.GetElement(element.GetTypeId());
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
            if (definition.CategoryId == new ElementId(BuiltInCategory.OST_Sheets))
                return ScheduleEditorSheets.Read(app, schedule);
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
                        candidates.Add(new Candidate { Document = linked, Element = e, Source = source, Placement = instance.UniqueId, Link = link, Instance = instance });
                }
#endif
            }
            if (candidates.Count == 0 || candidates.Count > 20000) throw new InvalidOperationException("조회 대상이 없거나 20,000개를 넘습니다. 일람표 필터로 범위를 줄여 주세요.");
            data.Diagnostics.Add("행 연결 진단 v108 · 링크 포함=" + definition.IncludeLinkedFiles + " · 개별 행=" + definition.IsItemized);
            foreach (var group in candidates.GroupBy(c => c.Source.Key))
                data.Diagnostics.Add(group.First().Source.Name + " · 수집 후보 " + group.Count() + "개");
            var originalFields = definition.GetFieldOrder().Select(definition.GetField).ToList();
            var shownFields = originalFields.Where(f => !f.IsHidden).ToList();
            foreach (var field in shownFields)
                data.Fields.Add(new ScheduleEditorField { Id = field.FieldId.ToString(), Name = field.ColumnHeading.Length > 0 ? field.ColumnHeading : field.GetName(),
                    IsType = field.FieldType == ScheduleFieldType.ElementType, IsProjectInfo = field.FieldType == ScheduleFieldType.ProjectInfo,
                    IsIfcEntity = field.ParameterId == new ElementId(BuiltInParameter.IFC_EXPORT_ELEMENT_AS) || field.ParameterId == new ElementId(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS),
                    Reason = field.IsCalculatedField || field.IsCombinedParameterField || field.DisplayType != ScheduleFieldDisplayType.Standard ||
                        (field.FieldType != ScheduleFieldType.Instance && field.FieldType != ScheduleFieldType.ElementType && field.FieldType != ScheduleFieldType.ProjectInfo)
                        ? "계산식·합성·관련 요소·합계 필드는 직접 편집할 수 없습니다." : "" });
            // 표시 단위와 원시값을 함께 검증한다. 모호한 행은 순서로 추측하지 않는다.
            bool Direct(ScheduleField f) => !f.IsCalculatedField && !f.IsCombinedParameterField && f.DisplayType == ScheduleFieldDisplayType.Standard && (LinkField(f) || candidates.Any(c => Resolve(host, f, c) is { } p && p.Parameter.StorageType != StorageType.None));
            var identityFields = definition.IsItemized ? shownFields.Where(Direct).ToList()
                : definition.GetSortGroupFields().Select(s => definition.GetField(s.FieldId)).ToList();
            foreach (var field in shownFields.Where(f => !identityFields.Contains(f)))
                data.Diagnostics.Add(field.GetName() + " · " + field.FieldType + " · 식별 후보 제외 · 매개변수 연결 " + candidates.Count(c => Resolve(host, field, c) != null) + "/" + candidates.Count);
            string mappingError = !definition.IsItemized && identityFields.Any(f => !Direct(f))
                ? "행과 요소를 정확히 연결할 직접 매개변수 필드가 부족합니다. 계산식/관련 요소 기준은 표시만 합니다." : "";
            var cells = new List<Dictionary<string, string>>();
            var counts = new List<int>();
            var linkColumns = new Dictionary<string, bool>();
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
                    // 그룹을 쪼개지 않도록 개별 행에서만 모델/배치 식별 열을 임시로 노출한다.
                    if (definition.IsItemized && definition.IncludeLinkedFiles)
                    {
                        foreach (bool instance in new[] { false, true })
                        {
                            var type = instance ? ScheduleFieldType.RevitLinkInstance : ScheduleFieldType.RevitLinkType;
                            var parameter = new ElementId(instance ? BuiltInParameter.RVT_LINK_INSTANCE_NAME : BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT);
                            try
                            {
                                var helper = d.GetFieldOrder().Select(d.GetField).FirstOrDefault(f => f.FieldType == type && f.ParameterId == parameter) ?? d.AddField(type, parameter);
                                helper.IsHidden = false; linkColumns[helper.FieldId.ToString()] = instance;
                            }
                            catch (Exception ex) { data.Diagnostics.Add("보조 열 추가 불가: " + type + " · " + ex.GetBaseException().Message); }
                        }
                    }
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
            data.Diagnostics.Add("표 데이터 " + cells.Count + "행 · 표시 요소 합계 " + counts.Sum() + "개 · 후보 합계 " + candidates.Count + "개");
            var matches = cells.Select(_ => new ScheduleEditorLogic.RowMatch()).ToList();
            if (mappingError.Length == 0) try
            {
                // 어떤 표시 필드가 특수 서식이어도 다른 검증 가능한 식별 필드를 사용할 수 있다.
                var columns = new Dictionary<string, List<string>>();
                foreach (var f in identityFields.ToList())
                {
                    string id = f.FieldId.ToString();
                    try
                    {
                        var values = candidates.Select(c => Display(host, f, c)).ToList();
                        var expected = values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
                        var actual = cells.Select((r, i) => new { value = r[id], count = counts[i] }).GroupBy(p => p.value).ToDictionary(g => g.Key, g => g.Sum(p => p.count));
                        // 뷰 수집기에 표 밖 후보가 섞여도 정상 키까지 버리지 않는다. 마지막 복합키별 개수는 반드시 정확히 대조한다.
                        bool covered = actual.All(p => expected.TryGetValue(p.Key, out int n) && n >= p.Value);
                        if (!covered || (!definition.IsItemized && (expected.Count != actual.Count || expected.Any(p => actual[p.Key] != p.Value))))
                            throw new InvalidOperationException(f.GetName() + ": 표시값/요소 분포 불일치");
                        columns.Add(id, values);
                        data.Diagnostics.Add(f.GetName() + " · " + f.FieldType + " · 식별 사용 · 값 종류 표/후보 " + actual.Count + "/" + expected.Count +
                            (LinkField(f) ? "" : " · 매개변수 없음/연결 불가 " + candidates.Count(c => Resolve(host, f, c) == null)));
                    }
                    catch (Exception ex) when (definition.IsItemized) { identityFields.Remove(f); data.Diagnostics.Add(f.GetName() + " · 식별 제외: " + ex.GetBaseException().Message); }
                }
                var ids = identityFields.Select(f => f.FieldId.ToString()).ToList();
                foreach (var helper in linkColumns.Where(p => !ids.Contains(p.Key)))
                {
                    var values = candidates.Select(c => LinkValue(c, helper.Value)).ToList();
                    var expected = values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
                    var actual = cells.Select((r, i) => new { value = r[helper.Key], count = counts[i] }).GroupBy(p => p.value).ToDictionary(g => g.Key, g => g.Sum(p => p.count));
                    if (actual.Any(p => !expected.TryGetValue(p.Key, out int n) || n < p.Value))
                    {
                        data.Diagnostics.Add("링크 " + (helper.Value ? "배치" : "파일") + " 보조 열 · 표시값/후보 분포 불일치");
                        data.Diagnostics.Add("  표: " + string.Join(" / ", actual.Take(20).Select(p => "[" + p.Key + "] " + p.Value)));
                        data.Diagnostics.Add("  후보: " + string.Join(" / ", expected.Take(20).Select(p => "[" + p.Key + "] " + p.Value)));
                        continue;
                    }
                    ids.Add(helper.Key); columns.Add(helper.Key, values);
                    data.Diagnostics.Add("링크 " + (helper.Value ? "배치" : "파일") + " 보조 열 · 식별 사용");
                }
                if (definition.IsItemized && ids.Count == 0) throw new InvalidOperationException("표시값이 정확히 대응하는 식별 필드가 없습니다. ‘행 연결 진단’에서 후보 수와 제외된 필드를 확인하세요.");
                var candidateKeys = candidates.Select((c, i) => ScheduleEditorLogic.Key(ids.Select(id => columns[id][i]))).ToList();
                var rawKeys = candidates.Select(c => ScheduleEditorLogic.Key(identityFields.Select(f => LinkField(f) ? Display(host, f, c) :
                    Resolve(host, f, c) is { } p ? ScheduleEditorParameters.RawKey(p.Parameter, c.Source.Key) : "missing"))).ToList();
                var displayedKeys = cells.Select(row => ScheduleEditorLogic.Key(ids.Select(id => row[id]))).ToList();
                matches = ScheduleEditorLogic.MatchRows(displayedKeys, candidateKeys, rawKeys, counts, definition.IsItemized);
            }
            catch (Exception ex) { mappingError = "표시만 가능: " + ex.GetBaseException().Message; }
            var candidateErrors = new Dictionary<Candidate, string>();
            var dependencyFields = identityFields.Concat(definition.GetSortGroupFields().Select(f => definition.GetField(f.FieldId)))
                .Concat(definition.GetFilters().Select(f => definition.GetField(f.FieldId))).GroupBy(f => f.FieldId.ToString()).Select(g => g.First()).ToList();
            foreach (var member in matches.SelectMany(m => m.Members).Distinct().Select(index => candidates[index]))
            {
                var guard = new ScheduleEditorGuard { SourceKey = member.Source.Key, ElementId = member.Element.UniqueId,
                    TypeId = member.Document.GetElement(member.Element.GetTypeId())?.UniqueId ?? "" };
                foreach (var f in dependencyFields)
                {
                    if (LinkField(f)) continue; // 링크 정보는 최종 재조회 fingerprint로 검증한다.
                    var resolved = Resolve(host, f, member);
                    if (resolved == null)
                    {
                        var key = ParameterIdentity(host, f, member.Source.IsHost);
                        if (key == null) candidateErrors[member] = "필터/정렬 조건 재검증 불가: " + f.GetName();
                        else guard.Missing.Add(key);
                        continue;
                    }
                    guard.Values.Add(ProjectInformationService.Snapshot(member.Document, resolved.Value.Owner, resolved.Value.Parameter, member.Source));
                }
                data.Guards.Add(guard);
            }
            var parameterCache = new Dictionary<string, UnifiedRow>();
            var referenceCatalogs = new Dictionary<string, Dictionary<string, string>>();
            var opened = sources.Where(s => !s.IsHost && s.Editable).ToDictionary(s => s.Key, s => LinkedModelEditService.FindOpenTarget(app, s.Path));
            var groupRows = new Dictionary<List<int>, ScheduleEditorRow>();
            for (int i = 0; i < cells.Count; i++)
            {
                string reason = mappingError.Length > 0 ? mappingError : matches[i].Reason;
                if (reason.Length == 0) reason = matches[i].Members.Select(index => candidates[index]).Where(candidateErrors.ContainsKey).Select(c => candidateErrors[c]).FirstOrDefault() ?? "";
                // 동일 행 수 × 공동 대상 수만큼 Revit API를 반복 호출하지 않는다. 공동 집합은 한 번만 읽는다.
                if (reason.Length == 0 && groupRows.TryGetValue(matches[i].Members, out var cachedRow))
                {
                    var duplicate = new ScheduleEditorRow { Number = i + 1, Models = cachedRow.Models, Identity = cachedRow.Identity,
                        ElementCount = cachedRow.ElementCount, RequiredRows = cachedRow.RequiredRows };
                    foreach (var pair in cells[i]) duplicate.Cells[pair.Key] = pair.Value;
                    foreach (var pair in cachedRow.Targets) duplicate.Targets[pair.Key] = pair.Value;
                    foreach (var pair in cachedRow.Errors) duplicate.Errors[pair.Key] = pair.Value;
                    foreach (var pair in cachedRow.ParameterMappings) duplicate.ParameterMappings[pair.Key] = pair.Value;
                    data.Rows.Add(duplicate); continue;
                }
                var members = reason.Length == 0 ? matches[i].Members.Select(index => candidates[index]).ToList() : new List<Candidate>();
                var row = new ScheduleEditorRow { Number = i + 1, Reason = reason, ElementCount = members.Count == 0 ? counts[i] : members.Select(m => m.Source.Key + "|" + m.Element.UniqueId).Distinct().Count(),
                    Models = members.Count == 0 ? "소속 확인 불가" : string.Join(", ", members.Select(m => m.Source.Name).Distinct()),
                    Identity = ScheduleEditorLogic.Key(members.Select(m => m.Source.Key + "|" + m.Placement + "|" + m.Element.UniqueId).OrderBy(s => s)) };
                row.RequiredRows = matches[i].RequiredRows;
                foreach (var pair in cells[i]) row.Cells[pair.Key] = pair.Value;
                foreach (var f in shownFields)
                {
                    string id = f.FieldId.ToString(); var targets = new List<UnifiedRow>();
                    foreach (var member in members)
                    {
                        var resolved = Resolve(host, f, member);
                        if (resolved == null) { row.Errors[id] = "동일 매개변수를 확인할 수 없습니다. 공유 GUID 또는 유일한 프로젝트 매개변수 이름·자료형을 확인하세요."; break; }
                        var snapshot = ProjectInformationService.Snapshot(member.Document, resolved.Value.Owner, resolved.Value.Parameter, member.Source);
                        if (opened.TryGetValue(member.Source.Key, out var original) && original != null)
                        {
                            var element = original.GetElement(member.Element.UniqueId);
                            var live = element == null ? null : Resolve(host, f, new Candidate { Document = original, Element = element, Source = member.Source });
                            if (live == null) { row.Errors[id] = "열린 원본에서 동일 요소/매개변수를 찾을 수 없습니다. 다시 조회하세요."; break; }
                            var current = ProjectInformationService.Snapshot(original, live.Value.Owner, live.Value.Parameter, member.Source);
                            if (current.Key != snapshot.Key || current.ProjectValue!.Raw != snapshot.ProjectValue!.Raw || current.ProjectValue.DataType != snapshot.ProjectValue.DataType)
                            { row.Errors[id] = "링크 표시와 열린 원본의 값이 다릅니다. 원본 동기화와 링크 갱신 후 다시 조회하세요."; break; }
                            snapshot = current;
                            ScheduleEditorParameters.ConfigureReference(original, live.Value.Parameter, snapshot, referenceCatalogs);
                        }
                        else ScheduleEditorParameters.ConfigureReference(member.Document, resolved.Value.Parameter, snapshot, referenceCatalogs);
                        if (!member.Source.IsHost && !resolved.Value.Parameter.IsShared && long.TryParse(f.ParameterId.ToString(), out long parameterId) && parameterId > 0)
                        {
                            string mapping = member.Source.Name + " (" + member.Source.Key + ") · " + member.Source.Path + " · " + snapshot.Family + " · " + snapshot.ProjectValue!.DataType +
                                " · " + (resolved.Value.Owner is ElementType ? "유형" : "인스턴스") + " · " + snapshot.ProjectValue.Identity;
                            if (!row.ParameterMappings.TryGetValue(id, out var mappings)) row.ParameterMappings[id] = mappings = new List<string>();
                            if (!mappings.Contains(mapping)) mappings.Add(mapping);
                        }
                        if (!parameterCache.TryGetValue(snapshot.Key, out var target)) { target = snapshot; parameterCache.Add(target.Key, target); }
                        if (resolved.Value.Owner is ElementType) data.Fields.First(df => df.Id == id).IsType = true;
                        targets.Add(target);
                    }
                    row.Targets[id] = targets.Distinct().ToList();
                }
                data.Rows.Add(row);
                if (reason.Length == 0) groupRows[matches[i].Members] = row;
            }
            ScheduleEditorParameters.ConfigureEditors(data);
            foreach (var group in data.Rows.GroupBy(r => r.Reason.Length == 0 ? "요소 연결 성공" : r.Reason))
                data.Diagnostics.Add(group.Key + " · " + group.Count() + "행");
            foreach (var group in data.Rows.SelectMany(r => r.Errors.Values).GroupBy(s => s)) data.Diagnostics.Add("셀 제한: " + group.Key + " · " + group.Count() + "건");
            return data;
        }
        internal static void ValidateGuards(Document doc, ScheduleEditorSnapshot data, string sourceKey)
        {
            foreach (var guard in data.Guards.Where(g => g.SourceKey == sourceKey))
            {
                Element element = doc.GetElement(guard.ElementId) ?? throw new InvalidOperationException("조회 이후 일람표 요소가 삭제되었습니다.");
                if ((doc.GetElement(element.GetTypeId())?.UniqueId ?? "") != guard.TypeId) throw new InvalidOperationException("조회 이후 일람표 요소의 유형이 변경되었습니다.");
                foreach (var missing in guard.Missing)
                    if (Resolve(doc, element, missing) != null) throw new InvalidOperationException("조회 당시 없거나 연결할 수 없던 매개변수가 변경되었습니다. 일람표를 다시 조회하세요.");
                foreach (var value in guard.Values) ProjectInformationService.Validate(doc, value, false);
            }
        }
    }
}
