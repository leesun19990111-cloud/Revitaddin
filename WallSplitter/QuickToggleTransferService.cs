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
        // CONFIRMED 코드 결함, 사용자가 "뷰템플릿과 필터가 복사가 안 되는거야?"라고 재차 물어봐서 웹 검색
        // (Jeremy Tammik/thebuildingcoder, Autodesk 공식 문서, Autodesk Community 포럼)으로 원인을 찾아
        // 고쳤다(2026-07-30): 뷰템플릿/필터는 자기 자신뿐 아니라 선패턴·채우기 패턴·재료 같은 "타입"
        // 요소도 함께 참조하는데, 그 타입들이 이름이 같은 채로 대상 문서에 이미 있으면 Revit이 기본적으로
        // "같은 이름의 타입이 있습니다 - 기존 걸 쓸지, 새로 복제할지" 확인이 필요한 상황으로 판단한다.
        // `CopyPasteOptions`에 `IDuplicateTypeNamesHandler`를 지정하지 않으면 이 판단을 자동으로 처리할
        // 방법이 없어(사용자가 지켜보며 Revit이 띄우는 대화상자를 눌러줘야 하는 대화형 시나리오를 전제로
        // 설계된 API라, 이 창의 코드처럼 자동으로 실행되는 문맥에선 그 대화상자 자체가 뜨지 않거나 예외로
        // 이어질 수 있다) - `CopyElements` 호출 자체가 조용히 실패하거나 아무것도 복사하지 않은 채 넘어가는
        // 원인이었을 것으로 보인다. `AutoUseDestinationTypesHandler`를 지정해 "이름이 같은 하위 타입은
        // 항상 대상 문서에 있는 걸 그대로 쓴다"로 자동 결정하도록 고쳤다.
        private sealed class AutoUseDestinationTypesHandler : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) =>
                DuplicateTypeAction.UseDestinationTypes;
        }

        // 소스 문서의 요소 하나(뷰템플릿 또는 필터)를 대상 문서로 복사한다. overwriteExistingId가 있으면
        // 먼저 그 기존 요소를 지우고 복사한다 - 호출자가 이미 "덮어쓰기"를 사용자에게 확인받은 뒤에만
        // 넘겨야 한다. 대상 문서에 열린 트랜잭션이 있어야 한다(호출자 책임 - 이 프로젝트의 다른 Revit API
        // 로직들과 같은 계약).
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
                CopyPasteOptions options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new AutoUseDestinationTypesHandler());
                ICollection<ElementId> copied = ElementTransformUtils.CopyElements(
                    sourceDoc, new List<ElementId> { sourceId }, targetDoc, null, options);
                return copied.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // 복사 중 Revit이 던질 수 있는 경고(예: 뷰템플릿/필터가 참조하는 하위 요소 관련 경고)가 대화상자로
        // 뜨는 것을 막는다 - 대화형 사용을 전제로 한 API를 이 창의 자동 실행 문맥에서 쓰다 보니, 경고
        // 대화상자가 뜨면 사용자가 못 보고 지나칠 수 있고 최악의 경우 조용히 멈춘 것처럼 보일 수 있다.
        // 오류(Error)는 그대로 두어 트랜잭션이 정상적으로 실패하게 한다 - 경고만 무시한다.
        public sealed class SilentWarningsPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (FailureMessageAccessor f in failuresAccessor.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning)
                        failuresAccessor.DeleteWarning(f);
                }
                return FailureProcessingResult.Continue;
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
