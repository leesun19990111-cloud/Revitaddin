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

                    case QuickToggleCategory.Preset:
                        return DeterminePresetState(view, cfg);

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

        // 프리셋 버튼은 뷰템플릿/필터/작업세트 세 필드를 동시에 가질 수 있고, 그중 비어있는 필드는
        // "이 프리셋에 포함되지 않음"으로 해석해 판정에서 완전히 제외한다(단일 카테고리 버튼과 달리
        // "선택 안 함 = 꺼짐"이 아니다) - 하나도 채워지지 않았으면 Disabled, 하나 이상 채워졌으면 채워진
        // 부분들이 전부 On이어야 전체가 On, 하나라도 Off면 전체 Off. 이 뷰/문서가 지원하지 않는 부분
        // (필터 없는 뷰 종류, 워크셰어링 안 된 문서 등)은 판정에서 조용히 제외한다.
        private static QuickToggleButtonState DeterminePresetState(View view, QuickToggleButtonConfig cfg)
        {
            bool anyPart = false;
            bool allOn = true;

            if (cfg.ViewTemplateId.HasValue)
            {
                anyPart = true;
                if (view.ViewTemplateId.ToInt() != cfg.ViewTemplateId.Value) allOn = false;
            }

            if (cfg.FilterIds.Count > 0)
            {
                try
                {
                    ICollection<ElementId> appliedFilters = view.GetFilters();
                    anyPart = true;
                    bool filtersOn = cfg.FilterIds.All(id =>
                    {
                        ElementId eid = new ElementId(id);
                        return appliedFilters.Contains(eid) && view.GetFilterVisibility(eid);
                    });
                    if (!filtersOn) allOn = false;
                }
                catch { /* 이 뷰 종류가 필터를 지원하지 않음 - 이 부분만 건너뛰고 나머지로 판정 */ }
            }

            if (cfg.WorksetIds.Count > 0 && view.Document.IsWorkshared)
            {
                anyPart = true;
                bool worksetsOn = cfg.WorksetIds.All(id =>
                    view.GetWorksetVisibility(new WorksetId(id)) == WorksetVisibility.Visible);
                if (!worksetsOn) allOn = false;
            }

            // 카테고리(V/G) 재정의가 하나라도 포함되어 있으면 anyPart는 true - CONFIRMED 코드 결함으로
            // 발견(2026-07-29 리뷰): 이 블록을 처음 짤 때 anyPart를 Visible이 있는 카테고리에서만 켰는데,
            // 그러면 "표시 여부는 안 건드리고 색상/패턴만 재정의하는" 프리셋은 anyPart가 끝까지 false로
            // 남아 DeterminePresetState가 Disabled를 반환 - 툴바가 Disabled 버튼을 IsEnabled=false로
            // 그려서(QuickToggleToolbar.UpdateButtonStates) 그런 프리셋은 클릭 자체가 막혀버렸다. on/off
            // 판정(allOn)은 여전히 표시 여부를 지정한 카테고리에 한해서만 반영한다 - 선/패턴/투명도 등은
            // OverrideGraphicSettings에 동등성 비교가 없어 정확히 비교할 방법이 마땅치 않기 때문이다.
            if (cfg.CategoryOverrides.Count > 0)
            {
                anyPart = true;
                foreach (CategoryOverrideConfig co in cfg.CategoryOverrides)
                {
                    if (!co.Visible.HasValue) continue;
                    try
                    {
                        bool hidden = view.GetCategoryHidden(new ElementId(co.CategoryId));
                        bool expectedHidden = !co.Visible.Value;
                        if (hidden != expectedHidden) allOn = false;
                    }
                    catch { /* 그 사이 삭제된 카테고리이거나 이 뷰에서 숨기기를 지원하지 않는 경우 */ }
                }
            }

            if (!anyPart) return QuickToggleButtonState.Disabled;
            return allOn ? QuickToggleButtonState.On : QuickToggleButtonState.Off;
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

                case QuickToggleCategory.Preset:
                    return TogglePreset(view, cfg, turnOn);
            }

            return true;
        }

        // 프리셋 버튼 적용 - 위 세 case와 같은 동작을 재사용하되, 비어있는 필드(이 프리셋에 포함되지
        // 않은 항목)는 아예 건드리지 않는다는 점만 다르다. 특히 뷰템플릿은 단일 카테고리 버튼과 달리
        // "선택 안 함"일 때 InvalidElementId로 강제 초기화하면 안 된다 - 프리셋에 뷰템플릿이 포함되지
        // 않았을 뿐인데 클릭할 때마다 현재 뷰템플릿을 지워버리는 것을 막기 위함.
        private static bool TogglePreset(View view, QuickToggleButtonConfig cfg, bool turnOn)
        {
            bool ok = true;

            if (cfg.ViewTemplateId.HasValue)
            {
                try
                {
                    view.ViewTemplateId = turnOn
                        ? new ElementId(cfg.ViewTemplateId.Value)
                        : ElementId.InvalidElementId;
                }
                catch
                {
                    ok = false;
                }
            }

            if (cfg.FilterIds.Count > 0)
            {
                try
                {
                    ICollection<ElementId> appliedFilters = view.GetFilters();
                    foreach (int id in cfg.FilterIds)
                    {
                        try
                        {
                            ElementId eid = new ElementId(id);
                            if (turnOn)
                            {
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
                }
                catch
                {
                    // 이 뷰 종류가 필터를 지원하지 않음
                }
            }

            if (cfg.WorksetIds.Count > 0)
            {
                foreach (int id in cfg.WorksetIds)
                {
                    try
                    {
                        view.SetWorksetVisibility(new WorksetId(id),
                            turnOn ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
                    }
                    catch
                    {
                        // 워크셰어링 안 된 문서이거나 삭제된 작업세트 등 - 나머지는 계속 적용
                    }
                }
            }

            if (cfg.CategoryOverrides.Count > 0) ApplyCategoryOverrides(view, cfg, turnOn);

            return ok;
        }

        // 프리셋의 카테고리(V/G) 탭 적용 - 켜질 때는 저장된 표시 여부 + 그래픽 재정의(선/패턴/투명도/
        // 하프톤/상세수준)를 그대로 적용하고, 꺼질 때는 표시로 되돌리고 재정의를 완전히 지운다(빈
        // OverrideGraphicSettings로 교체) - "되돌리기" 스냅샷과 달리 프리셋은 켜짐/꺼짐 두 상태를 직접
        // 정의하는 방식이라(필터/작업세트와 동일한 사고방식), 끄기 = "그 사이 원래 어떤 상태였는지"가
        // 아니라 "표시 + 재정의 없음"이라는 고정된 기본 상태로 정의했다.
        private static void ApplyCategoryOverrides(View view, QuickToggleButtonConfig cfg, bool turnOn)
        {
            Document doc = view.Document;
            foreach (CategoryOverrideConfig co in cfg.CategoryOverrides)
            {
                ElementId catId = new ElementId(co.CategoryId);
                if (turnOn)
                {
                    if (co.Visible.HasValue)
                    {
                        try { view.SetCategoryHidden(catId, !co.Visible.Value); }
                        catch { /* 그 사이 삭제된 카테고리이거나 뷰템플릿이 제어하는 경우 등 */ }
                    }
                    try { view.SetCategoryOverrides(catId, BuildOverrideGraphicSettings(doc, co)); }
                    catch { /* 이 카테고리가 그래픽 재정의를 지원하지 않거나 삭제된 경우 등 */ }
                }
                else
                {
                    try { view.SetCategoryHidden(catId, false); }
                    catch { /* 위와 동일 */ }
                    try { view.SetCategoryOverrides(catId, new OverrideGraphicSettings()); }
                    catch { /* 위와 동일 */ }
                }
            }
        }

        private static OverrideGraphicSettings BuildOverrideGraphicSettings(Document doc, CategoryOverrideConfig co)
        {
            OverrideGraphicSettings ogs = new OverrideGraphicSettings();

            if (co.Halftone.HasValue) ogs.SetHalftone(co.Halftone.Value);
            if (co.Transparency.HasValue) ogs.SetSurfaceTransparency(co.Transparency.Value);
            if (co.DetailLevel != null && Enum.TryParse(co.DetailLevel, out ViewDetailLevel detailLevel))
            {
                try { ogs.SetDetailLevel(detailLevel); } catch { /* 이 카테고리/뷰 조합이 상세수준 재정의를 지원하지 않는 경우 */ }
            }

            if (co.ProjectionLineWeight.HasValue) ogs.SetProjectionLineWeight(co.ProjectionLineWeight.Value);
            if (co.ProjectionLineColor.HasValue) ogs.SetProjectionLineColor(IntToColor(co.ProjectionLineColor.Value));
            ElementId? projLinePattern = ResolveLinePatternId(doc, co.ProjectionLinePatternName);
            if (projLinePattern != null) ogs.SetProjectionLinePatternId(projLinePattern);

            if (co.CutLineWeight.HasValue) ogs.SetCutLineWeight(co.CutLineWeight.Value);
            if (co.CutLineColor.HasValue) ogs.SetCutLineColor(IntToColor(co.CutLineColor.Value));
            ElementId? cutLinePattern = ResolveLinePatternId(doc, co.CutLinePatternName);
            if (cutLinePattern != null) ogs.SetCutLinePatternId(cutLinePattern);

            if (co.SurfaceForegroundVisible.HasValue) ogs.SetSurfaceForegroundPatternVisible(co.SurfaceForegroundVisible.Value);
            ElementId? surfFgPattern = ResolveFillPatternId(doc, co.SurfaceForegroundPatternName);
            if (surfFgPattern != null) ogs.SetSurfaceForegroundPatternId(surfFgPattern);
            if (co.SurfaceForegroundColor.HasValue) ogs.SetSurfaceForegroundPatternColor(IntToColor(co.SurfaceForegroundColor.Value));

            if (co.SurfaceBackgroundVisible.HasValue) ogs.SetSurfaceBackgroundPatternVisible(co.SurfaceBackgroundVisible.Value);
            ElementId? surfBgPattern = ResolveFillPatternId(doc, co.SurfaceBackgroundPatternName);
            if (surfBgPattern != null) ogs.SetSurfaceBackgroundPatternId(surfBgPattern);
            if (co.SurfaceBackgroundColor.HasValue) ogs.SetSurfaceBackgroundPatternColor(IntToColor(co.SurfaceBackgroundColor.Value));

            if (co.CutForegroundVisible.HasValue) ogs.SetCutForegroundPatternVisible(co.CutForegroundVisible.Value);
            ElementId? cutFgPattern = ResolveFillPatternId(doc, co.CutForegroundPatternName);
            if (cutFgPattern != null) ogs.SetCutForegroundPatternId(cutFgPattern);
            if (co.CutForegroundColor.HasValue) ogs.SetCutForegroundPatternColor(IntToColor(co.CutForegroundColor.Value));

            if (co.CutBackgroundVisible.HasValue) ogs.SetCutBackgroundPatternVisible(co.CutBackgroundVisible.Value);
            ElementId? cutBgPattern = ResolveFillPatternId(doc, co.CutBackgroundPatternName);
            if (cutBgPattern != null) ogs.SetCutBackgroundPatternId(cutBgPattern);
            if (co.CutBackgroundColor.HasValue) ogs.SetCutBackgroundPatternColor(IntToColor(co.CutBackgroundColor.Value));

            return ogs;
        }

        private static Autodesk.Revit.DB.Color IntToColor(int rgb) =>
            new Autodesk.Revit.DB.Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        // 카테고리 편집창의 "실선"/"실채우기" 항목은 실제 LinePatternElement/FillPatternElement가 아니라
        // Revit이 내부적으로 특수 취급하는 값이라 이름으로 검색할 수 없다 - 그래서 이 두 문자열을 예약된
        // 센티널로 쓰고, 그 외에는 이름으로 문서에서 검색한다(내보내기/가져오기에서도 동일하게 사용).
        internal const string SolidLinePatternName = "<실선>";
        internal const string SolidFillPatternName = "<실채우기>";

        private static ElementId? ResolveLinePatternId(Document doc, string? name)
        {
            if (name == null) return null;
            if (name == SolidLinePatternName) return LinePatternElement.GetSolidPatternId();
            return new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                .FirstOrDefault(p => p.Name == name)?.Id;
        }

        private static ElementId? ResolveFillPatternId(Document doc, string? name)
        {
            if (name == null) return null;
            List<FillPatternElement> all = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();
            if (name == SolidFillPatternName) return all.FirstOrDefault(p => p.GetFillPattern().IsSolidFill)?.Id;
            return all.FirstOrDefault(p => p.Name == name)?.Id;
        }

        // 문서에 있는 선 패턴/채우기 패턴 목록 - 설정 창의 카테고리 재정의 편집 드롭다운에서 사용.
        // "실선"/"실채우기"는 목록 맨 앞에 별도로 추가한다(위 센티널 참고, 실제 컬렉션에는 없음).
        public static List<string> AllLinePatternNames(Document doc)
        {
            List<string> names = new List<string> { SolidLinePatternName };
            names.AddRange(new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                .Select(p => p.Name).OrderBy(n => n));
            return names;
        }

        public static List<string> AllFillPatternNames(Document doc)
        {
            List<string> names = new List<string> { SolidFillPatternName };
            names.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Where(p => !p.GetFillPattern().IsSolidFill)
                .Select(p => p.Name).OrderBy(n => n));
            return names;
        }

        // 프리셋 "카테고리(V/G)" 탭에서 쓰는 카테고리 목록 - Revit V/G 대화상자와 같은 4개 그룹으로
        // 나눠 보여주기 위한 헬퍼. 모델/주석/해석모델은 doc.Settings.Categories(최상위 트리)를
        // CategoryType으로 걸러서 쓰고, 가져온 카테고리는 그 트리에 없어(CAD 가져오기별로 동적 생성됨)
        // 전용 루트 카테고리(OST_ImportObjectStyles)의 하위 카테고리에서 따로 모은다.
        public static List<Category> TopLevelCategoriesOfType(Document doc, CategoryType type)
        {
            List<Category> result = new List<Category>();
            foreach (Category c in doc.Settings.Categories)
                if (c.CategoryType == type) result.Add(c);
            return result.OrderBy(c => c.Name).ToList();
        }

        // 가져온 카테고리(Imported Categories) - CAD 가져오기의 레이어가 여기 하위 카테고리로 나타난다.
        // 이 루트 카테고리 자체가 없는 문서(가져온 CAD가 아예 없는 경우 등)도 있어 방어적으로 처리한다.
        public static List<Category> ImportedCategories(Document doc)
        {
            try
            {
                Category? root = Category.GetCategory(doc, BuiltInCategory.OST_ImportObjectStyles);
                if (root?.SubCategories == null) return new List<Category>();
                return root.SubCategories.Cast<Category>().OrderBy(c => c.Name).ToList();
            }
            catch
            {
                return new List<Category>();
            }
        }

        public static List<Category> SubCategoriesOf(Category parent)
        {
            List<Category> result = new List<Category>();
            foreach (Category c in parent.SubCategories) result.Add(c);
            return result.OrderBy(c => c.Name).ToList();
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
                {
                    snapshot.FilterVisibility[filterId.ToInt()] = view.GetFilterVisibility(filterId);
                    try { snapshot.FilterOverrides[filterId.ToInt()] = view.GetFilterOverrides(filterId); }
                    catch { /* 이 필터에 그래픽 재정의를 지원하지 않는 경우 등 */ }
                }
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

                try { snapshot.CategoryOverrides[cat.Id.ToInt()] = view.GetCategoryOverrides(cat.Id); }
                catch { /* 이 카테고리에 그래픽 재정의를 지원하지 않는 경우 등 */ }

                try
                {
                    ElementId schemeId = view.GetColorFillSchemeId(cat.Id);
                    if (schemeId != ElementId.InvalidElementId) snapshot.ColorFillSchemeId[cat.Id.ToInt()] = schemeId;
                }
                catch { /* 이 뷰/카테고리 조합이 색상표를 지원하지 않는 경우(대부분의 카테고리가 여기 해당) */ }
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

            try { snapshot.DetailLevel = view.DetailLevel; }
            catch { /* 상세수준을 지원하지 않는 뷰 종류 */ }

            try { snapshot.DisplayStyle = view.DisplayStyle; }
            catch { /* 비주얼스타일을 지원하지 않는 뷰 종류 */ }

            if (view is View3D view3D)
            {
                try
                {
                    snapshot.SectionBoxActive = view3D.IsSectionBoxActive;
                    if (view3D.IsSectionBoxActive) snapshot.SectionBox = view3D.GetSectionBox();
                }
                catch { /* 단면상자를 지원하지 않는 3D 뷰 종류 등 */ }

                try
                {
                    snapshot.IsPerspective = view3D.IsPerspective;
                    snapshot.Orientation = view3D.GetOrientation();
                }
                catch { /* 방향/투영모드를 읽을 수 없는 경우 */ }

                try { snapshot.RenderingSettings = view3D.GetRenderingSettings(); }
                catch { /* 렌더링설정이 없는 경우(레이트레이스 스타일이 아닌 3D 뷰 등) */ }
            }

            // 그림자/태양경로/스케치라인 등 - Revit 공개 API에 전용 접근자가 없어 최선 노력으로 캐치올:
            // PG_GRAPHICS 그룹의 정수형(예/아니오) 파라미터를 이름 기준으로 전부 캡처한다. 이 값들이 실제로
            // 원하는 항목을 포함하는지는 라이브 테스트로만 확인 가능 - docs/quick-toggle/CLAUDE.md 참고.
            try
            {
                foreach (Parameter p in view.Parameters)
                {
                    try
                    {
                        if (p.Definition.GetGroupTypeId() == GroupTypeId.Graphics
                            && p.StorageType == StorageType.Integer && !p.IsReadOnly)
                        {
                            snapshot.GraphicsIntegerParams[p.Definition.Name] = p.AsInteger();
                        }
                    }
                    catch { /* 개별 파라미터 실패는 무시하고 나머지는 계속 진행 */ }
                }
            }
            catch { /* 이 뷰 종류의 파라미터 목록을 읽을 수 없는 경우 */ }

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

            foreach (KeyValuePair<int, OverrideGraphicSettings> kvp in snapshot.FilterOverrides)
            {
                try
                {
                    ElementId filterId = new ElementId(kvp.Key);
                    if (!currentFilters.Contains(filterId)) continue;
                    view.SetFilterOverrides(filterId, kvp.Value);
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

            foreach (KeyValuePair<int, OverrideGraphicSettings> kvp in snapshot.CategoryOverrides)
            {
                try { view.SetCategoryOverrides(new ElementId(kvp.Key), kvp.Value); }
                catch { /* 뷰템플릿이 제어하거나, 그 사이 삭제된 카테고리 등 */ }
            }

            foreach (KeyValuePair<int, ElementId> kvp in snapshot.ColorFillSchemeId)
            {
                try { view.SetColorFillSchemeId(new ElementId(kvp.Key), kvp.Value); }
                catch { /* 그 사이 삭제된 색상표 등 */ }
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

            if (snapshot.DetailLevel.HasValue)
            {
                try { view.DetailLevel = snapshot.DetailLevel.Value; }
                catch { /* 상세수준을 지원하지 않는 뷰 종류 */ }
            }

            if (snapshot.DisplayStyle.HasValue)
            {
                try { view.DisplayStyle = snapshot.DisplayStyle.Value; }
                catch { /* 비주얼스타일을 지원하지 않는 뷰 종류 */ }
            }

            if (view is View3D view3D)
            {
                try
                {
                    if (snapshot.IsPerspective.HasValue && view3D.IsPerspective != snapshot.IsPerspective.Value)
                    {
                        if (snapshot.IsPerspective.Value) view3D.ToggleToPerspective();
                        else view3D.ToggleToIsometric();
                    }
                    if (snapshot.Orientation != null) view3D.SetOrientation(snapshot.Orientation);
                }
                catch { /* 투영모드/카메라를 되돌릴 수 없는 경우 */ }

                if (snapshot.SectionBox != null)
                {
                    try
                    {
                        view3D.IsSectionBoxActive = snapshot.SectionBoxActive ?? true;
                        view3D.SetSectionBox(snapshot.SectionBox);
                    }
                    catch { /* 단면상자를 지원하지 않는 3D 뷰 종류 등 */ }
                }
                else if (snapshot.SectionBoxActive.HasValue)
                {
                    try { view3D.IsSectionBoxActive = snapshot.SectionBoxActive.Value; }
                    catch { /* 단면상자를 지원하지 않는 3D 뷰 종류 등 */ }
                }

                if (snapshot.RenderingSettings != null)
                {
                    try { view3D.SetRenderingSettings(snapshot.RenderingSettings); }
                    catch { /* 렌더링설정을 되돌릴 수 없는 경우 */ }
                }
            }

            // 최선 노력 캐치올(그림자/태양경로 등 - 위 CaptureViewState 주석 참고): 지금 이 뷰에 같은
            // 이름의 정수형 파라미터가 있으면 저장했던 값으로 되돌린다.
            try
            {
                foreach (Parameter p in view.Parameters)
                {
                    try
                    {
                        if (p.Definition.GetGroupTypeId() == GroupTypeId.Graphics
                            && p.StorageType == StorageType.Integer && !p.IsReadOnly
                            && snapshot.GraphicsIntegerParams.TryGetValue(p.Definition.Name, out int value))
                        {
                            p.Set(value);
                        }
                    }
                    catch { /* 개별 파라미터 실패는 무시하고 나머지는 계속 적용 */ }
                }
            }
            catch { /* 이 뷰 종류의 파라미터 목록을 읽을 수 없는 경우 */ }
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

        // 프리셋 가져오기(다른 모델 간 이식)에서 이름으로 카테고리를 다시 찾을 때 쓰는 전체 목록 -
        // AllCategories(모델/주석/해석모델)에 더해 가져온 카테고리(Imported Categories)까지 포함한다.
        public static IEnumerable<Category> AllCategoriesForNameMatching(Document doc)
        {
            foreach (Category c in AllCategories(doc)) yield return c;
            foreach (Category imported in ImportedCategories(doc))
            {
                yield return imported;
                foreach (Category sub in AllSubCategories(imported)) yield return sub;
            }
        }
    }
}
