using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    internal enum MaterialSlotKind { CompoundLayer, Parameter }

    // 유형(ElementType) 하나가 가진 "재료 지정" 대상 슬롯 하나를 표현한다. 유형 하나가 여러 재료를 동시에
    // 쓸 수 있으므로(레이어가 여러 개인 벽/바닥, 재료 파라미터가 여러 개인 문/창 등) FindAll은 슬롯 여러 개를
    // 반환할 수 있다 - CompoundLayer는 레이어 인덱스로, Parameter는 Parameter.Id로 서로 구분한다.
    // ParameterRef는 Find 시점에 찾은 Parameter 참조 자체를 들고 있는다(Apply에서 그대로 재사용하면
    // "같은 파라미터를 이름/순서로 다시 찾아야 하는" 모호함이 아예 생기지 않는다) - 단, Apply는 항상
    // FindSlot으로 커밋 시점 기준 최신 슬롯을 다시 얻어 그 ParameterRef를 쓰므로, 여기 담긴 값이 오래돼도
    // (예: 다른 문서에서 만든 값을 식별용으로만 넘길 때) 안전하다.
    internal readonly struct MaterialSlot
    {
        public MaterialSlotKind Kind { get; }
        public int LayerIndex { get; }
        public ElementId ParameterId { get; }
        public ElementId MaterialId { get; }
        public Parameter? ParameterRef { get; }
        // 유형 하나에 슬롯이 여러 개일 때 UI/ChangeLog에서 서로 구분해 보여주는 표시 이름
        // ("레이어 2", 파라미터의 Definition.Name 등). 슬롯이 하나뿐인 레이어 유형은 굳이 구분할 필요가
        // 없어 빈 문자열로 둔다 - 기존(슬롯이 하나일 때만 대상으로 삼던 시절)과 같은 표시를 유지하기 위함.
        public string Label { get; }

        public MaterialSlot(MaterialSlotKind kind, int layerIndex, ElementId materialId, string label, ElementId? parameterId = null, Parameter? parameterRef = null)
        {
            Kind = kind;
            LayerIndex = layerIndex;
            MaterialId = materialId;
            Label = label;
            ParameterId = parameterId ?? ElementId.InvalidElementId;
            ParameterRef = parameterRef;
        }

        // 슬롯의 "정체성"(어느 레이어/파라미터인지)만 비교한다 - MaterialId(그 시점의 재료)나 ParameterRef는
        // 시점에 따라 다를 수 있으므로 비교 대상에서 제외한다.
        internal bool SameIdentityAs(MaterialSlot other) =>
            Kind == other.Kind && (Kind == MaterialSlotKind.CompoundLayer ? LayerIndex == other.LayerIndex : ParameterId == other.ParameterId);
    }

    // MaterialAssignWindow(목록/미리보기)와 MaterialAssignCommand(실제 반영) 양쪽이 공유하는, 유형 하나에서
    // "재료 지정" 대상이 될 슬롯들을 찾는 순수 판정 로직.
    // - 벽/바닥/지붕/천장처럼 CompoundStructure(레이어 구조)를 가진 유형(HostObjAttributes)은, 두께가 있는
    //   (비-멤브레인) 레이어 전부가 각각 하나의 슬롯이 된다 - 레이어가 하나뿐이면(WallSplitter/SplitFloorCommand가
    //   만드는 "단일 재질" 유형이 이 경우) 이전과 동일하게 슬롯 하나만 나온다.
    // - 그 외 유형(문/창/가구/구조 부재 등)은 재료(Material) 스펙을 갖는 파라미터 전부가 각각 슬롯이 된다.
    internal static class MaterialSlotFinder
    {
        private const double MinLayerWidth = 1e-9;

        public static List<MaterialSlot> FindAll(ElementType type)
        {
            var result = new List<MaterialSlot>();

            if (type is HostObjAttributes hostAttrs)
            {
                CompoundStructure? structure = hostAttrs.GetCompoundStructure();
                if (structure == null) return result;

                IList<CompoundStructureLayer> layers = structure.GetLayers();
                List<int> nonMembraneIndices = new List<int>();
                for (int i = 0; i < layers.Count; i++)
                    if (layers[i].Width >= MinLayerWidth) nonMembraneIndices.Add(i);

                bool multiple = nonMembraneIndices.Count > 1;
                for (int n = 0; n < nonMembraneIndices.Count; n++)
                {
                    int idx = nonMembraneIndices[n];
                    string label = multiple ? $"레이어 {n + 1}" : "";
                    result.Add(new MaterialSlot(MaterialSlotKind.CompoundLayer, idx, layers[idx].MaterialId, label));
                }
                return result;
            }

            foreach (Parameter p in type.Parameters)
            {
                if (p.StorageType != StorageType.ElementId) continue;
                Definition? def = p.Definition;
                if (def == null) continue;

                bool isMaterialParam;
                try { isMaterialParam = def.GetDataType() == SpecTypeId.Reference.Material; }
                catch { isMaterialParam = false; }
                if (!isMaterialParam) continue;

                result.Add(new MaterialSlot(MaterialSlotKind.Parameter, -1, p.AsElementId(), def.Name, p.Id, p));
            }
            return result;
        }

        // 슬롯이 하나라도 있으면 이 유형은 "재료 지정" 대상이 된다는 것만 확인할 때 쓰는 판정 - 몇 개인지,
        // 어느 슬롯인지는 신경 쓰지 않는다(FindEligibleTypes 전용).
        private static bool HasAnySlot(ElementType type) => FindAll(type).Count > 0;

        // targetIdentity와 같은 슬롯(Kind+LayerIndex 또는 Kind+ParameterId)을 지금 시점 기준으로 다시 찾는다 -
        // 창이 떠 있던 동안 얻은 슬롯을 그대로 믿지 않고, 커밋 직전 실제 문서 상태에서 다시 확인/반영하기 위함.
        public static MaterialSlot? FindSlot(ElementType type, MaterialSlot targetIdentity)
        {
            foreach (MaterialSlot slot in FindAll(type))
                if (slot.SameIdentityAs(targetIdentity)) return slot;
            return null;
        }

        // 이름(Label)만으로 슬롯을 찾는다 - 다른 문서로 변경사항을 재현할 때(ChangeReplayEngine)는
        // ElementId/Parameter.Id가 문서마다 다르므로 Label로만 같은 슬롯을 다시 찾을 수 있다.
        public static MaterialSlot? FindSlotByLabel(ElementType type, string label)
        {
            List<MaterialSlot> all = FindAll(type);
            foreach (MaterialSlot slot in all)
                if (slot.Label == label) return slot;
            // SlotLabel 필드가 없던 과거 기록과의 호환 - label이 비어있고 슬롯이 정확히 하나면 그것으로 간주.
            if (string.IsNullOrEmpty(label) && all.Count == 1) return all[0];
            return null;
        }

        // 새 재료를 실제로 적용한다 (열려 있는 Transaction 안에서 호출) - targetIdentity로 지금 시점의 실제
        // 슬롯을 다시 찾아서 쓰므로, 창이 떠 있던 동안 얻은 값이 아니라 커밋 시점의 실제 문서 상태를 기준으로
        // 안전하게 반영된다.
        public static bool Apply(ElementType type, MaterialSlot targetIdentity, ElementId newMaterialId, out MaterialSlot? previousSlot)
        {
            MaterialSlot? slot = FindSlot(type, targetIdentity);
            previousSlot = slot;
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

        // 문서 하나에서 "재료 지정" 대상이 될 수 있는 유형 전체를 모은다(슬롯이 하나 이상이면 대상). 이 목록은
        // ChangeReplayEngine의 "유형 이름 매칭이 모호할 때 사용자가 고를 후보 목록"으로만 쓰이는, 조건 하나만
        // 있는 순수 목록이다 - MaterialAssignWindow.LoadCandidates는 창 표시용 부가 정보까지 같이 만들고
        // 슬롯 단위로 후보를 펼치므로 그대로 재사용하지 않는다.
        internal static List<ElementType> FindEligibleTypes(Document doc)
        {
            var result = new List<ElementType>();
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                if (el is ElementType type && HasAnySlot(type)) result.Add(type);
            }
            return result.OrderBy(t => t.Name).ToList();
        }
    }
}
