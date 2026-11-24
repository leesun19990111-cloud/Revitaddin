using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 창을 열었을 때 확인한 유형/매개변수/인스턴스 범위를 그대로 전달한다.
    internal sealed class MaterialInstanceAssignment
    {
        internal ElementId TypeId { get; }
        internal MaterialSlot Slot { get; }
        internal List<ElementId> InstanceIds { get; }
        internal ElementId NewMaterialId { get; }

        internal MaterialInstanceAssignment(ElementId typeId, MaterialSlot slot, List<ElementId> instanceIds, ElementId materialId)
        {
            TypeId = typeId;
            Slot = slot;
            InstanceIds = instanceIds.Distinct().ToList();
            NewMaterialId = materialId;
        }

        // 호출자가 연 Transaction 안에서 유형의 해당 매개변수 전체를 한 묶음으로 반영한다.
        // 하나라도 잠금/수식/그룹 등의 이유로 실패하면 이 묶음을 전부 롤백해 일부만 바뀌지 않게 한다.
        internal bool TryApply(Document doc, out List<ElementId> oldMaterialIds, out string reason)
        {
            oldMaterialIds = new List<ElementId>();
            reason = "";
            if (Slot.Kind != MaterialSlotKind.InstanceParameter || InstanceIds.Count == 0
                || doc.GetElement(NewMaterialId) is not Material)
            {
                reason = "인스턴스 대상 또는 새 재료가 올바르지 않습니다.";
                return false;
            }

            using var sub = new SubTransaction(doc);
            try
            {
                var targets = new List<Element>();
                foreach (ElementId id in InstanceIds)
                {
                    Element? element = doc.GetElement(id);
                    if (element == null || element is ElementType || element.GetTypeId() != TypeId)
                        throw new InvalidOperationException($"요소 {id}: 삭제되었거나 유형이 변경되었습니다.");
                    MaterialSlot? current = MaterialSlotFinder.FindSlot(element, Slot);
                    if (current == null)
                        throw new InvalidOperationException($"요소 {id}: 재료 매개변수를 찾지 못했습니다.");
                    if (current.Value.MaterialId != NewMaterialId && current.Value.ParameterRef?.IsReadOnly != false)
                        throw new InvalidOperationException($"요소 {id}: 읽기 전용 재료 매개변수입니다.");
                    oldMaterialIds.Add(current.Value.MaterialId);
                    if (current.Value.MaterialId != NewMaterialId) targets.Add(element);
                }

                if (sub.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("인스턴스 재료 변경을 시작하지 못했습니다.");
                foreach (Element element in targets)
                {
                    if (!MaterialSlotFinder.Apply(element, Slot, NewMaterialId, out _))
                        throw new InvalidOperationException($"요소 {element.Id}: 재료 지정 실패");
                    MaterialSlot? verify = MaterialSlotFinder.FindSlot(element, Slot);
                    if (verify == null || verify.Value.MaterialId != NewMaterialId)
                        throw new InvalidOperationException($"요소 {element.Id}: 변경 후 재료 확인 실패");
                }
                if (sub.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("인스턴스 재료 변경이 롤백되었습니다.");
                return true;
            }
            catch (Exception ex)
            {
                if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                reason = ex.Message + " — 이 유형의 해당 인스턴스 재료는 모두 변경하지 않았습니다.";
                return false;
            }
        }
    }
}

