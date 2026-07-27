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
    }
}
