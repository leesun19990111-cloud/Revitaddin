using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 특정 뷰의 뷰템플릿/필터 표시/작업세트 표시 상태를 한 시점에 찍어둔 스냅샷 - "뷰 저장"/"되돌리기"
    // 고정 버튼(2026-07-27 추가)이 쓴다. 디스크에 저장하지 않는 이번 세션 한정 메모리 값이라
    // (QuickToggleToolbar._savedViewState 참고) 문서/뷰 이름 같은 참고용 필드는 없고 ElementId만 담는다.
    internal class ViewStateSnapshot
    {
        public int ViewId { get; set; }
        public int? ViewTemplateId { get; set; }
        public Dictionary<int, bool> FilterVisibility { get; set; } = new();
        public Dictionary<int, bool> WorksetVisibility { get; set; } = new(); // true = Visible
    }

    // 커스텀 툴바(QuickToggleToolbar)는 세션 내내 떠 있는 모드리스 창이라 버튼 클릭이 언제든 일어날 수
    // 있고, 그 시점엔 유효한 Revit API 컨텍스트(트랜잭션 등)가 열려있지 않다. ExternalEvent.Raise()로
    // 요청을 넣어두면 Revit이 다음 기회에 Execute를 유효한 컨텍스트에서 실행해준다.
    // 이 프로젝트에서 ExternalEvent를 쓰는 첫 사례 - 기존 창들은 전부 커맨드 Execute 안에서 여는
    // 모달 ShowDialog()라 이런 비동기 콜백이 필요 없었다.
    public class QuickToggleExternalEventHandler : IExternalEventHandler
    {
        public string? PendingButtonId { get; set; }
        public bool PendingTurnOn { get; set; }

        // null이 아니면 이번 Raise()는 "빠른 토글 버튼 켜기/끄기"가 아니라 "되돌리기" 요청이다
        // (2026-07-27 추가) - 같은 ExternalEvent를 재사용해 별도 배선 없이 처리한다.
        internal ViewStateSnapshot? PendingRevertSnapshot { get; set; }

        public void Execute(UIApplication app)
        {
            if (PendingRevertSnapshot != null)
            {
                ViewStateSnapshot snapshot = PendingRevertSnapshot;
                PendingRevertSnapshot = null;
                ExecuteRevert(app, snapshot);
                return;
            }

            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;

            Document doc = uidoc.Document;
            View? view = doc.ActiveView;
            if (view == null) return;

            QuickToggleSettings settings = QuickToggleSettings.Load(doc);
            QuickToggleButtonConfig? cfg = settings.Buttons.Find(b => b.Id == PendingButtonId);
            if (cfg == null) return;

            bool applied;
            TransactionStatus status;
            using (Transaction tx = new Transaction(doc, "빠른 토글: " + cfg.Name))
            {
                tx.Start();
                applied = QuickToggleService.Toggle(view, cfg, PendingTurnOn);
                status = applied ? tx.Commit() : tx.RollBack();
            }

            QuickToggleToolbar.Instance?.RefreshState();

            // ExternalEvent 콜백은 실패해도 Revit이 사용자에게 아무 것도 보여주지 않으므로("버튼을
            // 눌러도 반응이 없다"는 증상으로만 남는다), 여기서 직접 알려준다.
            if (!applied || status != TransactionStatus.Committed)
            {
                TaskDialog.Show("빠른 토글",
                    $"'{cfg.Name}' 버튼을 반영하지 못했습니다 (예: 대상 뷰템플릿이 이 뷰 종류와 호환되지 않음).");
            }
        }

        // "되돌리기" 실행 - 저장했던 뷰로 먼저 전환한 뒤(그 사이 다른 뷰를 보고 있었을 수 있으므로),
        // 그 뷰의 뷰템플릿/필터 표시/작업세트 표시를 저장 시점 값으로 되돌린다. 모델 자체의 형상/데이터
        // 변경(벽을 옮겼다든가)은 전혀 건드리지 않는다 - 이 스냅샷은 "뷰에 무엇이 보이는지"만 기억한다.
        private static void ExecuteRevert(UIApplication app, ViewStateSnapshot snapshot)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;
            Document doc = uidoc.Document;

            if (doc.GetElement(new ElementId(snapshot.ViewId)) is not View targetView)
            {
                TaskDialog.Show("빠른 토글", "저장했던 뷰를 찾을 수 없습니다 (그 사이 삭제되었을 수 있습니다).");
                return;
            }

            // 저장했던 뷰가 지금 활성 뷰와 다르면 먼저 그 뷰로 전환한다 - "여러 번 뷰가 바뀌더라도
            // 되돌리기를 누르면 저장했던 뷰로 돌아간다"는 요청사항.
            if (doc.ActiveView == null || doc.ActiveView.Id != targetView.Id)
            {
                try { uidoc.ActiveView = targetView; }
                catch
                {
                    TaskDialog.Show("빠른 토글", "저장했던 뷰로 전환하지 못했습니다.");
                    return;
                }
            }

            using (Transaction tx = new Transaction(doc, "빠른 토글: 뷰 상태 되돌리기"))
            {
                tx.Start();

                try
                {
                    targetView.ViewTemplateId = snapshot.ViewTemplateId.HasValue
                        ? new ElementId(snapshot.ViewTemplateId.Value)
                        : ElementId.InvalidElementId;
                }
                catch
                {
                    // 저장했던 뷰템플릿이 그 사이 삭제되었거나 이 뷰 종류와 안 맞는 경우 - 나머지는 계속 적용
                }

                ICollection<ElementId> currentFilters;
                try { currentFilters = targetView.GetFilters(); }
                catch { currentFilters = new List<ElementId>(); }

                foreach (KeyValuePair<int, bool> kvp in snapshot.FilterVisibility)
                {
                    try
                    {
                        ElementId filterId = new ElementId(kvp.Key);
                        // 저장 시점 이후 필터 자체가 뷰에서 빠졌으면(삭제 등) 되살리지 않는다 - 이 기능은
                        // 표시 상태만 되돌릴 뿐, 필터 목록 구성 자체를 복원하지는 않는다.
                        if (!currentFilters.Contains(filterId)) continue;
                        targetView.SetFilterVisibility(filterId, kvp.Value);
                    }
                    catch
                    {
                        // 개별 필터 실패는 무시하고 나머지는 계속 적용
                    }
                }

                if (doc.IsWorkshared)
                {
                    foreach (KeyValuePair<int, bool> kvp in snapshot.WorksetVisibility)
                    {
                        try
                        {
                            targetView.SetWorksetVisibility(new WorksetId(kvp.Key),
                                kvp.Value ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
                        }
                        catch
                        {
                            // 삭제된 작업세트 등 - 나머지는 계속 적용
                        }
                    }
                }

                tx.Commit();
            }

            QuickToggleToolbar.Instance?.RefreshState();
        }

        public string GetName() => "WallSplitter 빠른 토글";
    }
}
