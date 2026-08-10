using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    public enum QuickToggleButtonState
    {
        Off,
        On,
        // 대상이 아직 지정되지 않았거나, 현재 뷰/문서가 이 카테고리를 지원하지 않는 경우 (예: 스케줄 뷰의
        // 필터, 워크셰어링 안 된 문서의 작업세트). 버튼은 회색으로 비활성 표시된다.
        Disabled,
    }

    // Revit API를 직접 만지는 순수 로직. 트랜잭션은 호출자(QuickToggleExternalEventHandler)가 연다.
    public static class QuickToggleService
    {
        public static QuickToggleButtonState DetermineState(View view, QuickToggleButtonConfig cfg)
        {
            try
            {
                switch (cfg.Category)
                {
                    case QuickToggleCategory.ViewTemplate:
                        if (cfg.ViewTemplateId == null) return QuickToggleButtonState.Disabled;
                        return view.ViewTemplateId.ToInt() == cfg.ViewTemplateId.Value
                            ? QuickToggleButtonState.On
                            : QuickToggleButtonState.Off;

                    case QuickToggleCategory.Filter:
                        if (cfg.FilterIds.Count == 0) return QuickToggleButtonState.Disabled;
                        ICollection<ElementId> appliedFilters = view.GetFilters();
                        bool allFiltersOn = cfg.FilterIds.All(id =>
                        {
                            ElementId eid = new ElementId(id);
                            return appliedFilters.Contains(eid) && view.GetFilterVisibility(eid);
                        });
                        return allFiltersOn ? QuickToggleButtonState.On : QuickToggleButtonState.Off;

                    case QuickToggleCategory.Workset:
                        if (!view.Document.IsWorkshared) return QuickToggleButtonState.Disabled;
                        if (cfg.WorksetIds.Count == 0) return QuickToggleButtonState.Disabled;
                        bool allWorksetsOn = cfg.WorksetIds.All(id =>
                            view.GetWorksetVisibility(new WorksetId(id)) == WorksetVisibility.Visible);
                        return allWorksetsOn ? QuickToggleButtonState.On : QuickToggleButtonState.Off;

                    default:
                        return QuickToggleButtonState.Disabled;
                }
            }
            catch
            {
                // 이 뷰 종류가 해당 카테고리를 지원하지 않는 경우(예: 스케줄의 필터) 예외로 판단해서
                // 뷰 타입별 지원 여부를 직접 나열하지 않는다.
                return QuickToggleButtonState.Disabled;
            }
        }

        // 호출자가 이미 Transaction을 연 상태에서 호출해야 한다. bool 반환값은 실제로 반영됐는지를
        // 나타낸다 - CONFIRMED 코드 결함(2026-07-27 리뷰로 발견, 라이브 재현은 아직): 뷰템플릿 케이스만
        // Filter/Workset과 달리 try/catch가 없었다. view.ViewTemplateId 설정은 대상 뷰템플릿이 현재
        // 뷰 종류와 호환되지 않으면(예: 카테고리가 다른 뷰템플릿) 예외를 던지는데, 그 예외가 호출자
        // (QuickToggleExternalEventHandler.Execute, ExternalEvent 콜백이라 실패해도 Revit이 사용자에게
        // 아무 오류도 보여주지 않음)까지 그대로 전파되면 트랜잭션이 롤백되고 "버튼을 눌러도 아무 반응이
        // 없다"는 증상만 남는다. 여기서 잡아서 실패를 호출자에게 bool로 알린다.
        public static bool Toggle(View view, QuickToggleButtonConfig cfg, bool turnOn)
        {
            switch (cfg.Category)
            {
                case QuickToggleCategory.ViewTemplate:
                    try
                    {
                        view.ViewTemplateId = turnOn && cfg.ViewTemplateId.HasValue
                            ? new ElementId(cfg.ViewTemplateId.Value)
                            : ElementId.InvalidElementId;
                    }
                    catch
                    {
                        // 대상 뷰템플릿이 이 뷰 종류/카테고리와 호환되지 않는 경우 등 - 호출자가 실패로 보고한다.
                        return false;
                    }
                    break;

                case QuickToggleCategory.Filter:
                    ICollection<ElementId> appliedFilters = view.GetFilters();
                    foreach (int id in cfg.FilterIds)
                    {
                        try
                        {
                            ElementId eid = new ElementId(id);
                            if (turnOn)
                            {
                                // 필터 off는 "표시" 체크박스만 끄고 필터 자체는 뷰에 남겨두므로(Q&A 확정),
                                // on으로 되돌릴 때 필터가 이미 뷰에 있으면 다시 추가할 필요가 없다.
                                if (!appliedFilters.Contains(eid)) view.AddFilter(eid);
                                view.SetFilterVisibility(eid, true);
                            }
                            else if (appliedFilters.Contains(eid))
                            {
                                view.SetFilterVisibility(eid, false);
                            }
                        }
                        catch
                        {
                            // 그룹 중 하나가 실패해도(삭제된 필터 등) 나머지는 계속 적용
                        }
                    }
                    break;

                case QuickToggleCategory.Workset:
                    foreach (int id in cfg.WorksetIds)
                    {
                        try
                        {
                            view.SetWorksetVisibility(new WorksetId(id),
                                turnOn ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
                        }
                        catch
                        {
                            // 삭제된 작업세트 등 - 나머지는 계속 적용
                        }
                    }
                    break;
            }

            return true;
        }

        // "뷰 저장" 버튼 - 순수 읽기라 트랜잭션 없이 호출 가능하다. 뷰템플릿/필터 표시/작업세트 표시에
        // 더해(기존 범위) 2026-07-28 요청으로 카테고리 표시(모델/주석/해석모델/가져온 카테고리 - V/G
        // 대화상자의 탭 구분과 무관하게 doc.Settings.Categories 트리 하나로 전부 노출됨)와 뷰 자르기
        // (CropBoxActive/CropBoxVisible/CropBox)·범위(평면 뷰의 PlanViewRange)까지 확장했다.
        // "모델의 변경사항은 그대로 유지"라는 요청사항 - 형상/파라미터 등 모델 자체는 전혀 건드리지도,
        // 기록하지도 않는다.
        internal static ViewStateSnapshot CaptureViewState(View view)
        {
            Document doc = view.Document;
            ViewStateSnapshot snapshot = new ViewStateSnapshot
            {
                ViewId = view.Id.ToInt(),
                ViewTemplateId = view.ViewTemplateId == ElementId.InvalidElementId
                    ? (int?)null
                    : view.ViewTemplateId.ToInt(),
            };

            try
            {
                foreach (ElementId filterId in view.GetFilters())
                    snapshot.FilterVisibility[filterId.ToInt()] = view.GetFilterVisibility(filterId);
            }
            catch { /* 이 뷰 종류가 필터를 지원하지 않음 - 필터 상태 없이 저장 */ }

            if (doc.IsWorkshared)
            {
                try
                {
                    foreach (Workset workset in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                        snapshot.WorksetVisibility[workset.Id.IntegerValue] =
                            view.GetWorksetVisibility(workset.Id) == WorksetVisibility.Visible;
                }
                catch { /* 이 뷰 종류가 작업세트 표시를 지원하지 않음 */ }
            }

            foreach (Category cat in AllCategories(doc))
            {
                try { snapshot.CategoryHidden[cat.Id.ToInt()] = view.GetCategoryHidden(cat.Id); }
                catch { /* 이 뷰에서 숨기기를 지원하지 않는 카테고리(내부 전용 등) - 건너뜀 */ }
            }

            try
            {
                snapshot.CropBoxActive = view.CropBoxActive;
                snapshot.CropBoxVisible = view.CropBoxVisible;
                if (view.CropBoxActive) snapshot.CropBox = view.CropBox;
            }
            catch { /* 크롭을 지원하지 않는 뷰 종류(일람표 등) */ }

            if (view is ViewPlan viewPlan)
            {
                try { snapshot.PlanViewRange = viewPlan.GetViewRange(); }
                catch { /* 뷰 범위를 지원하지 않는 평면 뷰 하위 종류 등 */ }
            }

            return snapshot;
        }

        // "되돌리기" - 호출자가 이미 Transaction을 연 상태에서 호출해야 한다(Toggle과 동일한 계약).
        // 개별 항목 실패(그 사이 삭제된 필터/작업세트/카테고리, 뷰템플릿이 제어하는 파라미터 등)는
        // 각자 무시하고 나머지는 계속 적용한다 - 기존 필터/작업세트 되돌리기 로직과 같은 방어 스타일.
        internal static void RestoreViewState(View view, ViewStateSnapshot snapshot)
        {
            try
            {
                view.ViewTemplateId = snapshot.ViewTemplateId.HasValue
                    ? new ElementId(snapshot.ViewTemplateId.Value)
                    : ElementId.InvalidElementId;
            }
            catch { /* 저장했던 뷰템플릿이 그 사이 삭제되었거나 이 뷰 종류와 안 맞는 경우 */ }

            ICollection<ElementId> currentFilters;
            try { currentFilters = view.GetFilters(); }
            catch { currentFilters = new List<ElementId>(); }

            foreach (KeyValuePair<int, bool> kvp in snapshot.FilterVisibility)
            {
                try
                {
                    ElementId filterId = new ElementId(kvp.Key);
                    // 저장 시점 이후 필터 자체가 뷰에서 빠졌으면(삭제 등) 되살리지 않는다 - 표시 상태만
                    // 되돌릴 뿐, 필터 목록 구성 자체를 복원하지는 않는다.
                    if (!currentFilters.Contains(filterId)) continue;
                    view.SetFilterVisibility(filterId, kvp.Value);
                }
                catch { /* 개별 필터 실패는 무시하고 나머지는 계속 적용 */ }
            }

            if (view.Document.IsWorkshared)
            {
                foreach (KeyValuePair<int, bool> kvp in snapshot.WorksetVisibility)
                {
                    try
                    {
                        view.SetWorksetVisibility(new WorksetId(kvp.Key),
                            kvp.Value ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
                    }
                    catch { /* 삭제된 작업세트 등 */ }
                }
            }

            foreach (KeyValuePair<int, bool> kvp in snapshot.CategoryHidden)
            {
                try { view.SetCategoryHidden(new ElementId(kvp.Key), kvp.Value); }
                catch { /* 뷰템플릿이 제어하거나, 그 사이 삭제된 카테고리 등 */ }
            }

            try { view.CropBoxActive = snapshot.CropBoxActive; }
            catch { /* 크롭을 지원하지 않는 뷰 종류 */ }

            if (snapshot.CropBox != null)
            {
                try { view.CropBox = snapshot.CropBox; }
                catch { /* 크롭이 비활성 상태이거나 지원하지 않는 뷰 종류 */ }
            }

            try { view.CropBoxVisible = snapshot.CropBoxVisible; }
            catch { /* 크롭을 지원하지 않는 뷰 종류 */ }

            if (view is ViewPlan viewPlan && snapshot.PlanViewRange != null)
            {
                try { viewPlan.SetViewRange(snapshot.PlanViewRange); }
                catch { /* 저장 시점 이후 참조 레벨이 삭제된 경우 등 */ }
            }
        }

        // doc.Settings.Categories(최상위) + 모든 하위 SubCategories를 재귀적으로 순회한다. Revit의
        // V/G 대화상자는 모델/주석/해석모델/가져온 카테고리를 탭으로 나눠 보여주지만, 전부 이 하나의
        // 카테고리 트리에서 나온 Category 객체이고 View.GetCategoryHidden/SetCategoryHidden도 카테고리
        // 종류를 구분하지 않으므로, 탭별로 따로 나열하지 않고 트리 전체를 한 번에 다룬다.
        private static IEnumerable<Category> AllCategories(Document doc)
        {
            foreach (Category top in doc.Settings.Categories)
            {
                yield return top;
                foreach (Category sub in AllSubCategories(top))
                    yield return sub;
            }
        }

        private static IEnumerable<Category> AllSubCategories(Category category)
        {
            foreach (Category sub in category.SubCategories)
            {
                yield return sub;
                foreach (Category grandchild in AllSubCategories(sub))
                    yield return grandchild;
            }
        }
    }
}
