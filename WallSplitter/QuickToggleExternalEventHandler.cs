using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 커스텀 툴바(QuickToggleToolbar)는 세션 내내 떠 있는 모드리스 창이라 버튼 클릭이 언제든 일어날 수
    // 있고, 그 시점엔 유효한 Revit API 컨텍스트(트랜잭션 등)가 열려있지 않다. ExternalEvent.Raise()로
    // 요청을 넣어두면 Revit이 다음 기회에 Execute를 유효한 컨텍스트에서 실행해준다.
    // 이 프로젝트에서 ExternalEvent를 쓰는 첫 사례 - 기존 창들은 전부 커맨드 Execute 안에서 여는
    // 모달 ShowDialog()라 이런 비동기 콜백이 필요 없었다.
    public class QuickToggleExternalEventHandler : IExternalEventHandler
    {
        public string? PendingButtonId { get; set; }
        public bool PendingTurnOn { get; set; }

        public void Execute(UIApplication app)
        {
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

        public string GetName() => "WallSplitter 빠른 토글";
    }
}
