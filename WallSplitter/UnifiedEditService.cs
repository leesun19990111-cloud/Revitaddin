using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    internal static class UnifiedEditService
    {
        internal static bool HasLinks(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Any();

        internal static Result Execute(UIApplication app, bool materials, Func<Result> currentOnly)
        {
            string title = materials ? "재료 지정 — 통합 모델" : "NAMER — 통합 모델";
            bool retainChanges = false;
            try
            {
                if (NamerWindow.Instance != null)
                {
                    TaskDialog.Show(title, "열려 있는 NAMER의 작업을 완료하거나 닫은 후 통합 편집을 실행해 주세요.");
                    return Result.Cancelled;
                }
                Document host = app.ActiveUIDocument.Document;
                var sources = Collect(host, materials, app);
                var window = new UnifiedEditWindow(sources, materials);
                new WindowInteropHelper(window) { Owner = app.MainWindowHandle };
                bool? accepted = window.ShowDialog();
                if (window.CurrentOnly) return currentOnly();
                if (accepted != true) return Result.Cancelled;
                var batches = sources.Select(s => (Source: s, Rows: s.Rows.Where(r => r.Changed).ToList()))
                    .Where(b => b.Rows.Count > 0).OrderBy(b => b.Source.IsHost ? 1 : 0).ToList();
                if (batches.Count == 0) return Result.Cancelled;
                Preflight(app, batches.Select(b => b.Source));
                string summary = string.Join("\n\n", batches.Select(b =>
                    b.Source.Name + " · " + b.Rows.Count + "개 변경" +
                    (materials ? " · 인스턴스 " + b.Rows.Where(r => r.IsInstance).SelectMany(r => r.OriginalMaterials.Keys).Distinct().Count() + "개" : "") +
                    "\n" + b.Source.Path +
                    (b.Source.OpenDocumentPath.Length > 0 ? "\n열린 문서 재사용: " + b.Source.OpenDocumentPath : "")));
                if (!LinkedModelEditService.Confirm(title, "모델별 변경 내용을 확인하고 일괄 적용할까요?",
                    summary + "\n\n링크는 원본 중앙모델에 동기화되어 다른 사용자·프로젝트에도 반영됩니다." +
                    "\n현재 모델은 마지막에 반영하며 자동 저장·중앙 동기화하지 않습니다." +
                    "\n이미 열린 링크 원본은 해당 문서를 재사용합니다. 그 문서의 기존 작업도 함께 동기화되므로 실행 직전에 별도로 확인합니다." +
                    "\n링크 원본의 로컬은 같은 Revit에 미리 열어 두어야 합니다. 링크 해제·자동 재열기·재로드는 하지 않습니다." +
                    "\n항목 적용 실패는 이번 모델의 변경을 취소합니다. 동기화 실패 시에는 변경을 보존하고 나머지 모델을 중단합니다. 이미 동기화된 모델은 자동으로 되돌릴 수 없습니다." +
                    "\n서버 동기화 실패는 중앙 반영 여부가 불확실할 수 있으므로 자동 재시도하지 않습니다." +
                    (!materials && window.MergeMaterials ? "\n같은 이름의 재료는 모델 안에서 병합합니다." : "")))
                    return Result.Cancelled;
                var report = new List<string>();
                bool stopped = false;
                foreach (var batch in batches)
                {
                    if (stopped) { report.Add(batch.Source.Name + " — 미처리 (앞선 모델 실패)"); continue; }
                    try
                    {
                        if (!batch.Source.Editable) throw new InvalidOperationException(batch.Source.BlockReason);
                        var plan = new LinkedEditPlan
                        {
                            Summary = batch.Source.Name + " · " + batch.Rows.Count + "개",
                            Apply = (doc, logs) => Apply(doc, batch.Rows, materials, window.MergeMaterials, logs)
                        };
                        if (batch.Source.IsHost)
                        {
                            using var group = new TransactionGroup(host, title);
                            if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("변경 시작 실패");
                            var logs = new List<ChangeLogEntry>();
                            if (plan.Apply(host, logs) != Result.Succeeded)
                                throw new InvalidOperationException("현재 모델 변경 실패 — 이번 모델 전체 취소");
                            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("현재 모델 확정 실패");
                            retainChanges = true;
                            ChangeLog.Append(logs);
                            report.Add(batch.Source.Name + " — 반영 완료 (저장/중앙 동기화는 직접 실행)");
                        }
                        else
                        {
                            var link = host.GetElement(batch.Source.LinkUniqueId) as RevitLinkType
                                ?? throw new InvalidOperationException("링크가 삭제되었습니다.");
                            string livePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(link.GetExternalFileReference().GetAbsolutePath());
                            if (!LinkedModelEditService.SamePath(livePath, batch.Source.Path))
                                throw new InvalidOperationException("링크 원본 경로가 변경되었습니다. 다시 조회하세요.");
                            var outcome = new LinkedEditReport();
                            LinkedModelEditService.Run(app, link, title, _ => plan, outcome);
                            retainChanges |= outcome.RetainChanges;
                            report.Add(batch.Source.Name + " — " + string.Join("\n", outcome.Messages));
                            stopped = !outcome.Synced || outcome.HasProblems;
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Add(batch.Source.Name + " — 실패: " + ex.GetBaseException().Message);
                        stopped = true;
                    }
                }
                TaskDialog.Show(title + " — 처리 결과", string.Join("\n\n", report));
                // 실패/미처리는 보고서로 알린다. 이미 동기화했거나 복구용으로 남긴 변경은 Revit이 취소하지 않게 한다.
                return stopped && !retainChanges ? Result.Cancelled : Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(title, "통합 편집을 완료하지 못했습니다.\n" + ex.GetBaseException().Message);
                return retainChanges ? Result.Succeeded : Result.Cancelled;
            }
        }

        // 변경할 모든 모델의 준비 상태를 먼저 확인한다. 앞 모델만 바뀌는 사전조건 실패를 방지한다.
        internal static void Preflight(UIApplication app, IEnumerable<UnifiedSource> sources)
        {
            Document host = app.ActiveUIDocument.Document;
            if (!host.IsValidObject || host.IsReadOnly || host.IsModifiable || host.IsLinked || host.IsFamilyDocument)
                throw new InvalidOperationException("현재 프로젝트를 편집할 수 없습니다. 이번 작업은 아무 모델에도 반영하지 않았습니다.");
            var errors = new List<string>();
            foreach (UnifiedSource source in sources.GroupBy(s => s.Key).Select(g => g.First()))
            {
                try
                {
                    if (!source.Editable) throw new InvalidOperationException(source.BlockReason);
                    if (source.IsHost) continue;
                    var link = host.GetElement(source.LinkUniqueId) as RevitLinkType
                        ?? throw new InvalidOperationException("링크가 삭제되었습니다. 다시 조회해 주세요.");
                    LinkedModelEditService.RequireOpenTarget(app, link, source.Path);
                }
                catch (Exception ex) { errors.Add(source.Name + " — " + ex.GetBaseException().Message); }
            }
            if (errors.Count > 0)
                throw new InvalidOperationException("사전 검사에서 중단했습니다. 현재 모델을 포함해 아무 모델에도 이번 변경을 적용하지 않았습니다.\n\n" +
                    string.Join("\n\n", errors));
        }

        internal static List<UnifiedSource> Collect(Document host, bool materials, UIApplication? app = null, bool readRows = true)
        {
            var sources = new List<UnifiedSource>();
            var current = new UnifiedSource { Key = "host", Name = "[현재] " + host.Title, IsHost = true, Path = host.PathName };
            if (host.IsReadOnly || host.IsModifiable) current.BlockReason = "읽기 전용 또는 편집 모드";
            sources.Add(current);
            if (readRows) ReadRows(current, host, materials);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RevitLinkType link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>().OrderBy(l => l.IsNestedLink))
            {
                var source = new UnifiedSource { Key = link.UniqueId, LinkUniqueId = link.UniqueId, Name = "[링크] " + link.Name };
                try
                {
                    ModelPath path = link.GetExternalFileReference().GetAbsolutePath();
                    source.Path = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
                    string identity = LinkedModelEditService.IsFileBasedPath(path) ? LinkedModelEditService.CanonicalPath(source.Path) : source.Path;
                    if (!seen.Add(identity)) continue;
                    if (link.IsNestedLink) source.BlockReason = "중첩 링크: 직접 편집 미지원";
                    else if (!LinkedModelEditService.IsFileBasedPath(path)) source.BlockReason = "비어 있는 경로 또는 클라우드/Revit Server 편집 미지원";
                    else
                    {
                        using var info = BasicFileInfo.Extract(source.Path);
                        if (!info.IsCentral || !info.IsWorkshared) source.BlockReason = "파일 기반 중앙모델만 편집 가능";
                        else if (!info.IsSavedInCurrentVersion) source.BlockReason = "Revit 버전 불일치";
                    }
                    var instance = new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>().FirstOrDefault(i => i.GetTypeId() == link.Id);
                    Document? doc = instance?.GetLinkDocument();
                    if (doc == null) source.BlockReason = "미로드 링크: 요소를 조회할 수 없음";
                    else
                    {
                        // 링크에 남아 있는 옛 이름 대신 이미 열린 원본의 현재 작업값을 표시한다.
                        if (source.Editable && app != null)
                        {
                            Document? open = LinkedModelEditService.FindOpenTarget(app, source.Path);
                            if (open != null) { doc = open; source.OpenDocumentPath = open.PathName; }
                        }
                        if (readRows) ReadRows(source, doc, materials);
                    }
                }
                catch (Exception ex) { source.BlockReason = "조회/편집 불가: " + ex.GetBaseException().Message; }
                sources.Add(source);
            }
            return sources;
        }

        private static void ReadRows(UnifiedSource source, Document doc, bool materials)
        {
            if (!materials)
            {
                source.Rows.AddRange(ProjectInformationService.Read(doc, source));
                var indexed = new Dictionary<string, UnifiedRow>();
                foreach (NamerWindow.NamerCategory category in Enum.GetValues(typeof(NamerWindow.NamerCategory)))
                    foreach (Element element in NamerWindow.CollectCandidates(doc, category))
                    {
                        if (indexed.ContainsKey(element.UniqueId)) continue;
                        var row = new UnifiedRow { Source = source, UniqueId = element.UniqueId, Original = element.Name,
                            Working = element.Name, Category = (int)category, IsFamily = element is Family,
                            Family = (element as ElementType)?.FamilyName ?? "" };
                        indexed.Add(row.UniqueId, row); source.Rows.Add(row);
                    }
                foreach (Family family in NamerWindow.CollectCandidates(doc, NamerWindow.NamerCategory.Family))
                    foreach (ElementId id in family.GetFamilySymbolIds())
                    {
                        Element? child = doc.GetElement(id);
                        if (child != null && indexed.TryGetValue(child.UniqueId, out var row)) row.ParentId = family.UniqueId;
                    }
                return;
            }
            foreach (Material material in new FilteredElementCollector(doc).OfClass(typeof(Material)))
                source.Materials[material.UniqueId] = material.Name;
            var types = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>().ToDictionary(t => t.Id);
            var groups = new Dictionary<string, UnifiedRow>();
            void Add(Element element, ElementType type, MaterialSlot slot)
            {
                var row = new UnifiedRow { Source = source, UniqueId = type.UniqueId, Family = type.FamilyName,
                    IsMaterial = true, IsInstance = slot.Kind == MaterialSlotKind.InstanceParameter,
                    Working = type.Name, SlotKind = (int)slot.Kind, LayerIndex = slot.LayerIndex,
                    ParameterId = slot.ParameterId.ToString(), SlotLabel = slot.Label };
                if (!groups.TryGetValue(row.Key, out var group)) { groups.Add(row.Key, row); source.Rows.Add(row); group = row; }
                string materialId = (doc.GetElement(slot.MaterialId) as Material)?.UniqueId ?? "";
                group.OriginalMaterials[element.UniqueId] = materialId;
                string name = source.Materials.TryGetValue(materialId, out string? value) ? value : "(재료 없음)";
                if (!group.MaterialNames.Contains(name)) group.MaterialNames.Add(name);
            }
            foreach (var type in types.Values)
                foreach (var slot in MaterialSlotFinder.FindAll(type)) Add(type, type, slot);
            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                if (types.TryGetValue(element.GetTypeId(), out var type))
                    foreach (var slot in MaterialSlotFinder.FindAll(element)) Add(element, type, slot);
            foreach (var row in source.Rows)
            {
                row.Original = row.MaterialNames.Count == 1 ? row.MaterialNames[0] : "<다양함>";
                row.Detail = (row.IsInstance ? "인스턴스 " + row.OriginalMaterials.Count + "개 전체" : "유형") + " · " + row.SlotLabel;
            }
        }

        internal static Result Apply(Document doc, List<UnifiedRow> rows, bool materials, bool merge, List<ChangeLogEntry> logs)
        {
            // 링크 로드 시점 이후 중앙에서 바뀐 항목을 덮어쓰지 않는다. 쓰기 전에 전부 다시 확인한다.
            foreach (var row in rows)
            {
                if (row.ProjectValue != null) { ProjectInformationService.Validate(doc, row); continue; }
                Element element = doc.GetElement(row.UniqueId) ?? throw new InvalidOperationException("요소가 삭제됨: " + row.Original);
                if (element.Name != (materials ? row.Working : row.Original))
                    throw new InvalidOperationException("조회 후 이름이 변경됨: " + row.Original + " — 링크를 갱신하고 다시 조회하세요.");
            }
            if (!materials)
            {
                ProjectInformationService.Apply(doc, rows.Where(r => r.ProjectValue != null).ToList(), logs);
                var names = rows.Where(r => r.ProjectValue == null).ToList();
                if (names.Count == 0) return Result.Succeeded;
                var result = NamerCommand.ApplyRenames(doc, names.Select(r => (doc.GetElement(r.UniqueId).Id, r.Working)).ToList(), merge, logs);
                return result.Status == TransactionStatus.Committed && result.Failed.Count == 0 ? Result.Succeeded : Result.Failed;
            }
            var assignments = new List<(ElementId TypeId, MaterialSlot Slot, ElementId NewMaterialId)>();
            var instances = new List<MaterialInstanceAssignment>();
            foreach (var row in rows)
            {
                Element type = doc.GetElement(row.UniqueId);
                Material target = doc.GetElement(row.NewMaterial) as Material ?? throw new InvalidOperationException("지정할 재료가 삭제되었습니다.");
                if (target.Name != row.Source.Materials[row.NewMaterial]) throw new InvalidOperationException("지정할 재료 이름이 변경되었습니다.");
                MaterialSlot? identity = null;
                var ids = new List<ElementId>();
                foreach (var original in row.OriginalMaterials)
                {
                    Element element = doc.GetElement(original.Key) ?? throw new InvalidOperationException("재료 대상 요소가 삭제되었습니다.");
                    if (row.IsInstance && element.GetTypeId() != type.Id) throw new InvalidOperationException("인스턴스의 유형이 변경되었습니다.");
                    var matches = MaterialSlotFinder.FindAll(element).Where(s => (int)s.Kind == row.SlotKind &&
                        (s.Kind == MaterialSlotKind.CompoundLayer ? s.LayerIndex == row.LayerIndex : s.ParameterId.ToString() == row.ParameterId)).ToList();
                    if (matches.Count != 1 || matches[0].Label != row.SlotLabel) throw new InvalidOperationException("재료 슬롯 구성이 변경되었습니다.");
                    var slot = matches[0];
                    string liveMaterial = (doc.GetElement(slot.MaterialId) as Material)?.UniqueId ?? "";
                    if (liveMaterial != original.Value) throw new InvalidOperationException("조회 이후 재료가 변경되었습니다. 다시 조회하세요.");
                    identity = slot; ids.Add(element.Id);
                }
                if (identity == null) throw new InvalidOperationException("빈 재료 대상입니다.");
                if (row.IsInstance)
                {
                    // 새 인스턴스까지 임의 확장하거나 일부를 빠뜨리지 않는다.
                    var liveIds = new HashSet<string>(new FilteredElementCollector(doc).WhereElementIsNotElementType()
                        .Where(e => e.GetTypeId() == type.Id && MaterialSlotFinder.FindSlot(e, identity.Value) != null).Select(e => e.UniqueId));
                    if (!liveIds.SetEquals(row.OriginalMaterials.Keys)) throw new InvalidOperationException("유형의 인스턴스 범위가 변경되었습니다. 다시 조회하세요.");
                    instances.Add(new MaterialInstanceAssignment(type.Id, identity.Value, ids, target.Id));
                }
                else assignments.Add((type.Id, identity.Value, target.Id));
            }
            return MaterialAssignCommand.ExecuteAssign(doc, assignments, instances, logs);
        }
    }
}
