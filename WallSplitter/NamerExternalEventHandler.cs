using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // NAMER는 2026-09-21부터 **모드리스**다(그 전에는 ShowDialog 모달). 사용자 요청:
    // *"지금 NAMER가 모달창이라면, 네이머를 띄워두어도 다른작업이 병행가능한 창으로 변경해서라도
    // 특성버튼을 누르면 레빗 자체의 유형편집이나 특성창을 띄울 수 있도록 했으면 좋겠어."*
    //
    // **이 전환이 필요했던 이유**: Revit 기본 대화상자를 여는 유일한 공개 수단인
    // `UIApplication.PostCommand`는 "지금 실행 중인 명령이 끝난 뒤(Idle)"에 실행된다. 모달 창은
    // `IExternalCommand.Execute` 안에서 블록하고 있으므로, 창이 떠 있는 동안에는 그 Idle이 오지 않아
    // 기본 창이 **영영 열리지 않는다**. 모드리스로 바꿔 Execute가 즉시 반환되면 비로소 열린다.
    //
    // 대신 창이 떠 있는 동안에는 유효한 API 컨텍스트가 없으므로, 모델을 건드리는 일(이름 변경)과
    // 선택/뷰 전환/PostCommand는 전부 이 ExternalEvent를 거쳐야 한다
    // (WarningPickExternalEventHandler / QuickToggleExternalEventHandler와 같은 패턴).
    public class NamerExternalEventHandler : IExternalEventHandler
    {
        internal sealed class PropertyRequest
        {
            public NamerWindow.NamerCategory Category;
            public ElementId Id = ElementId.InvalidElementId;
            public string Name = "";
        }

        // 목록을 만든 시점의 문서. 창이 떠 있는 동안 사용자가 다른 문서로 전환했으면 거부하고 안내한다 -
        // ElementId는 문서마다 독립적이라 같은 정수가 다른 문서에서는 전혀 다른 요소를 가리킨다.
        internal Document? TargetDocument { get; set; }

        // Document는 API 래퍼 객체라 같은 열린 문서라도 조회 시점이 다르면 참조가 다를 수 있다 -
        // ReferenceEquals로 비교하면 "활성 문서가 아니다"가 항상 뜬다(경고Pick에서 실제로 겪은 버그).
        private static string DocKey(Document doc) => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;

        private bool IsTargetDocument(Document doc) => TargetDocument != null && DocKey(doc) == DocKey(TargetDocument);

        internal List<(ElementId Id, string NewName)>? PendingRenames { get; set; }
        internal bool PendingMergeDuplicateMaterials { get; set; }
        internal PropertyRequest? PendingProperties { get; set; }

        // ExternalEvent 콜백에서 예외가 새어 나가면 Revit은 아무 것도 보여주지 않는다 - 사용자에게는
        // "버튼을 눌러도 반응이 없다"로만 보인다(커스텀 버튼에서 실제로 겪은 문제). 여기서 직접 잡아 알린다.
        public void Execute(UIApplication app)
        {
            try
            {
                ExecuteCore(app);
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("NAMER", "요청을 실행하지 못했습니다.\n\n" + ex.GetBaseException().Message);
            }
        }

        public string GetName() => "NAMER";

        private void ExecuteCore(UIApplication app)
        {
            if (PendingRenames != null)
            {
                List<(ElementId, string)> renames = PendingRenames;
                bool merge = PendingMergeDuplicateMaterials;
                PendingRenames = null;
                ExecuteRenames(app, renames, merge);
                return;
            }

            if (PendingProperties != null)
            {
                PropertyRequest request = PendingProperties;
                PendingProperties = null;
                ExecuteProperties(app, request);
            }
        }

        private void ExecuteRenames(UIApplication app, List<(ElementId Id, string NewName)> renames, bool merge)
        {
            Document? doc = ResolveDocument(app);
            if (doc == null) return;

            NamerCommand.RenameResult result = NamerCommand.ApplyRenames(doc, renames, merge);
            NamerWindow.Instance?.OnRenamesApplied(result);
        }

        private void ExecuteProperties(UIApplication app, PropertyRequest request)
        {
            Document? doc = ResolveDocument(app);
            if (doc == null) return;

            NamerNativeProperties.Result result = NamerNativeProperties.Open(app, doc, request.Category, request.Id, request.Name);

            // 기본 창을 열 수 없는 경우에만 자체 특성 창으로 넘어간다. **이 창은 여기(ExternalEvent
            // 안)에서 띄워야 한다** - 이 안은 유효한 API 컨텍스트라 그 창이 자체 Transaction을 그대로
            // 열 수 있다. 모드리스 창 쪽에서 직접 띄우면 트랜잭션을 시작할 수 없다.
            if (!result.Opened)
            {
                NamerWindow.Instance?.ShowFallbackProperties(doc, request, result.Failure);
                return;
            }

            NamerWindow.Instance?.ShowStatus(result.Hint ?? "");
        }

        private Document? ResolveDocument(UIApplication app)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("NAMER", "열려 있는 문서가 없습니다.");
                return null;
            }

            if (!IsTargetDocument(uidoc.Document))
            {
                TaskDialog.Show("NAMER",
                    "NAMER 목록을 만든 문서가 더 이상 활성 문서가 아닙니다.\n" +
                    "그 문서를 다시 활성화하거나, NAMER를 닫았다가 다시 실행하세요.");
                return null;
            }

            return uidoc.Document;
        }
    }

    // NAMER의 '특성' 버튼이 **Revit 기본 창**을 여는 방법을 카테고리별로 모아 둔 곳.
    //
    // 카테고리마다 기본 창에 닿는 길이 다르고, 두 가지는 "그 항목을 콕 집어" 열 수가 없다 -
    // 아래 표가 실제로 할 수 있는 것의 전부다(2023~2027 PostableCommand 교집합을 전부 뽑아 확인).
    //
    //   뷰/시트/범례/일람표 → 그 뷰를 활성화하고 선택을 비운다 → Revit 특성 팔레트가 그 뷰의 특성을 보여준다 (정확히 그 항목)
    //   유형                → 그 유형을 쓰는 부재 하나를 선택하고 PostableCommand.TypeProperties      (정확히 그 유형)
    //   패밀리              → 그 패밀리의 유형을 쓰는 부재 하나를 선택하고 TypeProperties              (정확히 그 유형)
    //   뷰 템플릿           → PostableCommand.ManageViewTemplates                                     (목록에서 직접 골라야 함)
    //   재료                → PostableCommand.Materials (재료 브라우저)                                (목록에서 직접 골라야 함)
    //
    // **유형/패밀리는 그 유형을 쓰는 부재가 모델에 하나도 없으면 열 수 없다** - Revit의 유형 특성
    // 명령이 "현재 선택된 부재"를 기준으로 동작하고, 유형 자체는 Selection에 넣을 수 없기 때문이다.
    // 그때만 자체 특성 창으로 넘어간다.
    internal static class NamerNativeProperties
    {
        internal sealed class Result
        {
            public bool Opened;
            public string? Hint;     // 열리긴 했지만 사용자가 목록에서 직접 골라야 할 때의 안내
            public string? Failure;  // 못 열었을 때의 이유 (자체 창으로 넘어간다)

            public static Result Ok(string? hint = null) => new Result { Opened = true, Hint = hint };
            public static Result Fail(string reason) => new Result { Opened = false, Failure = reason };
        }

        // 카테고리 → 어떤 기본 창으로 갈지. **일부러 Revit 타입을 전혀 쓰지 않는 순수 함수로 떼어 놓았다** -
        // 이 프로젝트의 관례대로(CenterShiftFromFaces, RoomSeparatorGeometry) 하네스에서 리플렉션으로
        // 8개 카테고리를 전부 돌려 볼 수 있게 하기 위해서다. 한 칸만 잘못 이어져도(예: 뷰 템플릿이
        // ActivateView로 가면 예외) 라이브에서야 드러나는 종류의 실수라 표로 고정해 두고 검증한다.
        internal enum NativePlan
        {
            ActivateView,          // 뷰/시트/범례/일람표 - 뷰를 열면 특성 팔레트가 그 뷰의 특성을 보여준다
            ManageViewTemplates,   // 뷰 템플릿 - 목록 창까지만 열 수 있다
            MaterialBrowser,       // 재료 - 재료 탐색기까지만 열 수 있다
            TypeProperties,        // 유형 - 그 유형을 쓰는 부재를 선택하고 유형 특성 창
            FamilyTypeProperties,  // 패밀리 - 그 패밀리의 배치된 유형 하나로 유형 특성 창
        }

        internal static NativePlan PlanFor(NamerWindow.NamerCategory category) => category switch
        {
            NamerWindow.NamerCategory.View => NativePlan.ActivateView,
            NamerWindow.NamerCategory.Sheet => NativePlan.ActivateView,
            NamerWindow.NamerCategory.Legend => NativePlan.ActivateView,
            NamerWindow.NamerCategory.Schedule => NativePlan.ActivateView,
            NamerWindow.NamerCategory.ViewTemplate => NativePlan.ManageViewTemplates,
            NamerWindow.NamerCategory.Material => NativePlan.MaterialBrowser,
            NamerWindow.NamerCategory.Type => NativePlan.TypeProperties,
            NamerWindow.NamerCategory.Family => NativePlan.FamilyTypeProperties,
            _ => NativePlan.TypeProperties,
        };

        internal static Result Open(UIApplication app, Document doc, NamerWindow.NamerCategory category,
            ElementId id, string name)
        {
            Element? el = doc.GetElement(id);
            if (el == null) return Result.Fail("이 항목을 모델에서 더 이상 찾을 수 없습니다.");

            switch (PlanFor(category))
            {
                case NativePlan.ActivateView:
                    return ActivateView(app, el, name);

                case NativePlan.ManageViewTemplates:
                    return PostBrowser(app, PostableCommand.ManageViewTemplates,
                        $"Revit '뷰 템플릿' 창에서 '{name}'을(를) 고르세요. (뷰 템플릿은 뷰처럼 열 수 없어 특정 항목을 바로 지정할 수 없습니다)");

                case NativePlan.MaterialBrowser:
                    return PostBrowser(app, PostableCommand.Materials,
                        $"Revit '재료 탐색기'에서 '{name}'을(를) 고르세요. (재료 브라우저를 특정 재료로 바로 여는 API가 없습니다)");

                case NativePlan.TypeProperties:
                    return OpenTypeProperties(app, doc, id, name);

                case NativePlan.FamilyTypeProperties:
                    return OpenFamilyTypeProperties(app, doc, el, name);

                default:
                    return Result.Fail("이 종류는 Revit 기본 창으로 열 수 없습니다.");
            }
        }

        // 뷰/시트/범례/일람표: 그 뷰를 활성화하면 Revit 특성 팔레트가 곧바로 그 뷰의 특성을 보여준다.
        // **선택을 반드시 비운다** - 뭔가 선택돼 있으면 팔레트가 그 선택 요소의 특성을 보여주기 때문에,
        // 정작 열려던 뷰의 특성이 안 보인다.
        private static Result ActivateView(UIApplication app, Element el, string name)
        {
            if (el is not View view) return Result.Fail("이 항목은 뷰가 아닙니다.");
            if (view.IsTemplate) return Result.Fail("뷰 템플릿은 열 수 없습니다.");

            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null) return Result.Fail("열려 있는 문서가 없습니다.");

            try
            {
                if (uidoc.ActiveView == null || uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
                uidoc.Selection.SetElementIds(new List<ElementId>());
            }
            catch (System.Exception ex)
            {
                return Result.Fail("이 뷰를 열 수 없습니다: " + ex.GetBaseException().Message);
            }

            return Result.Ok($"'{name}'을(를) 열었습니다 - Revit 특성 팔레트에 이 뷰의 특성이 표시됩니다.");
        }

        // 뷰 템플릿/재료: 항목을 지정할 수 없는 "브라우저" 성격의 기본 창.
        private static Result PostBrowser(UIApplication app, PostableCommand command, string hint)
        {
            RevitCommandId? id = RevitCommandId.LookupPostableCommandId(command);
            if (id == null || !app.CanPostCommand(id))
                return Result.Fail("지금은 Revit 기본 창을 열 수 없습니다 (다른 명령이 실행 중일 수 있습니다).");

            app.PostCommand(id);
            return Result.Ok(hint);
        }

        private static Result OpenTypeProperties(UIApplication app, Document doc, ElementId typeId, string name)
        {
            ElementId instanceId = FindInstanceOfType(app, doc, typeId);
            if (instanceId == ElementId.InvalidElementId)
                return Result.Fail(
                    $"'{name}' 유형을 쓰는 부재가 모델에 하나도 없어 Revit 유형 특성 창을 열 수 없습니다.\n" +
                    "(Revit의 유형 특성 명령은 '선택된 부재'를 기준으로 동작합니다.)");

            return SelectAndPostTypeProperties(app, instanceId, name);
        }

        // 패밀리는 그 자체로 특성 창이 없다 - 그 패밀리의 유형 중 실제로 배치된 것을 하나 찾아
        // 그 유형의 특성 창을 연다(Revit에서 사람이 하는 것과 같은 경로).
        private static Result OpenFamilyTypeProperties(UIApplication app, Document doc, Element el, string name)
        {
            if (el is not Family family) return Result.Fail("이 항목은 패밀리가 아닙니다.");

            ICollection<ElementId> symbolIds;
            try { symbolIds = family.GetFamilySymbolIds(); }
            catch { symbolIds = new List<ElementId>(); }

            foreach (ElementId symbolId in symbolIds)
            {
                ElementId instanceId = FindInstanceOfType(app, doc, symbolId);
                if (instanceId == ElementId.InvalidElementId) continue;
                Element? symbol = doc.GetElement(symbolId);
                return SelectAndPostTypeProperties(app, instanceId, symbol?.Name ?? name);
            }

            return Result.Fail(
                $"'{name}' 패밀리의 유형 중 모델에 배치된 것이 하나도 없어 Revit 유형 특성 창을 열 수 없습니다.\n" +
                "(Revit의 유형 특성 명령은 '선택된 부재'를 기준으로 동작합니다.)");
        }

        private static Result SelectAndPostTypeProperties(UIApplication app, ElementId instanceId, string typeName)
        {
            UIDocument? uidoc = app.ActiveUIDocument;
            if (uidoc == null) return Result.Fail("열려 있는 문서가 없습니다.");

            RevitCommandId? id = RevitCommandId.LookupPostableCommandId(PostableCommand.TypeProperties);
            if (id == null || !app.CanPostCommand(id))
                return Result.Fail("지금은 Revit 유형 특성 창을 열 수 없습니다 (다른 명령이 실행 중일 수 있습니다).");

            // 선택을 먼저 바꾸고 나서 명령을 건다 - PostCommand는 이 핸들러가 끝난 뒤에 실행되므로
            // 그 시점에는 선택이 이미 반영돼 있다.
            uidoc.Selection.SetElementIds(new List<ElementId> { instanceId });
            app.PostCommand(id);

            return Result.Ok($"'{typeName}' 유형을 쓰는 부재를 선택하고 Revit 유형 특성 창을 엽니다. (NAMER 때문에 Revit의 선택이 바뀝니다)");
        }

        // 그 유형을 쓰는 부재 하나를 찾는다. **활성 뷰에 보이는 것을 먼저** 찾는다 - 화면에 없는 요소를
        // 선택해 두면 창을 닫은 뒤 "뭐가 선택된 거지?" 상태가 된다.
        private static ElementId FindInstanceOfType(UIApplication app, Document doc, ElementId typeId)
        {
            // 로드된 패밀리는 전용 필터가 있어 문서 전체를 훑지 않아도 된다.
            try
            {
                if (doc.GetElement(typeId) is FamilySymbol)
                {
                    Element? hit = new FilteredElementCollector(doc)
                        .WherePasses(new FamilyInstanceFilter(doc, typeId))
                        .FirstElement();
                    if (hit != null) return hit.Id;
                }
            }
            catch { /* 이 유형에 쓸 수 없는 필터면 아래 일반 경로로 간다 */ }

            ElementId categoryId = ElementId.InvalidElementId;
            try { categoryId = doc.GetElement(typeId)?.Category?.Id ?? ElementId.InvalidElementId; }
            catch { /* 카테고리가 없는 유형은 카테고리 제한 없이 훑는다 */ }

            View? activeView = null;
            try { activeView = app.ActiveUIDocument?.ActiveView; } catch { }
            if (activeView != null)
            {
                try
                {
                    ElementId hit = FirstInstance(new FilteredElementCollector(doc, activeView.Id), categoryId, typeId);
                    if (hit != ElementId.InvalidElementId) return hit;
                }
                catch { /* 일람표처럼 뷰 한정 수집이 안 되는 활성 뷰는 건너뛴다 */ }
            }

            return FirstInstance(new FilteredElementCollector(doc), categoryId, typeId);
        }

        // 카테고리로 먼저 좁힌다 - 큰 모델에서 문서 전체의 모든 부재를 훑는 것과 차이가 크다.
        private static ElementId FirstInstance(FilteredElementCollector collector, ElementId categoryId, ElementId typeId)
        {
            if (categoryId != ElementId.InvalidElementId) collector = collector.OfCategoryId(categoryId);
            foreach (Element e in collector.WhereElementIsNotElementType())
                if (e.GetTypeId() == typeId) return e.Id;
            return ElementId.InvalidElementId;
        }
    }
}
