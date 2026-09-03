using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{

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

    // "링크된 모델" 팝업(LinkedModelPopupWindow)에서 링크 줄이나 전체 켜기/끄기를 눌렀을 때 보내는 요청
    // (2026-09-02 추가). 링크 인스턴스 ElementId는 문서마다 다른 값이라, 팝업을 열어둔 채 다른 프로젝트로
    // 전환한 경우 엉뚱한 요소에 적용되지 않도록 문서 경로를 같이 담아 실행 시점에 대조한다.
    internal class LinkedModelApplyRequest
    {
        public string SourceDocumentPath { get; set; } = "";
        public List<int> InstanceIds { get; set; } = new();
        public bool Visible { get; set; }
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

        // true면 이번 Raise()는 "빠른 토글 버튼 켜기/끄기"가 아니라 "설정 창 열기" 요청이다
        // (2026-09-03, 툴바 우측 끝 톱니바퀴 버튼) - 같은 ExternalEvent를 재사용해 별도 배선 없이 처리한다.
        internal bool PendingOpenSettings { get; set; }

        // "색상 버튼" 팝업의 실시간 조작 요청 (2026-07-29 추가) - 위와 같은 방식으로
        // 같은 ExternalEvent를 재사용한다.
        internal ColorToolApplyRequest? PendingColorApply { get; set; }

        // "기능 버튼" 클릭 요청 (2026-08-03 추가) - 위 둘과 같은 방식으로 같은 ExternalEvent를 재사용한다.
        // 버튼 설정 자체(어느 명령을 실행할지)만 있으면 되므로 cfg 참조를 그대로 담는다.
        internal QuickToggleButtonConfig? PendingCommandLaunch { get; set; }

        // "링크된 모델" 팝업에서 보낸 개별/전체 링크 표시 요청 (2026-09-02 추가).
        internal LinkedModelApplyRequest? PendingLinkedModelApply { get; set; }

        // ExternalEvent 콜백에서 예외가 밖으로 나가면 Revit은 사용자에게 아무 것도 보여주지 않고
        // "버튼을 눌러도 반응이 없다"는 증상으로만 남는다(이 파일의 아래 주석과 같은 이유).
        // 이미 개별 실패는 TaskDialog로 알리고 있으므로, 예상 못 한 예외도 여기서 같은 방식으로 알린다.
        public void Execute(UIApplication app)
        {
            try
            {
                ExecuteCore(app);
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("커스텀 버튼", "요청을 실행하지 못했습니다.\n\n" + ex.GetBaseException().Message);
            }
        }

        private void ExecuteCore(UIApplication app)
        {
            if (PendingOpenSettings)
            {
                PendingOpenSettings = false;
                ExecuteOpenSettings(app);
                return;
            }

            if (PendingColorApply != null)
            {
                ColorToolApplyRequest request = PendingColorApply;
                PendingColorApply = null;
                ExecuteColorApply(app, request);
                return;
            }

            if (PendingLinkedModelApply != null)
            {
                LinkedModelApplyRequest linkRequest = PendingLinkedModelApply;
                PendingLinkedModelApply = null;
                ExecuteLinkedModelApply(app, linkRequest);
                return;
            }

            if (PendingCommandLaunch != null)
            {
                QuickToggleButtonConfig launchCfg = PendingCommandLaunch;
                PendingCommandLaunch = null;
                if (!QuickToggleService.RunCommand(app, launchCfg, out string failureReason))
                {
                    TaskDialog.Show("커스텀 버튼",
                        $"'{launchCfg.Name}' 기능을 실행하지 못했습니다.\n\n{failureReason}");
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
                    $"'{cfg.Name}' 버튼을 반영하지 못했습니다 (예: 대상 뷰템플릿이 이 뷰 종류와 호환되지 않거나, " +
                    "뷰 템플릿이 이 뷰의 가시성/그래픽 설정을 제어하고 있음).");
            }
        }

        // "설정"(톱니바퀴) 버튼 실행 - 리본의 QuickToggleSettingsCommand가 하는 것과 똑같이 설정 창을
        // 모달로 연다. 툴바는 모드리스라 클릭 시점엔 유효한 API 컨텍스트가 없지만 여기(ExternalEvent
        // 콜백)는 유효하므로, 창 생성자가 FilteredElementCollector로 문서를 읽어도 안전하다.
        private static void ExecuteOpenSettings(UIApplication app)
        {
            Document? doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show("커스텀 버튼", "먼저 프로젝트 파일을 여세요.");
                return;
            }

            QuickToggleSettingsWindow window = new QuickToggleSettingsWindow(doc);
            // Revit 메인 창을 소유자로 지정 - 창이 Revit 뒤로 숨거나 작업 표시줄에 따로 뜨는 것을 방지
            // (QuickToggleSettingsCommand와 같은 처리).
            new System.Windows.Interop.WindowInteropHelper(window) { Owner = app.MainWindowHandle };
            window.ShowDialog();
            // 저장했다면 창 자신이 ForceReloadSettings/RefreshState를 이미 호출한다 - 취소했을 때를
            // 위해 여기서 한 번 더 부르지는 않는다(불필요한 재빌드로 클릭이 씹히는 걸 피하려는 이 파일의 방침).
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

        // "링크된 모델" 팝업의 요청 실행 (2026-09-02 추가) - 색상 버튼과 마찬가지로 실행 시점의 활성 뷰에
        // 적용하되, ElementId는 문서마다 다른 값이라 팝업을 연 문서와 지금 활성 문서가 같은지 먼저 대조한다.
        private static void ExecuteLinkedModelApply(UIApplication app, LinkedModelApplyRequest request)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            Document? doc = uidoc?.Document;
            View? view = doc?.ActiveView;
            if (doc == null || view == null) return;

            if (!string.Equals(doc.PathName ?? "", request.SourceDocumentPath ?? "", System.StringComparison.OrdinalIgnoreCase))
            {
                TaskDialog.Show("커스텀 버튼",
                    "링크 목록을 연 문서와 현재 활성 문서가 다릅니다. 현재 문서에서 버튼을 다시 눌러 주세요.");
                return;
            }

            bool applied;
            TransactionStatus status;
            using (Transaction tx = new Transaction(doc, request.Visible ? "커스텀 버튼: 링크된 모델 켜기" : "커스텀 버튼: 링크된 모델 끄기"))
            {
                tx.Start();
                applied = QuickToggleService.SetLinkedModelsVisible(view, request.InstanceIds, request.Visible);
                status = applied ? tx.Commit() : tx.RollBack();
            }

            QuickToggleToolbar.Instance?.RefreshState();
            // 팝업의 켜짐/꺼짐 표시는 요청을 보낸 직후가 아니라 실제로 반영된 지금 다시 읽어야 모델과
            // 어긋나지 않는다(ExternalEvent는 비동기라 Raise() 직후엔 아직 아무것도 바뀌지 않았다).
            QuickToggleToolbar.Instance?.RefreshLinkedModelPopup(view);

            if (!applied || status != TransactionStatus.Committed)
            {
                TaskDialog.Show("커스텀 버튼",
                    "링크된 모델의 표시 상태를 바꾸지 못했습니다 (뷰 템플릿이 이 뷰의 가시성/그래픽 설정을 제어하고 있거나, " +
                    "이 뷰에서 숨길 수 없는 링크일 수 있습니다).");
            }
        }

        public string GetName() => "WallSplitter 커스텀 버튼";
    }
}
