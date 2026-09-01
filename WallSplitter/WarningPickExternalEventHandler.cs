using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // '경고Pick' 창(WarningPickWindow)은 QuickToggleToolbar처럼 세션 내내 떠 있을 수 있는 모드리스 창이라,
    // 목록 클릭 시점엔 유효한 Revit API 컨텍스트가 없다 - ExternalEvent.Raise()로 요청을 넣어 Revit이
    // 다음 기회에 유효한 컨텍스트에서 실행하게 한다 (QuickToggleExternalEventHandler와 같은 패턴).
    public class WarningPickExternalEventHandler : IExternalEventHandler
    {
        // 경고 목록을 조회했던 시점의 문서 - 창이 열린 뒤 사용자가 다른 문서로 전환했으면 선택을 거부하고
        // 안내한다(ElementId는 문서마다 독립적이라 다른 문서에서 같은 정수 ID가 전혀 다른 요소를 가리킬 수 있다).
        internal Document? TargetDocument { get; set; }

        // CONFIRMED LIVE BUG (2026-08-25): 문서를 하나만 열어 두고 뷰조차 바꾸지 않았는데도 "선택"을 누르면
        // 매번 "문서가 더 이상 활성 문서가 아닙니다"가 떴다 - 원인은 ReferenceEquals(uidoc.Document,
        // TargetDocument) 비교. Document는 Revit API 래퍼 객체라 창을 연 시점(WarningPickCommand.Execute)과
        // ExternalEvent가 실제 실행되는 시점에 각각 조회한 게 같은 열린 문서를 가리켜도 서로 다른 래퍼
        // 인스턴스일 수 있어 참조 비교가 항상 false로 나올 수 있다. QuickToggleToolbar.DocKey와 같은 방식으로
        // 경로(저장 안 된 문서는 제목)를 문서 식별자로 삼아 비교해야 한다 - Document를 ReferenceEquals/기본
        // Equals로 다시 비교하지 말 것.
        private static string DocKey(Document doc) => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

        private bool IsTargetDocument(Document doc) => TargetDocument != null && DocKey(doc) == DocKey(TargetDocument);

        internal List<ElementId>? PendingSelectIds { get; set; }

        // "선택 항목 단면상자로 보기" / "선택 항목만 표시" 요청 - 같은 ExternalEvent를 재사용한다
        // (QuickToggleExternalEventHandler가 여러 종류의 요청을 한 이벤트로 처리하는 것과 같은 패턴).
        internal List<ElementId>? PendingSectionBoxIds { get; set; }
        internal List<ElementId>? PendingIsolateIds { get; set; }
        internal bool PendingResetIsolate { get; set; }

        // true면 이번 Raise()는 "선택"이 아니라 경고 목록 새로고침 요청이다 - 같은 ExternalEvent를 재사용한다.
        internal bool PendingRefresh { get; set; }

        // ExternalEvent 콜백에서 예외가 나면 Revit은 사용자에게 아무 것도 보여주지 않는다 - "버튼을 눌러도
        // 반응이 없다"는 증상으로만 남는다(커스텀 버튼에서 실제로 겪은 문제, docs/quick-toggle 참고).
        // 그래서 여기서 직접 잡아 알려준다.
        public void Execute(UIApplication app)
        {
            try
            {
                ExecuteCore(app);
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("경고Pick", "요청을 실행하지 못했습니다.\n\n" + ex.GetBaseException().Message);
            }
        }

        private void ExecuteCore(UIApplication app)
        {
            if (PendingSelectIds != null)
            {
                List<ElementId> ids = PendingSelectIds;
                PendingSelectIds = null;
                ExecuteSelect(app, ids);
                return;
            }

            if (PendingSectionBoxIds != null)
            {
                List<ElementId> ids = PendingSectionBoxIds;
                PendingSectionBoxIds = null;
                ExecuteSectionBox(app, ids);
                return;
            }

            if (PendingIsolateIds != null)
            {
                List<ElementId> ids = PendingIsolateIds;
                PendingIsolateIds = null;
                ExecuteIsolate(app, ids);
                return;
            }

            if (PendingResetIsolate)
            {
                PendingResetIsolate = false;
                ExecuteResetIsolate(app);
                return;
            }

            if (PendingRefresh)
            {
                PendingRefresh = false;
                ExecuteRefresh(app);
            }
        }

        // 선택 + 뷰 이동을 함께 수행한다 - Revit 기본 "경고 표시"는 뷰로 안내만 하고 요소를 실제로
        // 선택해주지는 않는데, 그 불편함을 없애는 게 이 기능의 핵심이라 SetElementIds를 먼저 적용해 둔
        // 채로 ShowElements를 호출한다(ShowElements는 뷰 전환/줌만 하고 선택 상태는 건드리지 않는다).
        private void ExecuteSelect(UIApplication app, List<ElementId> ids)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;

            if (!IsTargetDocument(uidoc.Document))
            {
                WarningPickWindow.Instance?.ShowDocumentMismatch();
                return;
            }

            // 삭제 등으로 이제 존재하지 않는 ID는 걸러낸다 - 그 사이 사용자가 경고를 직접 해결했을 수 있다.
            List<ElementId> validIds = ids.Where(id => uidoc.Document.GetElement(id) != null).ToList();
            if (validIds.Count == 0)
            {
                TaskDialog.Show("경고Pick", "선택하려는 요소를 더 이상 찾을 수 없습니다 (이미 삭제되었을 수 있습니다).");
                return;
            }

            uidoc.Selection.SetElementIds(validIds);
            uidoc.ShowElements(validIds);
        }

        // 선택한 요소들을 감싸는 3D 뷰 단면상자를 만든다 - 단면상자는 View3D에만 존재하는 개념이라
        // 활성 뷰가 3D 뷰가 아니면 안내만 하고 아무것도 바꾸지 않는다(뷰를 대신 만들거나 전환하지 않음 -
        // 사용자가 보고 있던 뷰를 마음대로 바꾸는 건 과한 자동화라 범위 밖으로 뒀다).
        private void ExecuteSectionBox(UIApplication app, List<ElementId> ids)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null || !IsTargetDocument(uidoc.Document))
            {
                WarningPickWindow.Instance?.ShowDocumentMismatch();
                return;
            }
            Document doc = uidoc.Document;

            if (doc.ActiveView is not View3D view3D)
            {
                TaskDialog.Show("경고Pick", "단면상자는 3D 뷰에서만 만들 수 있습니다. 3D 뷰를 활성화한 뒤 다시 시도하세요.");
                return;
            }

            List<ElementId> validIds = ids.Where(id => doc.GetElement(id) != null).ToList();
            if (validIds.Count == 0)
            {
                TaskDialog.Show("경고Pick", "선택한 요소를 더 이상 찾을 수 없습니다 (이미 삭제되었을 수 있습니다).");
                return;
            }

            XYZ? min = null;
            XYZ? max = null;
            foreach (ElementId id in validIds)
                ExpandByElementBoundingBox(doc, view3D, id, ref min, ref max);

            if (min == null || max == null)
            {
                TaskDialog.Show("경고Pick", "선택한 요소의 형상 정보를 찾을 수 없어 단면상자를 만들 수 없습니다.");
                return;
            }

            // 요소 표면에 딱 붙으면 잘려 보이는 면이 시야에서 답답하므로 사방으로 약간 여유를 둔다.
            var padding = new XYZ(1.0, 1.0, 1.0);
            var box = new BoundingBoxXYZ { Min = min - padding, Max = max + padding };

            using (Transaction tx = new Transaction(doc, "경고Pick: 단면상자"))
            {
                tx.Start();
                view3D.IsSectionBoxActive = true;
                view3D.SetSectionBox(box);
                tx.Commit();
            }

            uidoc.Selection.SetElementIds(validIds);
        }

        // 요소의 바운딩박스는 로컬 좌표계일 수 있어(패밀리 인스턴스 등) Transform으로 8개 모서리를 실제
        // 위치로 변환한 뒤 min/max를 넓혀야 회전된 요소도 정확히 감싼다.
        private static void ExpandByElementBoundingBox(Document doc, View view, ElementId id, ref XYZ? min, ref XYZ? max)
        {
            Element? element = doc.GetElement(id);
            if (element == null) return;

            BoundingBoxXYZ? bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bbox == null) return;

            Transform t = bbox.Transform;
            foreach (double x in new[] { bbox.Min.X, bbox.Max.X })
            foreach (double y in new[] { bbox.Min.Y, bbox.Max.Y })
            foreach (double z in new[] { bbox.Min.Z, bbox.Max.Z })
            {
                XYZ corner = t.OfPoint(new XYZ(x, y, z));
                min = min == null ? corner : new XYZ(System.Math.Min(min.X, corner.X), System.Math.Min(min.Y, corner.Y), System.Math.Min(min.Z, corner.Z));
                max = max == null ? corner : new XYZ(System.Math.Max(max.X, corner.X), System.Math.Max(max.Y, corner.Y), System.Math.Max(max.Z, corner.Z));
            }
        }

        // "선택 항목만 표시" - Revit의 임시 숨기기/격리(뷰에 영구히 반영되지 않는, "적용" 전까지는 이 뷰를
        // 다시 열 때 초기화되는 임시 상태)를 그대로 쓴다. 매번 새로 호출하면 이전 격리 대상을 이번 체크
        // 목록으로 갈아치운다.
        private void ExecuteIsolate(UIApplication app, List<ElementId> ids)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null || !IsTargetDocument(uidoc.Document))
            {
                WarningPickWindow.Instance?.ShowDocumentMismatch();
                return;
            }
            Document doc = uidoc.Document;
            View? view = doc.ActiveView;
            if (view == null) return;

            List<ElementId> validIds = ids.Where(id => doc.GetElement(id) != null).ToList();
            if (validIds.Count == 0)
            {
                TaskDialog.Show("경고Pick", "선택한 요소를 더 이상 찾을 수 없습니다 (이미 삭제되었을 수 있습니다).");
                return;
            }

            using (Transaction tx = new Transaction(doc, "경고Pick: 선택 항목만 표시"))
            {
                tx.Start();
                view.IsolateElementsTemporary(validIds);
                tx.Commit();
            }

            uidoc.Selection.SetElementIds(validIds);
        }

        // "격리 해제" - IsolateElementsTemporary로 켠 임시 격리를 원래대로 되돌린다(모델 자체는 전혀
        // 건드리지 않는 뷰 전용 임시 상태라 "적용"하지 않았다면 언제든 되돌릴 수 있다).
        private void ExecuteResetIsolate(UIApplication app)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null || !IsTargetDocument(uidoc.Document))
            {
                WarningPickWindow.Instance?.ShowDocumentMismatch();
                return;
            }
            Document doc = uidoc.Document;
            View? view = doc.ActiveView;
            if (view == null || !view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate)) return;

            using (Transaction tx = new Transaction(doc, "경고Pick: 격리 해제"))
            {
                tx.Start();
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
            }
        }

        private void ExecuteRefresh(UIApplication app)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null || !IsTargetDocument(uidoc.Document))
            {
                WarningPickWindow.Instance?.ShowDocumentMismatch();
                return;
            }

            List<WarningPickTypeGroup> typeGroups = WarningPickTypeGroup.BuildTypeGroups(uidoc.Document, uidoc.Document.GetWarnings());
            WarningPickWindow.Instance?.ApplyRefreshedTypeGroups(typeGroups);
        }

        public string GetName() => "WallSplitter 경고Pick";
    }
}
