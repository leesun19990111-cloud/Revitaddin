using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 항목 하나를 대상 문서에서 어떻게 처리하기로 했는지.
    internal enum ResolutionStatus { Auto, Picked, Skipped }

    internal sealed class ResolvedEntry
    {
        public ChangeLogEntry Entry = null!;
        public ResolutionStatus Status;
        // Rename=이름을 바꿀 요소, MaterialAssign=재료를 바꿀 유형, MaterialDelete/MaterialIdentityEdit=대상 재료.
        public ElementId PrimaryTargetId = ElementId.InvalidElementId;
        // MaterialAssign 전용 - 실제로 적용할 "새 재료"의 대상 문서 내 ElementId.
        public ElementId SecondaryTargetId = ElementId.InvalidElementId;
        // MaterialAssign 전용 - PrimaryTargetId(유형) 안에서 이 기록이 가리키던 바로 그 슬롯을 대상 문서 기준
        // 으로 다시 찾은 결과(MaterialSlotFinder.FindSlotByLabel). 유형 이름만으로는 "그 중 어느 슬롯인지"까지
        // 구분되지 않으므로 별도로 들고 있는다.
        public MaterialSlot? TargetSlot;
        public string? SkipReason;
    }

    internal sealed class ReplaySummary
    {
        public string DocumentTitle = "";
        public bool CancelledByUser;
        public bool TransactionCommitted = true;
        public int AppliedCount;
        public List<(ChangeLogEntry Entry, string Reason)> Skipped { get; } = new();
        public List<(ChangeLogEntry Entry, string Reason)> Failed { get; } = new();
    }

    // NAMER/재료 지정에서 자동 기록된 ChangeLogEntry들을 "다른" Document에서 이름으로 같은 사례를 찾아 그대로
    // 재현한다. SplitWallCommand가 "모달 창은 전부 트랜잭션을 열기 전에 띄우고, 트랜잭션 안에서는 순수 반영만
    // 한다"는 원칙을 지키는 것과 같은 이유로, 이 엔진도 (1) 이름 매칭 조회 (2) 모호한 것만 ElementPickerWindow
    // (3) 트랜잭션 하나로 반영 - 세 단계로 나눈다. 커스텀 IFailuresPreprocessor는 NamerCommand/
    // MaterialAssignCommand와 같은 이유로 절대 붙이지 않는다.
    internal static class ChangeReplayEngine
    {
        internal static ReplaySummary RunForDocument(IntPtr revitMainWindowHandle, Document targetDoc, List<ChangeLogEntry> entries)
        {
            // 같은 세션 안에서 "재료 X를 Y로 이름 변경한 뒤 Y의 클래스를 편집"처럼 서로 이어지는 기록이 있을 수
            // 있다 - 선택한 순서가 아니라 실제로 일어난 순서(Timestamp)대로 적용해야, 뒤의 기록이 앞의 기록이
            // 만들어낸 결과(Y)를 대상 문서에서 정확히 찾을 수 있다.
            List<ChangeLogEntry> ordered = entries.OrderBy(e => e.Timestamp).ToList();

            var summary = new ReplaySummary { DocumentTitle = targetDoc.Title };
            var resolved = new List<ResolvedEntry>();

            foreach (ChangeLogEntry entry in ordered)
            {
                bool cancelled = ResolveOne(revitMainWindowHandle, targetDoc, entry, resolved, summary);
                if (cancelled)
                {
                    // 취소는 "이 문서는 통째로 포기" 의미다 - 지금까지 자동/직접선택으로 이미 해결된 항목이
                    // 있어도 Apply를 아예 호출하지 않으므로, 개별 스킵 사유를 남기는 대신 이 문서의 모든 항목을
                    // 통일된 사유로 다시 표시한다(부분 반영처럼 보이는 걸 방지).
                    summary.CancelledByUser = true;
                    summary.TransactionCommitted = false;
                    summary.Skipped.Clear();
                    foreach (ChangeLogEntry e in ordered)
                        summary.Skipped.Add((e, "사용자가 이 문서에 대한 적용을 취소함"));
                    return summary;
                }
            }

            return Apply(targetDoc, resolved, summary);
        }

        // 항목 하나를 이름으로 매칭한다 - 유일하게 매칭되면 자동으로, 0개/여러 개면(재료 삭제 제외) 사용자가
        // 직접 고르게 한다. 반환값 true는 "사용자가 이 문서 전체 적용을 취소함"을 의미한다.
        private static bool ResolveOne(IntPtr ownerHandle, Document targetDoc, ChangeLogEntry entry,
            List<ResolvedEntry> resolved, ReplaySummary summary)
        {
            switch (entry.Kind)
            {
                case ChangeKind.Rename:
                {
                    NamerWindow.NamerCategory category = entry.Category ?? NamerWindow.NamerCategory.Type;
                    List<Element> candidates = NamerWindow.CollectCandidates(targetDoc, category);
                    List<Element> matches = candidates.Where(el => el.Name == entry.Key).ToList();

                    if (!TryResolveSingle(ownerHandle, entry, matches, candidates,
                        DisplayNameSelectorFor(category),
                        $"'{entry.Key}' 이름의 항목을 찾을 수 없거나 여러 개입니다.\n어느 항목의 이름을 '{entry.NewValue}'(으)로 바꿀지 고르세요.",
                        out Element? picked, out bool cancelled, out bool skipped))
                    {
                        if (cancelled) return true;
                        if (skipped) { summary.Skipped.Add((entry, "이름이 일치하는 항목을 찾지 못해 사용자가 건너뜀")); return false; }
                    }

                    resolved.Add(new ResolvedEntry
                    {
                        Entry = entry,
                        Status = matches.Count == 1 ? ResolutionStatus.Auto : ResolutionStatus.Picked,
                        PrimaryTargetId = picked!.Id,
                    });
                    return false;
                }

                case ChangeKind.MaterialAssign:
                {
                    List<ElementType> eligibleTypes = MaterialSlotFinder.FindEligibleTypes(targetDoc);
                    List<ElementType> typeMatches = eligibleTypes.Where(t => t.Name == entry.Key).ToList();

                    if (!TryResolveSingle(ownerHandle, entry, typeMatches.Cast<Element>().ToList(), eligibleTypes.Cast<Element>().ToList(),
                        el => el is ElementType et && !string.IsNullOrEmpty(et.FamilyName) ? $"{et.FamilyName} : {et.Name}" : (el.Name ?? ""),
                        $"유형 이름 '{entry.Key}'을(를) 찾을 수 없거나 여러 개입니다.\n어느 유형의 재료를 바꿀지 고르세요.",
                        out Element? pickedType, out bool typeCancelled, out bool typeSkipped))
                    {
                        if (typeCancelled) return true;
                        if (typeSkipped) { summary.Skipped.Add((entry, "유형 이름이 일치하는 항목을 찾지 못해 사용자가 건너뜀")); return false; }
                    }

                    // 유형이 정해지면, 그 유형 안에서 이 기록이 가리키던 바로 그 슬롯(레이어/파라미터)을 라벨로
                    // 다시 찾는다 - 유형 이름이 같아도 슬롯 구성이 달라졌으면(레이어 수가 바뀜 등) 엉뚱한
                    // 슬롯에 잘못 적용될 수 있으므로, 못 찾으면 재료 삭제와 같은 이유(엉뚱한 대상을 사용자
                    // 대신 고르게 유도하는 picker는 위험)로 그냥 건너뛴다.
                    MaterialSlot? targetSlot = MaterialSlotFinder.FindSlotByLabel((ElementType)pickedType!, entry.SlotLabel ?? "");
                    if (targetSlot == null)
                    {
                        string slotDesc = string.IsNullOrEmpty(entry.SlotLabel) ? "기본 슬롯" : entry.SlotLabel!;
                        summary.Skipped.Add((entry, $"'{pickedType.Name}' 유형에서 대상 슬롯({slotDesc})을 찾지 못함 - 레이어/파라미터 구성이 다를 수 있음"));
                        return false;
                    }

                    List<Element> allMaterials = new FilteredElementCollector(targetDoc).OfClass(typeof(Material)).ToList();
                    List<Element> materialMatches = allMaterials.Where(m => m.Name == entry.NewValue).ToList();

                    if (!TryResolveSingle(ownerHandle, entry, materialMatches, allMaterials,
                        el => el.Name ?? "",
                        $"'{entry.NewValue}' 재료를 대상 문서에서 찾을 수 없거나 여러 개입니다.\n'{pickedType!.Name}' 유형에 지정할 기존 재료를 고르세요 (새로 만들지 않습니다).",
                        out Element? pickedMaterial, out bool matCancelled, out bool matSkipped))
                    {
                        if (matCancelled) return true;
                        if (matSkipped) { summary.Skipped.Add((entry, "지정할 재료를 찾지 못해 사용자가 건너뜀")); return false; }
                    }

                    resolved.Add(new ResolvedEntry
                    {
                        Entry = entry,
                        Status = (typeMatches.Count == 1 && materialMatches.Count == 1) ? ResolutionStatus.Auto : ResolutionStatus.Picked,
                        PrimaryTargetId = pickedType!.Id,
                        SecondaryTargetId = pickedMaterial!.Id,
                        TargetSlot = targetSlot,
                    });
                    return false;
                }

                case ChangeKind.MaterialDelete:
                {
                    List<Material> matches = new FilteredElementCollector(targetDoc).OfClass(typeof(Material))
                        .Cast<Material>().Where(m => m.Name == entry.Key).ToList();

                    // 재료 삭제는 "없으면 고르는" 대신 그냥 건너뛴다 - 대상이 없을 때 사용자에게 "그럼 대신 어떤
                    // 재료를 지울까요"라고 묻는 건, 의도와 무관한 재료를 실수로 삭제하게 만드는 위험한 유도가
                    // 될 수 있다(재료 지정/클래스 편집처럼 "고른 결과를 눈으로 확인하며 승인"하는 것과 달리,
                    // 삭제는 되돌릴 수 없는 데다 후보를 고르는 행위 자체가 곧 "이걸 지워라"가 되어버리기 때문).
                    if (matches.Count != 1)
                    {
                        summary.Skipped.Add((entry, matches.Count == 0
                            ? "대상 문서에 같은 이름의 재료가 없음 (이미 없거나 이름이 다름)"
                            : "이름이 같은 재료가 여러 개 있어 어느 것인지 확정할 수 없음"));
                        return false;
                    }

                    resolved.Add(new ResolvedEntry { Entry = entry, Status = ResolutionStatus.Auto, PrimaryTargetId = matches[0].Id });
                    return false;
                }

                case ChangeKind.MaterialIdentityEdit:
                {
                    List<Element> allMaterials = new FilteredElementCollector(targetDoc).OfClass(typeof(Material)).ToList();
                    List<Element> matches = allMaterials.Where(m => m.Name == entry.Key).ToList();

                    if (!TryResolveSingle(ownerHandle, entry, matches, allMaterials,
                        el => el.Name ?? "",
                        $"'{entry.Key}' 재료를 찾을 수 없거나 여러 개입니다.\n어느 재료의 {(entry.Field == IdentityField.MaterialClass ? "클래스" : "설명")}를 바꿀지 고르세요.",
                        out Element? pickedMaterial, out bool cancelled, out bool skipped))
                    {
                        if (cancelled) return true;
                        if (skipped) { summary.Skipped.Add((entry, "대상 재료를 찾지 못해 사용자가 건너뜀")); return false; }
                    }

                    resolved.Add(new ResolvedEntry
                    {
                        Entry = entry,
                        Status = matches.Count == 1 ? ResolutionStatus.Auto : ResolutionStatus.Picked,
                        PrimaryTargetId = pickedMaterial!.Id,
                    });
                    return false;
                }

                default:
                    summary.Skipped.Add((entry, "알 수 없는 변경 종류"));
                    return false;
            }
        }

        // matches가 정확히 하나면 그대로 자동 확정(true 반환, out picked에 채움). 0개/여러 개면
        // ElementPickerWindow를 띄운다 - 반환값 false는 "picked가 아직 없고, cancelled/skipped 중 하나를 봐야
        // 함"을 의미한다(호출부가 매번 세 가지 out을 다 확인하도록 강제하기 위해 일부러 bool 반환 + out picked
        // 조합을 씀).
        private static bool TryResolveSingle(IntPtr ownerHandle, ChangeLogEntry entry, List<Element> matches, List<Element> allCandidates,
            Func<Element, string> displaySelector, string pickerHeader,
            out Element? picked, out bool cancelled, out bool skipped)
        {
            cancelled = false;
            skipped = false;

            if (matches.Count == 1)
            {
                picked = matches[0];
                return true;
            }

            var pickerWindow = new ElementPickerWindow(pickerHeader, allCandidates, displaySelector);
            new WindowInteropHelper(pickerWindow) { Owner = ownerHandle };
            bool? dialogResult = pickerWindow.ShowDialog();

            if (dialogResult != true) { picked = null; cancelled = true; return false; }
            if (pickerWindow.Skipped) { picked = null; skipped = true; return false; }

            picked = pickerWindow.Result;
            return true;
        }

        private static Func<Element, string> DisplayNameSelectorFor(NamerWindow.NamerCategory category) =>
            category is NamerWindow.NamerCategory.Type or NamerWindow.NamerCategory.Family
                ? (el => el is ElementType et && !string.IsNullOrEmpty(et.FamilyName) ? $"{et.FamilyName} : {et.Name}" : (el.Name ?? ""))
                : (el => el.Name ?? "");

        // 여기서부터 트랜잭션 하나 - 모달 창은 위에서 전부 끝났으므로 이 안에서는 순수 반영만 한다.
        private static ReplaySummary Apply(Document targetDoc, List<ResolvedEntry> resolved, ReplaySummary summary)
        {
            if (resolved.Count == 0) { summary.TransactionCommitted = true; return summary; }

            TransactionStatus status;
            using (Transaction tx = new Transaction(targetDoc, "모델간 변경 반영 - 변경사항 적용"))
            {
                tx.Start();

                foreach (ResolvedEntry re in resolved)
                {
                    try
                    {
                        (bool ok, string? reason) = ApplyOne(targetDoc, re);
                        if (ok) summary.AppliedCount++;
                        else summary.Failed.Add((re.Entry, reason ?? "알 수 없는 이유로 실패"));
                    }
                    catch (System.Exception ex)
                    {
                        summary.Failed.Add((re.Entry, ex.Message));
                    }
                }

                // NamerCommand/MaterialAssignCommand와 같은 이유로 커스텀 IFailuresPreprocessor를 붙이지 않는다
                // (이름 변경/재료 지정/삭제와 같은 부류의 작업이라 같은 위험을 그대로 안고 있다).
                status = tx.Commit();
            }

            summary.TransactionCommitted = status == TransactionStatus.Committed;
            if (!summary.TransactionCommitted)
            {
                summary.AppliedCount = 0;
                summary.Failed.Clear();
                summary.Failed.AddRange(resolved.Select(r => (r.Entry, $"트랜잭션 롤백: {status}")));
            }
            return summary;
        }

        private static (bool ok, string? reason) ApplyOne(Document doc, ResolvedEntry re)
        {
            ChangeLogEntry entry = re.Entry;

            switch (entry.Kind)
            {
                case ChangeKind.Rename:
                {
                    Element? target = doc.GetElement(re.PrimaryTargetId);
                    if (target == null) return (false, "대상 요소를 찾을 수 없음");

                    if (target is Material mat)
                    {
                        var failed = new List<string>();
                        var merged = new List<string>();
                        NamerCommand.RenameMaterial(doc, mat, entry.Key, entry.NewValue ?? "",
                            entry.MergeDuplicateMaterials ?? true, failed, merged);
                        return failed.Count == 0 ? (true, null) : (false, failed[0]);
                    }

                    target.Name = entry.NewValue ?? "";
                    return (true, null);
                }

                case ChangeKind.MaterialAssign:
                {
                    Element? typeEl = doc.GetElement(re.PrimaryTargetId);
                    if (typeEl is not ElementType type) return (false, "대상 유형을 찾을 수 없음");
                    if (re.TargetSlot == null) return (false, "대상 슬롯 정보 없음");

                    bool ok = MaterialAssignCommand.TryAssignMaterial(type, re.TargetSlot.Value, re.SecondaryTargetId, out _, out string reason);
                    return (ok, ok ? null : reason);
                }

                case ChangeKind.MaterialDelete:
                {
                    bool ok = MaterialAssignCommand.TryDeleteMaterial(doc, re.PrimaryTargetId, out string reason);
                    return (ok, ok ? null : reason);
                }

                case ChangeKind.MaterialIdentityEdit:
                {
                    Element? matEl = doc.GetElement(re.PrimaryTargetId);
                    if (matEl is not Material mat) return (false, "대상 재료를 찾을 수 없음");

                    string? newClass = entry.Field == IdentityField.MaterialClass ? entry.NewValue : null;
                    string? newDescription = entry.Field == IdentityField.Description ? entry.NewValue : null;
                    bool ok = MaterialAssignCommand.TrySetMaterialIdentity(mat, newClass, newDescription, out string reason);
                    return (ok, ok ? null : reason);
                }

                default:
                    return (false, "알 수 없는 변경 종류");
            }
        }
    }
}
