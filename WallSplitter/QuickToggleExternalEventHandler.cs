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

        // 2026-07-28, "모델/주석/해석모델/가져온카테고리/뷰자르기 및 범위까지 전부 기억해야 한다"는 요청으로
        // 확장 - V/G 대화상자의 카테고리 표시 체크박스는 탭(모델/주석/해석모델/가져온 카테고리)과 무관하게
        // 전부 doc.Settings.Categories 트리 하나로 노출되므로(View.GetCategoryHidden/SetCategoryHidden),
        // 탭별로 따로 나누지 않고 카테고리 전체를 순회해 한 번에 담는다 (QuickToggleService.CaptureViewState).
        public Dictionary<int, bool> CategoryHidden { get; set; } = new(); // true = Hidden
        public bool CropBoxActive { get; set; }
        public bool CropBoxVisible { get; set; }
        public BoundingBoxXYZ? CropBox { get; set; }
        public PlanViewRange? PlanViewRange { get; set; } // 평면 뷰가 아니면 null (View Range는 평면 뷰 전용)

        // 2026-07-28, "가시성/그래픽설정, 그래픽 화면표시 옵션, 색상표, 그림자, 태양경로, 뷰템플릿,
        // 상세수준, 비주얼스타일, 뷰자르기, 단면상자, 렌더링설정, 투영모드, 뷰범위 등등 뷰에 표시되는
        // 모든 요소" 요청으로 확장. 전부 이 스냅샷이 디스크로 나가지 않고 메모리에만 머무르므로(같은
        // 문서 내에서만 재사용됨), CropBox/PlanViewRange가 이미 그렇듯 원시 값으로 풀어내지 않고 Revit
        // API 객체를 가공 없이 그대로 담는다.
        public Dictionary<int, OverrideGraphicSettings> CategoryOverrides { get; set; } = new(); // "가시성/그래픽설정"(V/G 재정의) - 카테고리별
        public Dictionary<int, OverrideGraphicSettings> FilterOverrides { get; set; } = new(); // 필터별 그래픽 재정의(표시 여부와 별개)
        public Dictionary<int, ElementId> ColorFillSchemeId { get; set; } = new(); // "색상표" - 카테고리별
        public ViewDetailLevel? DetailLevel { get; set; } // "상세수준"
        public Autodesk.Revit.DB.DisplayStyle? DisplayStyle { get; set; } // "비주얼스타일"

        // View3D 전용 (평면/입면/단면 등은 전부 null로 남는다)
        public bool? SectionBoxActive { get; set; } // "단면상자"
        public BoundingBoxXYZ? SectionBox { get; set; }
        public bool? IsPerspective { get; set; } // "투영모드"
        public ViewOrientation3D? Orientation { get; set; } // 카메라 위치/방향
        public RenderingSettings? RenderingSettings { get; set; } // "렌더링설정"

        // 그림자/태양경로/스케치라인 등 "그래픽 화면표시 옵션" 대화상자의 개별 항목은 Revit 공개 API에
        // 전용 getter/setter가 없는 것으로 알려져 있다 - 최선 노력으로, PG_GRAPHICS 그룹에 속하는 정수형
        // (예/아니오류) 뷰 파라미터를 이름 기준으로 전부 캡처/복원한다. 설치된 Revit 버전이 실제로 이
        // 값들을 파라미터로 노출하는지는 라이브 테스트로만 확인 가능 - 안 되는 항목이 있으면
        // docs/quick-toggle/CLAUDE.md의 이 항목부터 확인할 것.
        public Dictionary<string, int> GraphicsIntegerParams { get; set; } = new();
    }

    // "색상 버튼" 팝업(ColorToolPopupWindow)이 색상 팔레트/투명도 슬라이더를 조작할 때마다 보내는 요청 -
    // 2026-07-29 추가. Document/View를 담지 않고 카테고리 Id 목록 + 이번에 바뀐 값만 담는다 - 어느
    // 문서/뷰에 적용할지는 Execute 시점에 항상 "그때의 활성 뷰"를 다시 조회한다(팝업을 연 뒤 사용자가
    // 다른 뷰로 전환했을 수도 있으므로, 팝업을 열 때 캡처해둔 뷰가 아니라 실행 시점 기준으로 적용).
    internal class ColorToolApplyRequest
    {
        public List<int> CategoryIds { get; set; } = new();
        public int? Color { get; set; } // 0xRRGGBB, null이면 이번 요청은 색상을 건드리지 않음
        public int? Transparency { get; set; } // 0~100, null이면 이번 요청은 투명도를 건드리지 않음
        // true면 Color/Transparency는 무시하고 대상 카테고리의 그래픽 재정의 자체를 완전히 비운다
        // ("재지정 지우기" 버튼, 2026-07-29 추가).
        public bool Clear { get; set; }
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

        // "색상 버튼" 팝업의 실시간 조작 요청 (2026-07-29 추가) - PendingRevertSnapshot과 같은 방식으로
        // 같은 ExternalEvent를 재사용한다.
        internal ColorToolApplyRequest? PendingColorApply { get; set; }

        // "기능 버튼" 클릭 요청 (2026-08-03 추가) - 위 둘과 같은 방식으로 같은 ExternalEvent를 재사용한다.
        // 버튼 설정 자체(어느 명령을 실행할지)만 있으면 되므로 cfg 참조를 그대로 담는다.
        internal QuickToggleButtonConfig? PendingCommandLaunch { get; set; }

        public void Execute(UIApplication app)
        {
            if (PendingRevertSnapshot != null)
            {
                ViewStateSnapshot snapshot = PendingRevertSnapshot;
                PendingRevertSnapshot = null;
                ExecuteRevert(app, snapshot);
                return;
            }

            if (PendingColorApply != null)
            {
                ColorToolApplyRequest request = PendingColorApply;
                PendingColorApply = null;
                ExecuteColorApply(app, request);
                return;
            }

            if (PendingCommandLaunch != null)
            {
                QuickToggleButtonConfig launchCfg = PendingCommandLaunch;
                PendingCommandLaunch = null;
                if (!QuickToggleService.RunCommand(app, launchCfg))
                {
                    TaskDialog.Show("커스텀 버튼",
                        $"'{launchCfg.Name}' 기능을 실행하지 못했습니다 (지금 상황에서 사용할 수 없는 기능일 수 있습니다).");
                }
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
            using (Transaction tx = new Transaction(doc, "커스텀 버튼: " + cfg.Name))
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
                TaskDialog.Show("커스텀 버튼",
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
                TaskDialog.Show("커스텀 버튼", "저장했던 뷰를 찾을 수 없습니다 (그 사이 삭제되었을 수 있습니다).");
                return;
            }

            // 저장했던 뷰가 지금 활성 뷰와 다르면 먼저 그 뷰로 전환한다 - "여러 번 뷰가 바뀌더라도
            // 되돌리기를 누르면 저장했던 뷰로 돌아간다"는 요청사항.
            if (doc.ActiveView == null || doc.ActiveView.Id != targetView.Id)
            {
                try { uidoc.ActiveView = targetView; }
                catch
                {
                    TaskDialog.Show("커스텀 버튼", "저장했던 뷰로 전환하지 못했습니다.");
                    return;
                }
            }

            using (Transaction tx = new Transaction(doc, "커스텀 버튼: 뷰 상태 되돌리기"))
            {
                tx.Start();
                QuickToggleService.RestoreViewState(targetView, snapshot);
                tx.Commit();
            }

            QuickToggleToolbar.Instance?.RefreshState();
        }

        // "색상 버튼" 실행 - 항상 그 순간의 활성 뷰를 다시 조회한다(팝업을 열어둔 채 사용자가 다른 뷰로
        // 전환했을 수 있으므로, 팝업이 열릴 때 캡처해둔 뷰가 아니라 지금 활성 뷰 기준으로 적용).
        private static void ExecuteColorApply(UIApplication app, ColorToolApplyRequest request)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            Document? doc = uidoc?.Document;
            View? view = doc?.ActiveView;
            if (doc == null || view == null) return;

            using (Transaction tx = new Transaction(doc, request.Clear ? "커스텀 버튼: 재지정 지우기" : "커스텀 버튼: 색상/투명도 지정"))
            {
                tx.Start();
                if (request.Clear)
                    QuickToggleService.ClearColorTool(view, request.CategoryIds);
                else
                    QuickToggleService.ApplyColorTool(view, request.CategoryIds, request.Color, request.Transparency);
                tx.Commit();
            }
        }

        public string GetName() => "WallSplitter 커스텀 버튼";
    }
}
