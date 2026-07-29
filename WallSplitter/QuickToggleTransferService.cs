using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 커스텀 버튼 내보내기/가져오기가 "이름만 다시 찾기"에 그치지 않고, 대상 문서에 없는 뷰템플릿/필터를
    // 실제로 복사해 가져올 수 있게 해주는 순수 Revit API 로직 (2026-07-30 추가, 사용자 요청 - "설정해둔
    // 버튼에 해당하는 대상(예를 들면 xxx필터, 작업세트)을 함께 내보내고 가져오기가 되었으면 좋겠다").
    // JSON 파일을 통한 이식은 소스 문서 자체가 없어(파일엔 이름 문자열만 있음) 여기 있는 복사 기능을 쓸 수
    // 없다 - 실제 요소 복사는 같은 Revit 세션에 열려 있는 다른 문서와 주고받는 "모델로 내보내기/가져오기"
    // 경로에서만 의미가 있다(QuickToggleSettingsWindow.TransferButtons 참고). 카테고리는 여기서 다루지
    // 않는다 - Revit 카테고리는 고정된 분류 체계라 "복사"할 대상이 아니라 이름으로 다시 찾는 것만 가능하다.
    internal static class QuickToggleTransferService
    {
        // 소스 문서의 요소 하나(뷰템플릿 또는 필터)를 대상 문서로 복사한다. overwriteExistingId가 있으면
        // 먼저 그 기존 요소를 지우고 복사한다 - 호출자가 이미 "덮어쓰기"를 사용자에게 확인받은 뒤에만
        // 넘겨야 한다. 대상 문서에 열린 트랜잭션이 있어야 한다(호출자 책임 - 이 프로젝트의 다른 Revit API
        // 로직들과 같은 계약).
        //
        // **라이브 테스트 필요**: 뷰템플릿(IsTemplate=true인 View)이 ElementTransformUtils.CopyElements로
        // 다른 문서에 정상적으로 복사되는지는 이 개발 환경에서 실행 중인 Revit이 없어 검증하지 못했다 -
        // 필터(ParameterFilterElement)는 이 방식으로 문서 간 복사되는 사례가 흔하지만, 뷰템플릿은 Revit의
        // "프로젝트 표준 전송" 기능이 내부적으로 같은 API를 쓰는지 확인할 방법이 없었다. 실패하면 null을
        // 반환하도록 방어했으니 최소한 예외로 창이 죽지는 않지만, 실제 동작 여부는 라이브 확인이 필요하다.
        public static ElementId? CopyNamedElement(Document sourceDoc, ElementId sourceId, Document targetDoc, ElementId? overwriteExistingId)
        {
            if (overwriteExistingId != null)
            {
                try { targetDoc.Delete(overwriteExistingId); }
                catch { /* 삭제 실패해도 복사는 시도한다 - 실패하면 이름이 겹친 채로 남아 CopyElements가
                           자동으로 구분된 이름(예: "이름 2")을 만들 수 있고, 그러면 사용자가 기대한 이름과
                           달라지지만 최소한 가져오기 자체는 계속 진행된다 */ }
            }
            try
            {
                ICollection<ElementId> copied = ElementTransformUtils.CopyElements(
                    sourceDoc, new List<ElementId> { sourceId }, targetDoc, null, new CopyPasteOptions());
                return copied.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // 작업세트는 뷰템플릿/필터와 달리 "복사"할 내용이 없다 - 이름 하나가 전부라, 대상 문서에 같은
        // 이름이 없으면 새로 만들기만 하면 충분하다(덮어쓰기 개념 자체가 없음 - 이미 있으면 그냥 그걸
        // 쓴다). 대상 문서가 작업공유(워크셰어링) 상태가 아니면 애초에 작업세트를 만들 수 없어 null.
        public static int? EnsureWorkset(Document targetDoc, string name)
        {
            if (!targetDoc.IsWorkshared) return null;

            Workset? existing = new FilteredWorksetCollector(targetDoc).OfKind(WorksetKind.UserWorkset)
                .FirstOrDefault(w => w.Name == name);
            if (existing != null) return existing.Id.IntegerValue;

            try { return Workset.Create(targetDoc, name).Id.IntegerValue; }
            catch { return null; }
        }
    }
}
