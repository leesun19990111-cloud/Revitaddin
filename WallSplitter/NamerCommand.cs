using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public class NamerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            List<ElementId> preSelected = uiDoc.Selection.GetElementIds().ToList();

            NamerWindow window = new NamerWindow(doc, preSelected);
            new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
            bool? dialogResult = window.ShowDialog();
            if (dialogResult != true || window.Result == null || window.Result.Count == 0)
                return Result.Cancelled;

            var failed = new List<string>();
            var merged = new List<string>();
            var pendingLogEntries = new List<ChangeLogEntry>();
            TransactionStatus status;

            using (Transaction tx = new Transaction(doc, "NAMER 이름 일괄 변경"))
            {
                tx.Start();

                foreach ((ElementId id, string newName) in window.Result)
                {
                    Element? el = doc.GetElement(id);
                    if (el == null) continue;
                    string oldName = el.Name ?? "";

                    // 재료는 이름이 겹치면 그냥 실패시키지 않고, 사용자가 미리 고른 정책(병합/숫자 접미사)대로
                    // 처리한다 - 다른 카테고리는 지금까지와 동일하게 이름 변경을 그대로 시도한다.
                    if (el is Material material)
                    {
                        int failedBefore = failed.Count;
                        RenameMaterial(doc, material, oldName, newName, window.MergeDuplicateMaterials, failed, merged);
                        // RenameMaterial의 시그니처를 그대로 두기 위해(성공 여부를 별도로 안 돌려줌), 이 호출로
                        // failed에 새로 추가된 게 없으면 성공한 것으로 판단한다.
                        if (failed.Count == failedBefore)
                            pendingLogEntries.Add(new ChangeLogEntry
                            {
                                Timestamp = DateTime.Now,
                                SourceDocumentTitle = doc.Title,
                                Kind = ChangeKind.Rename,
                                Category = NamerWindow.NamerCategory.Material,
                                Key = oldName,
                                NewValue = newName,
                                MergeDuplicateMaterials = window.MergeDuplicateMaterials,
                            });
                        continue;
                    }

                    try
                    {
                        el.Name = newName;
                        pendingLogEntries.Add(new ChangeLogEntry
                        {
                            Timestamp = DateTime.Now,
                            SourceDocumentTitle = doc.Title,
                            Kind = ChangeKind.Rename,
                            Category = ClassifyForLog(el),
                            Key = oldName,
                            NewValue = newName,
                        });
                    }
                    catch (System.Exception ex)
                    {
                        failed.Add($"{oldName} → {newName} ({ex.Message})");
                    }
                }

                // Commit()은 실패 시 예외 없이 TransactionStatus.RolledBack을 조용히 반환하고 이번 트랜잭션의
                // 모든 변경(이름 변경 포함)을 되돌린다 — 이 반환값을 확인하지 않으면 위 try/catch가 전부
                // 통과했다는 이유만으로 "성공"으로 오판해, 진행 게이지는 돌지만 실제로는 아무것도 반영되지
                // 않는 현상이 사용자에게 아무 설명 없이 나타난다.
                status = tx.Commit();
            }

            if (status != TransactionStatus.Committed)
            {
                TaskDialog.Show("NAMER", $"이름 변경이 모델에 반영되지 않았습니다 (트랜잭션 롤백: {status}).");
                return Result.Failed;
            }

            // 커밋이 실제로 성공한 뒤에만 기록한다 - 롤백되면 pendingLogEntries 전부가 "실제로는 일어나지
            // 않은 변경"이 되므로, 모델간 변경 반영이 나중에 이걸 다른 문서에 재현하려 들면 안 된다.
            if (pendingLogEntries.Count > 0) ChangeLog.Append(pendingLogEntries);

            if (merged.Count > 0)
            {
                string detail = string.Join("\n", merged.Take(30));
                if (merged.Count > 30) detail += $"\n... 외 {merged.Count - 30}개";
                TaskDialog.Show("NAMER", $"{merged.Count}개 재료를 기존 재료로 병합했습니다:\n" + detail);
            }

            if (failed.Count > 0)
            {
                string detail = string.Join("\n", failed.Take(30));
                if (failed.Count > 30) detail += $"\n... 외 {failed.Count - 30}개";
                TaskDialog.Show("NAMER", $"{failed.Count}개 항목의 이름을 바꾸지 못했습니다 (이름 중복 등):\n" + detail);
            }

            return Result.Succeeded;
        }

        // window.Result((ElementId, NewName)만 담음)는 카테고리 정보를 따로 들고 있지 않으므로, 커밋된 뒤
        // 기록을 남길 때 요소의 실제 런타임 타입으로부터 다시 분류한다. NamerWindow.CollectCandidates의
        // 카테고리별 분류 규칙과 같은 우선순위(시트/일람표/뷰 템플릿/범례를 뷰보다 먼저 확인)를 따른다.
        private static NamerWindow.NamerCategory ClassifyForLog(Element el) => el switch
        {
            ViewSheet => NamerWindow.NamerCategory.Sheet,
            ViewSchedule => NamerWindow.NamerCategory.Schedule,
            View v when v.IsTemplate => NamerWindow.NamerCategory.ViewTemplate,
            View v when v.ViewType == ViewType.Legend => NamerWindow.NamerCategory.Legend,
            View => NamerWindow.NamerCategory.View,
            Family => NamerWindow.NamerCategory.Family,
            ElementType => NamerWindow.NamerCategory.Type,
            _ => NamerWindow.NamerCategory.Type,
        };

        // 재료 하나를 이름 변경한다 - 새 이름을 이미 다른 재료가 쓰고 있으면(재료는 문서 전체에서 이름이
        // 유일해야 함) mergeOnDuplicate에 따라 병합하거나 숫자를 붙인다.
        // internal: ChangeReplayEngine이 다른 문서에 Rename+Category=Material 기록을 재현할 때도 이
        // 병합/충돌 처리 로직을 그대로 써야 하므로 (중복되면 언젠가 어긋나는 로직이라 공유가 맞다).
        internal static void RenameMaterial(Document doc, Material material, string oldName, string newName,
            bool mergeOnDuplicate, List<string> failed, List<string> merged)
        {
            Material? existing = new FilteredElementCollector(doc).OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => m.Id != material.Id && m.Name == newName);

            if (existing == null)
            {
                try
                {
                    material.Name = newName;
                }
                catch (System.Exception ex)
                {
                    failed.Add($"{oldName} → {newName} ({ex.Message})");
                }
                return;
            }

            if (mergeOnDuplicate)
            {
                try
                {
                    int redirected = RedirectMaterialUsages(doc, material.Id, existing.Id);
                    doc.Delete(material.Id);

                    // CompoundStructure/Parameter 변경과 삭제를 연달아 호출한 직후 doc.GetElement로 바로
                    // 확인했더니, 이미 반영된 게 확실한 경우에도 여전히 존재하는 것처럼 보이는 라이브 버그가
                    // 남아있었다 - Revit이 이 변경들을 실제로 문서에 완전히 반영(재생성)하기 전에 조회한 것이
                    // 원인으로 보인다. Regenerate()로 강제로 반영을 끝낸 뒤에 확인해야 신뢰할 수 있다.
                    doc.Regenerate();

                    if (doc.GetElement(material.Id) != null)
                        failed.Add($"{oldName} → {existing.Name} 병합 (사용처는 옮겼지만 원래 재료 삭제는 확인되지 않음 - 개체 스타일/채우기 패턴 등 이 도구가 옮기지 않는 곳에서 아직 쓰이고 있을 수 있음)");
                    else
                        merged.Add($"{oldName} → {existing.Name} (사용 중이던 유형/파라미터 {redirected}곳 이동)");
                }
                catch (System.Exception ex)
                {
                    failed.Add($"{oldName} → {existing.Name} 병합 실패 ({ex.Message})");
                }
            }
            else
            {
                string uniqueName = FindUniqueMaterialName(doc, newName);
                try
                {
                    material.Name = uniqueName;
                }
                catch (System.Exception ex)
                {
                    failed.Add($"{oldName} → {uniqueName} ({ex.Message})");
                }
            }
        }

        // oldMaterialId를 참조하는 모든 유형(복합구조 레이어 + 재료 스펙 파라미터)을 newMaterialId로 옮긴다.
        // MaterialSlotFinder는 유형 하나당 "대표 재료 슬롯 하나"만 찾지만(재료 지정 도구용), 병합은 그 유형이
        // 가진 모든 레이어/파라미터에서 이 재료를 쓰는 자리를 빠짐없이 찾아 옮겨야 하므로 별도로 전체 스캔한다.
        // 유형(ElementType) 수준만 다루며, 부재(인스턴스) 단위의 재료 재정의는 범위 밖이다.
        private static int RedirectMaterialUsages(Document doc, ElementId oldMaterialId, ElementId newMaterialId)
        {
            int count = 0;
            var allTypes = new FilteredElementCollector(doc).WhereElementIsElementType().ToElements();

            foreach (Element el in allTypes)
            {
                if (el is not ElementType type) continue;

                if (type is HostObjAttributes hostAttrs)
                {
                    CompoundStructure? structure = hostAttrs.GetCompoundStructure();
                    if (structure != null)
                    {
                        IList<CompoundStructureLayer> layers = structure.GetLayers();
                        var newLayers = new List<CompoundStructureLayer>(layers.Count);
                        bool changed = false;
                        foreach (CompoundStructureLayer layer in layers)
                        {
                            if (layer.MaterialId == oldMaterialId)
                            {
                                newLayers.Add(new CompoundStructureLayer(layer.Width, layer.Function, newMaterialId));
                                changed = true;
                            }
                            else
                            {
                                newLayers.Add(layer);
                            }
                        }

                        if (changed)
                        {
                            structure.SetLayers(newLayers);
                            hostAttrs.SetCompoundStructure(structure);
                            count++;
                        }
                        continue;
                    }
                }

                foreach (Parameter p in type.Parameters)
                {
                    if (p.StorageType != StorageType.ElementId) continue;
                    if (p.AsElementId() != oldMaterialId) continue;
                    if (p.IsReadOnly) continue;

                    Definition? def = p.Definition;
                    if (def == null) continue;
                    bool isMaterialParam;
                    try { isMaterialParam = def.GetDataType() == SpecTypeId.Reference.Material; }
                    catch { isMaterialParam = false; }
                    if (!isMaterialParam) continue;

                    p.Set(newMaterialId);
                    count++;
                }
            }

            return count;
        }

        // GetOrCreateSingleLayerWallType(Class1.cs/SplitFloorCommand.cs)이 이름 충돌 시 "(2)", "(3)"을
        // 붙이는 것과 같은 발상이나, 사용자가 명시적으로 요청한 형식("이름1")에 맞춰 숫자만 이어붙인다.
        private static string FindUniqueMaterialName(Document doc, string baseName)
        {
            var existingNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(Material))
                    .Cast<Material>()
                    .Select(m => m.Name ?? ""));

            if (!existingNames.Contains(baseName)) return baseName;

            int suffix = 1;
            while (existingNames.Contains(baseName + suffix)) suffix++;
            return baseName + suffix;
        }

        // 여기에 커스텀 IFailuresPreprocessor(SplitWallCommand/NamerCommand가 다른 곳에서 쓰는 것과 같은
        // "경고는 삭제, 해결 가능한 오류는 기본 해결책 적용" 패턴)를 붙이지 않는다 — 라이브 테스트로 확인된
        // 실제 버그: 이 트랜잭션에 커스텀 전처리기를 붙이면, 실패 메시지가 단 하나도 없는(순수 이름 변경뿐인)
        // 상황에서도 Revit이 항상 정확히 666번 PreprocessFailures를 호출하며 재처리 루프를 돌다가 결국
        // TransactionStatus.RolledBack으로 커밋 전체를 되돌렸다 — 체크된 항목이 100개든 2~3개든 호출
        // 횟수가 동일했으므로 이름 변경 자체와는 무관하고, 전처리기를 붙이는 행위 자체가 원인이었다.
        // 전처리기를 완전히 제거하자(Revit 기본 처리에 맡기자) 정상적으로 커밋됐다. 이름 변경만 하는 이
        // 트랜잭션은 지오메트리를 건드리지 않아 애초에 해결해야 할 경고/오류가 나올 일이 드물므로, 무관한
        // 기존 경고 대화상자가 가끔 뜨는 것보다 이 커밋 실패가 훨씬 치명적이다 — 다시 추가하지 말 것.
    }
}
