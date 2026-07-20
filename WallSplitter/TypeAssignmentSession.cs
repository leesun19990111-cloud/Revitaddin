using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 어떤 종류의 호스트 요소를 분리하는 중인지 - Wall/Floor 세션을 완전히 분리해 두지 않으면,
    // 벽 레이어의 (재료, 두께) 조합이 우연히 바닥 레이어와 같을 때 벽에서 지정한 WallType Id를
    // 바닥에 그대로 재적용하려다 캐스팅에 실패하는 사고가 날 수 있다.
    internal enum TypeAssignmentTarget
    {
        Wall,
        Floor,
    }

    // '유형 직접 지정' 모드(NamingMode.DirectType)의 '복수' 지속 설정에서, 직전에 사용자가 지정한
    // 레이어별 유형을 기억해 뒀다가 같은 레이어 구성(개수+재료+두께, 순서대로)을 가진 다음 벽/바닥에
    // 자동으로 재적용하기 위한 세션 상태. 현재 Revit 문서 세션 동안만 메모리에 유지되고,
    // 디스크에 저장하지 않는다 - 선택된 유형의 ElementId는 그 값을 만든 문서 밖에서는 의미가 없어서다.
    // (문서가 바뀌면 자동으로 무효화되도록 문서 키까지 같이 기억해 둔다.)
    internal static class TypeAssignmentSession
    {
        private class State
        {
            public string? DocumentKey;
            public List<(ElementId MaterialId, double WidthFeet)>? Signature;
            public List<ElementId>? Selections;
        }

        private static readonly Dictionary<TypeAssignmentTarget, State> States = new();

        private static State GetState(TypeAssignmentTarget target)
        {
            if (!States.TryGetValue(target, out State? state))
            {
                state = new State();
                States[target] = state;
            }
            return state;
        }

        // 현재 레이어 구성이 직전 지정과 완전히 같으면 그때 선택했던 유형들을 그대로 재사용한다.
        // false를 반환하면서 mismatchReason이 null이면 "최초 지정"(경고 아님), non-null이면
        // 레이어 구성이 달라서 다시 물어봐야 하는 구체적 사유(빨간 글씨로 표시할 문구)다.
        internal static bool TryGetReusableSelection(
            TypeAssignmentTarget target,
            Document doc,
            List<(ElementId MaterialId, double WidthFeet)> currentSignature,
            out List<ElementId> selections,
            out string? mismatchReason)
        {
            selections = new List<ElementId>();
            mismatchReason = null;

            State state = GetState(target);
            string docKey = doc.PathName ?? doc.Title;
            if (state.Signature == null || state.DocumentKey != docKey)
                return false;

            if (state.Signature.Count != currentSignature.Count)
            {
                mismatchReason = $"레이어 개수가 직전 지정({state.Signature.Count}개)과 달라 다시 지정해야 합니다 (현재 {currentSignature.Count}개).";
                return false;
            }

            for (int i = 0; i < currentSignature.Count; i++)
            {
                (ElementId MaterialId, double WidthFeet) prev = state.Signature[i];
                (ElementId MaterialId, double WidthFeet) cur = currentSignature[i];
                if (prev.MaterialId != cur.MaterialId || Math.Abs(prev.WidthFeet - cur.WidthFeet) > 1e-6)
                {
                    mismatchReason = $"{i + 1}번째 레이어의 재료 또는 두께가 직전 지정과 달라 다시 지정해야 합니다.";
                    return false;
                }
            }

            selections = state.Selections!;
            return true;
        }

        internal static void Remember(
            TypeAssignmentTarget target, Document doc,
            List<(ElementId MaterialId, double WidthFeet)> signature, List<ElementId> selections)
        {
            State state = GetState(target);
            state.DocumentKey = doc.PathName ?? doc.Title;
            state.Signature = signature;
            state.Selections = selections;
        }

        // 지속 방식(단일/복수) 자체가 바뀌는 등, 모든 대상의 "직전 선택"을 한 번에 무효화할 때 사용.
        internal static void Reset()
        {
            States.Clear();
        }
    }
}
