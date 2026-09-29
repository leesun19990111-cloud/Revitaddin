using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 요청 이후 창의 대상 문서/필터/체크가 바뀌어도 삭제 범위가 넓어지지 않는 스냅샷.
    internal sealed class WarningPickDeleteRequest
    {
        internal Document Document { get; }
        internal List<ElementId> Ids { get; }
        internal string Scope { get; }
        internal WarningPickDeleteRequest(Document document, IEnumerable<ElementId> ids, string scope)
        {
            Document = document;
            Ids = ids.Distinct().ToList();
            Scope = scope;
        }
    }

    internal sealed class WarningPickDeletePreview
    {
        internal int TargetCount { get; }
        internal int TotalCount { get; }
        internal int DependentCount => TotalCount - TargetCount;
        internal string Details { get; }
        internal WarningPickDeletePreview(int targetCount, int totalCount, string details)
        {
            TargetCount = targetCount;
            TotalCount = totalCount;
            Details = details;
        }
    }

    internal static class WarningPickDeleteService
    {
        // 실제 문서에서는 반드시 ExternalEvent 안에서만 호출한다. 사전 계산은 무조건 롤백하고,
        // 명시적 확인 후 별도 트랜잭션 하나로 삭제한다. 잠금/그룹/소유권 등은 강제로 해제하지 않는다.
        internal static string Delete(Document doc, IEnumerable<ElementId> requested,
            Func<WarningPickDeletePreview, bool> confirm)
        {
            if (doc.IsReadOnly || doc.IsModifiable)
                return "현재 문서는 삭제할 수 없는 상태입니다. 진행 중인 명령을 끝낸 뒤 다시 시도해 주세요.";

            List<ElementId> ids = requested.Distinct().ToList();
            if (ids.Count == 0) return "삭제할 요소가 없습니다.";
            HashSet<ElementId> currentWarningIds = new HashSet<ElementId>(
                doc.GetWarnings().SelectMany(w => w.GetFailingElements().Concat(w.GetAdditionalElements())));
            // 목록을 읽은 뒤 없어지거나 경고가 해결된 요소를 조용히 삭제하지 않는다.
            if (ids.Any(id => doc.GetElement(id) == null || !currentWarningIds.Contains(id)))
                return "대상 요소나 경고가 변경되었습니다. 목록을 새로고침한 뒤 다시 선택해 주세요.";

            HashSet<ElementId> expected;
            using (Transaction probe = new Transaction(doc, "경고Pick: 삭제 범위 확인 (롤백)"))
            {
                if (probe.Start() != TransactionStatus.Started)
                    return "삭제 범위를 확인할 수 없어 중단했습니다.";
                try { expected = new HashSet<ElementId>(doc.Delete(ids)); }
                finally
                {
                    if (probe.GetStatus() == TransactionStatus.Started)
                        probe.RollBack();
                }
                if (probe.GetStatus() != TransactionStatus.RolledBack)
                    throw new InvalidOperationException("삭제 범위 확인을 안전하게 되돌리지 못했습니다.");
            }
            if (!expected.IsSupersetOf(ids))
                return "요청한 요소 전체를 삭제할 수 없어 중단했습니다.";

            // 의존 요소는 원상 복구된 문서에서 이름을 읽는다. 상세 정보에 ID까지 모두 제공한다.
            HashSet<ElementId> targets = new HashSet<ElementId>(ids);
            string details = string.Join("\n", expected.OrderBy(id => targets.Contains(id) ? 0 : 1).ThenBy(id => id.ToString())
                .Select(id =>
                {
                    Element? element = doc.GetElement(id);
                    return (targets.Contains(id) ? "[대상] " : "[함께 삭제] ") +
                        (element?.Category?.Name ?? "기타") + " / " + (element?.Name ?? "(이름 없음)") + " / ID " + id;
                }));
            var preview = new WarningPickDeletePreview(ids.Count, expected.Count, details);
            if (!confirm(preview)) return "삭제를 취소했습니다. 모델은 변경하지 않았습니다.";

            int deletedCount;
            using (Transaction tx = new Transaction(doc, "경고Pick: 요소 삭제"))
            {
                if (tx.Start() != TransactionStatus.Started) return "삭제를 시작하지 못했습니다.";
                // 실패 처리 대기(Pending)로 돌아온 뒤 결과를 성급히 성공으로 표시하지 않도록 모달로 처리한다.
                tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetForcedModalHandling(true));
                HashSet<ElementId> actual = new HashSet<ElementId>(doc.Delete(ids));
                if (!actual.SetEquals(expected))
                {
                    tx.RollBack();
                    return "확인한 범위와 실제 삭제 범위가 달라 전체 취소했습니다. 다시 확인해 주세요.";
                }
                deletedCount = actual.Count;
                if (tx.Commit() != TransactionStatus.Committed)
                    return "삭제가 완료되지 않았습니다. Revit의 오류 안내를 확인해 주세요.";
            }
            return $"삭제 완료: 대상 {ids.Count}개 + 함께 삭제된 요소 {deletedCount - ids.Count}개 (총 {deletedCount}개). Revit 실행 취소로 되돌릴 수 있습니다.";
        }
    }
}
