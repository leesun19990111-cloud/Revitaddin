using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    internal enum MaterialSlotKind { CompoundLayer, Parameter }

    // 유형(ElementType) 하나가 가진 "현재 재료" 슬롯 하나를 표현한다. CompoundLayer는 레이어 인덱스만,
    // Parameter는 Find() 시점에 찾은 Parameter 참조 자체를 들고 있는다(Apply에서 그대로 재사용하면
    // "같은 파라미터를 이름/순서로 다시 찾아야 하는" 모호함이 아예 생기지 않는다).
    internal readonly struct MaterialSlot
    {
        public MaterialSlotKind Kind { get; }
        public int LayerIndex { get; }
        public ElementId MaterialId { get; }
        public Parameter? ParameterRef { get; }

        public MaterialSlot(MaterialSlotKind kind, int layerIndex, ElementId materialId, Parameter? parameterRef = null)
        {
            Kind = kind;
            LayerIndex = layerIndex;
            MaterialId = materialId;
            ParameterRef = parameterRef;
        }
    }

    // MaterialAssignWindow(목록/미리보기)와 MaterialAssignCommand(실제 반영) 양쪽이 공유하는, 유형 하나에서
    // "재료 지정" 대상이 될 단 하나의 슬롯을 찾는 순수 판정 로직.
    // - 벽/바닥/지붕/천장처럼 CompoundStructure(레이어 구조)를 가진 유형(HostObjAttributes)은, 두께가 있는
    //   (비-멤브레인) 레이어가 정확히 1개일 때만 그 레이어를 대상으로 삼는다 - 레이어가 여러 개면 어느 레이어를
    //   바꿀지 모호하므로 제외한다. WallSplitter/SplitFloorCommand가 만드는 "단일 재질" 유형이 바로 이 조건을
    //   만족하도록 설계돼 있어, 이 도구의 실질적인 주 대상이 된다.
    // - 그 외 유형(문/창/가구/구조 부재 등)은 재료(Material) 스펙을 갖는 첫 번째 파라미터를 대상으로 삼는다.
    internal static class MaterialSlotFinder
    {
        private const double MinLayerWidth = 1e-9;

        public static MaterialSlot? Find(ElementType type)
        {
            if (type is HostObjAttributes hostAttrs)
            {
                CompoundStructure? structure = hostAttrs.GetCompoundStructure();
                if (structure == null) return null;

                IList<CompoundStructureLayer> layers = structure.GetLayers();
                List<int> nonMembraneIndices = new List<int>();
                for (int i = 0; i < layers.Count; i++)
                {
                    if (layers[i].Width >= MinLayerWidth) nonMembraneIndices.Add(i);
                }

                if (nonMembraneIndices.Count != 1) return null;

                int idx = nonMembraneIndices[0];
                return new MaterialSlot(MaterialSlotKind.CompoundLayer, idx, layers[idx].MaterialId);
            }

            foreach (Parameter p in type.Parameters)
            {
                if (p.StorageType != StorageType.ElementId) continue;
                Definition? def = p.Definition;
                if (def == null) continue;

                bool isMaterialParam;
                try { isMaterialParam = def.GetDataType() == SpecTypeId.Reference.Material; }
                catch { isMaterialParam = false; }

                if (isMaterialParam) return new MaterialSlot(MaterialSlotKind.Parameter, -1, p.AsElementId(), p);
            }

            return null;
        }

        // 새 재료를 실제로 적용한다 (열려 있는 Transaction 안에서 호출) - 슬롯을 매번 새로 찾아서 쓰므로,
        // 창이 떠 있던 동안 얻은 값이 아니라 커밋 시점의 실제 문서 상태를 기준으로 안전하게 반영된다.
        public static bool Apply(ElementType type, ElementId newMaterialId)
        {
            MaterialSlot? slot = Find(type);
            if (slot == null) return false;

            if (slot.Value.Kind == MaterialSlotKind.CompoundLayer)
            {
                if (type is not HostObjAttributes hostAttrs) return false;
                CompoundStructure? structure = hostAttrs.GetCompoundStructure();
                if (structure == null) return false;

                // CompoundStructureLayer는 불변 객체라 재료만 바꾸는 세터가 없다 - 같은 두께/기능을 유지한 채
                // 새 CompoundStructureLayer로 그 인덱스만 교체한 목록을 SetLayers에 통째로 다시 넘겨야 한다
                // (SplitWallCommand/SplitFloorCommand가 새 유형을 만들 때 쓰는 것과 같은 패턴).
                IList<CompoundStructureLayer> layers = structure.GetLayers();
                var newLayers = new List<CompoundStructureLayer>(layers.Count);
                for (int i = 0; i < layers.Count; i++)
                {
                    CompoundStructureLayer layer = layers[i];
                    newLayers.Add(i == slot.Value.LayerIndex
                        ? new CompoundStructureLayer(layer.Width, layer.Function, newMaterialId)
                        : layer);
                }
                structure.SetLayers(newLayers);
                hostAttrs.SetCompoundStructure(structure);
                return true;
            }

            Parameter? param = slot.Value.ParameterRef;
            if (param == null || param.IsReadOnly) return false;
            param.Set(newMaterialId);
            return true;
        }
    }
}
