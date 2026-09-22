using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;
using RevitDocument = Autodesk.Revit.DB.Document;

namespace WallSplitter
{
    // Dynamo로 같은 일(여러 매개변수를 합쳐 하나에 기입)을 하면 값이 바뀔 때마다 사람이 다시 Run을 눌러야
    // 한다. 이 애드인이 대신 쓰는 것은 Revit의 Dynamic Model Update - IUpdater를 등록해 두면 사용자가
    // 소스 매개변수를 바꾼 **바로 그 트랜잭션 안에서** Revit이 우리를 불러주고, 우리가 거기서 결과 매개변수를
    // 다시 계산해 써넣는다. 그래서 사용자 눈에는 "번호를 001에서 002로 고치는 순간 실번호도 따라 바뀐" 것처럼
    // 보이고, Ctrl+Z 한 번이면 둘 다 같이 되돌아간다(같은 트랜잭션이므로).
    //
    // 주의해서 지켜야 하는 것들:
    // - Execute 안에서 트랜잭션을 새로 열면 안 된다. 이미 Revit의 트랜잭션 안이다.
    // - Execute에서 예외가 밖으로 새어나가면 Revit이 "업데이터 실패" 대화상자를 띄우거나 업데이터를 꺼버린다.
    //   그래서 전부 try로 감싼다(App.cs의 다른 Revit 이벤트 콜백들과 같은 방침).
    // - 우리가 쓴 값이 다시 우리를 부르는 무한 루프를 막는 장치가 두 겹 있다: 재진입 플래그와,
    //   "값이 이미 같으면 아무것도 쓰지 않는다"는 ParamCombineEngine.Apply의 조기 종료.
    internal sealed class ParamCombineUpdater : IUpdater
    {
        // 한 번 정하면 절대 바꾸지 않는다 - 이 GUID가 곧 모델에 기록되는 업데이터 식별자다.
        private static readonly Guid UpdaterGuid = new Guid("7C3B1E42-8F5A-4C36-9C1E-2B7D9A4E60F1");

        private readonly UpdaterId _id;

        internal static ParamCombineUpdater? Instance { get; private set; }

        private static bool _running;

        private ParamCombineUpdater(AddInId addInId)
        {
            _id = new UpdaterId(addInId, UpdaterGuid);
        }

        // ===== IUpdater =====

        public void Execute(UpdaterData data)
        {
            // 우리가 쓴 변경 때문에 Revit이 같은 패스에서 우리를 다시 부르는 경우를 막는다.
            if (_running) return;
            _running = true;

            try
            {
                ParamCombineSettings settings = ParamCombineSettings.Current;
                if (!settings.AutoUpdate) return;

                RevitDocument doc = data.GetDocument();
                if (doc == null || doc.IsReadOnly) return;

                List<CombineRule> rules = settings.ActiveRules.ToList();
                if (rules.Count == 0) return;

                foreach (ElementId id in ChangedIds(data))
                {
                    Element? element;
                    try { element = doc.GetElement(id); }
                    catch { continue; }

                    if (element?.Category == null) continue;

                    int categoryId;
                    try { categoryId = element.Category.Id.ToInt(); }
                    catch { continue; }

                    foreach (CombineRule rule in rules)
                    {
                        if (rule.CategoryId != categoryId) continue;
                        try { ParamCombineEngine.Apply(element, rule); }
                        catch { /* 요소 하나가 실패해도 나머지는 계속 처리한다. */ }
                    }
                }
            }
            catch
            {
                // 예외를 밖으로 내보내지 않는다(위 클래스 주석 참고).
            }
            finally
            {
                _running = false;
            }
        }

        private static IEnumerable<ElementId> ChangedIds(UpdaterData data)
        {
            List<ElementId> ids = new List<ElementId>();
            try { ids.AddRange(data.GetModifiedElementIds()); } catch { }
            try { ids.AddRange(data.GetAddedElementIds()); } catch { }
            return ids;
        }

        public string GetAdditionalInformation() =>
            "Sunny Tools - 여러 매개변수를 정해진 자리수로 합쳐 결과 매개변수에 자동으로 기입합니다.";

        public ChangePriority GetChangePriority() => ChangePriority.Annotations;

        public UpdaterId GetUpdaterId() => _id;

        public string GetUpdaterName() => "Sunny Tools 자동 결합";

        // ===== 등록/트리거 =====

        // App.OnStartup에서 한 번만 부른다. 트리거(어떤 문서의 어떤 카테고리를 감시할지)는 문서 단위라
        // 여기서는 업데이터 자체만 등록하고, 실제 감시는 문서가 열릴 때/설정이 바뀔 때 RefreshTriggers가 붙인다.
        internal static void Register(AddInId addInId)
        {
            if (Instance != null || addInId == null) return;

            try
            {
                ParamCombineUpdater updater = new ParamCombineUpdater(addInId);
                // isOptional = true: 이 애드인이 설치되지 않은 PC에서 그 모델을 열어도 Revit이
                // "업데이터를 찾을 수 없다"고 항의하지 않게 한다.
                UpdaterRegistry.RegisterUpdater(updater, true);
                Instance = updater;
            }
            catch
            {
                // 등록에 실패해도 애드인 로드 자체는 막지 않는다 - 자동 갱신만 못 쓰고 "전체 갱신"은 그대로 된다.
            }
        }

        internal static void Unregister()
        {
            if (Instance == null) return;
            try { UpdaterRegistry.UnregisterUpdater(Instance.GetUpdaterId()); }
            catch { /* 종료 중이라 실패해도 알릴 곳이 없다. */ }
            Instance = null;
        }

        // 한 문서의 트리거를 현재 설정대로 다시 붙인다(문서를 열었을 때, 설정을 저장했을 때).
        //
        // 트리거를 Element.GetChangeTypeParameter(특정 매개변수)로 좁히지 않고 GetChangeTypeAny로 두는 이유:
        // 매개변수별 트리거를 걸려면 그 매개변수의 ElementId가 필요한데, 규칙은 "이름"으로만 적혀 있고
        // 내장 매개변수/공유 매개변수/프로젝트 매개변수가 섞여 들어올 수 있으며, 사용자가 설정 창에서
        // 아직 존재하지 않는 이름을 적어둘 수도 있다. 하나라도 해석에 실패하면 그 매개변수 변경을 조용히
        // 놓치게 되는데, 그러면 "실시간 연동"이라는 기능의 핵심이 무너진다. GetChangeTypeAny는 그 카테고리
        // 요소가 바뀔 때마다 불려 조금 더 자주 돌지만, Apply가 "값이 같으면 즉시 반환"이라 비용이 거의 없다.
        internal static void RefreshTriggers(RevitDocument doc)
        {
            if (Instance == null || doc == null) return;
            if (doc.IsFamilyDocument || doc.IsLinked) return;

            UpdaterId id = Instance.GetUpdaterId();

            try { UpdaterRegistry.RemoveDocumentTriggers(id, doc); }
            catch { /* 붙어 있는 트리거가 없으면 실패할 수 있다 - 아래에서 새로 붙이면 된다. */ }

            ParamCombineSettings settings = ParamCombineSettings.Current;
            if (!settings.AutoUpdate) return;

            foreach (int categoryId in settings.ActiveRules.Select(r => r.CategoryId).Distinct())
            {
                try
                {
                    ElementCategoryFilter filter = new ElementCategoryFilter(new ElementId(categoryId));
                    UpdaterRegistry.AddTrigger(id, doc, filter, Element.GetChangeTypeAny());
                    UpdaterRegistry.AddTrigger(id, doc, filter, Element.GetChangeTypeElementAddition());
                }
                catch
                {
                    // 그 카테고리만 감시가 안 될 뿐 나머지 규칙은 계속 동작한다.
                }
            }
        }

        // 설정을 저장했거나 리본 토글을 눌렀을 때 - 지금 열려 있는 모든 문서에 다시 반영한다.
        internal static void RefreshTriggers(RevitApplication app)
        {
            if (app == null) return;
            try
            {
                foreach (RevitDocument doc in app.Documents)
                    RefreshTriggers(doc);
            }
            catch
            {
                // 문서 목록 조회 실패는 무시 - 다음에 문서를 열 때 DocumentOpened에서 다시 붙는다.
            }
        }
    }
}
