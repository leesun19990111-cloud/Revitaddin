using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

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

                    // 색상 버튼은 켜짐/꺼짐 개념이 없다 - 클릭하면 색상/투명도 조절 패널이 열릴 뿐이라
                    // 항상 클릭 가능한 상태(Off)로 고정한다. Disabled를 반환하면 툴바가 버튼 자체를
                    // IsEnabled=false로 그려 클릭이 막힌다(QuickToggleToolbar.RebuildButtons 참고).
                    case QuickToggleCategory.ColorTool:
                        return QuickToggleButtonState.Off;

                    // 기능 버튼도 색상 버튼과 같은 이유로 항상 Off(클릭 가능) 고정 - on/off 개념이 없고
                    // 클릭할 때마다 지정된 명령을 한 번 실행할 뿐이다(RunCommand).
                    case QuickToggleCategory.CommandLauncher:
                        return QuickToggleButtonState.Off;

                    // 링크 버튼(2026-09-02 추가)은 설정에 저장된 대상이 없고 "지금 이 뷰에 걸려 있는
                    // 링크"가 곧 대상이다 - 링크가 하나도 없으면 Disabled(회색), 있으면 전부 숨겨졌을 때
                    // Off, 하나라도 보이면 On이다. 즉 켜짐 = 링크가 화면에 보이는 상태이고, 켜진 버튼을
                    // 누르면 꺼진다("클릭하면 끌 수 있게" 요청).
                    case QuickToggleCategory.LinkedCad:
                        return DetermineLinkState(view, LinkedCadCategoryIds(view));

                    case QuickToggleCategory.LinkedModel:
                        return DetermineLinkState(view, LinkedModelCategoryIds(view));

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

        // ===== 링크된 도면(CAD) / 링크된 모델(RVT) 끄고 켜기 (2026-09-02 추가) =====

        // 링크의 표시 여부는 Revit의 V/G 대화상자와 같은 방식으로 "카테고리 숨기기"로 다룬다 - 링크된
        // CAD 도면은 도면 파일마다 가져온 카테고리("가져온 카테고리" 탭)가 하나씩 생기고, 링크된 Revit
        // 모델은 전부 "Revit 링크" 카테고리(OST_RvtLinks) 하나에 묶인다. View.HideElements로 링크 인스턴스
        // 자체를 숨기는 방법도 있지만, 그건 V/G 대화상자에 아무 표시도 남지 않아("숨겨진 요소 표시"를
        // 켜야만 보임) 이 버튼으로 끈 걸 사용자가 다른 경로로 되돌리기 어렵다.
        private static QuickToggleButtonState DetermineLinkState(View view, List<ElementId> categoryIds)
        {
            if (categoryIds.Count == 0) return QuickToggleButtonState.Disabled;

            bool anyControllable = false;
            bool allHidden = true;
            foreach (ElementId categoryId in categoryIds)
            {
                try
                {
                    bool hidden = view.GetCategoryHidden(categoryId);
                    anyControllable = true;
                    if (!hidden) allHidden = false;
                }
                catch { /* 이 뷰 종류가 이 카테고리의 표시 여부를 다루지 못하는 경우 - 나머지로 판정 */ }
            }

            if (!anyControllable) return QuickToggleButtonState.Disabled;
            return allHidden ? QuickToggleButtonState.Off : QuickToggleButtonState.On;
        }

        // 호출자가 이미 Transaction을 연 상태에서 호출해야 한다(Toggle과 같은 계약). 하나라도 실제로
        // 반영됐으면 true - 전부 실패하면(뷰템플릿이 가시성을 제어하는 경우 등) 호출자가 사용자에게 알린다.
        private static bool ToggleLinkVisibility(View view, List<ElementId> categoryIds, bool turnOn)
        {
            bool any = false;
            foreach (ElementId categoryId in categoryIds)
            {
                try
                {
                    view.SetCategoryHidden(categoryId, !turnOn);
                    any = true;
                }
                catch { /* 뷰템플릿이 가시성을 제어하거나 이 뷰에서 숨길 수 없는 카테고리 - 나머지는 계속 */ }
            }
            return any;
        }

        // 링크 목록 조회는 문서 전체를 훑는 작업인데, DetermineState는 툴바의 Idling 갱신(초당 여러 번,
        // 버튼마다 한 번씩)에서도 호출된다 - 매 틱마다 다시 훑지 않도록 문서별로 짧게 캐시해둔다.
        // 링크를 새로 걸거나 지워도 늦어도 이 시간 안에는 버튼 상태에 반영된다.
        private sealed class LinkScan
        {
            public DateTime At;
            // 링크된 CAD 도면: (그 도면의 가져온 카테고리 Id, 뷰 전용 가져오기면 그 뷰 Id / 아니면 -1)
            public List<(int CategoryId, int OwnerViewId)> Cad = new List<(int, int)>();
            public bool HasModelLink;
        }

        private static readonly Dictionary<string, LinkScan> LinkScans = new Dictionary<string, LinkScan>();
        private static readonly TimeSpan LinkScanLifetime = TimeSpan.FromSeconds(2);

        private static LinkScan ScanLinks(Document doc)
        {
            string key = string.IsNullOrEmpty(doc.PathName) ? "title:" + doc.Title : doc.PathName;
            if (LinkScans.TryGetValue(key, out LinkScan? cached) && DateTime.UtcNow - cached.At < LinkScanLifetime)
                return cached;

            LinkScan scan = new LinkScan { At = DateTime.UtcNow };
            try
            {
                foreach (ImportInstance import in new FilteredElementCollector(doc)
                    .OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
                {
                    // 링크가 아니라 "가져오기"로 들어온 CAD는 대상이 아니다 - 사용자가 말한 "링크된 도면"만.
                    if (!import.IsLinked) continue;
                    Category? category = import.Category;
                    if (category == null) continue;
                    scan.Cad.Add((category.Id.ToInt(), import.OwnerViewId.ToInt()));
                }
            }
            catch { /* 이 문서에서 가져오기 인스턴스를 훑을 수 없는 경우 - CAD 링크 없음으로 취급 */ }

            try
            {
                scan.HasModelLink = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Any();
            }
            catch { /* 위와 동일 */ }

            LinkScans[key] = scan;
            return scan;
        }

        // 이 뷰에서 끄고 켤 수 있는 "링크된 도면"의 카테고리들. 특정 뷰에만 놓인 CAD 링크(뷰 전용
        // 가져오기)는 그 뷰에서만 대상으로 삼고, 모델 공간에 놓인 링크는 어느 뷰에서든 대상이다
        // (평면 뷰의 뷰 범위 밖에 있어 실제로는 안 보이는 경우까지 가려내지는 않는다 - Revit V/G의
        // "가져온 카테고리" 탭도 뷰와 무관하게 문서의 링크를 전부 나열한다).
        internal static List<ElementId> LinkedCadCategoryIds(View view)
        {
            List<ElementId> result = new List<ElementId>();
            HashSet<int> seen = new HashSet<int>();
            int viewId = view.Id.ToInt();
            int invalidId = ElementId.InvalidElementId.ToInt();

            foreach ((int categoryId, int ownerViewId) in ScanLinks(view.Document).Cad)
            {
                if (ownerViewId != invalidId && ownerViewId != viewId) continue;
                if (seen.Add(categoryId)) result.Add(new ElementId(categoryId));
            }
            return result;
        }

        // 링크된 Revit 모델은 개별 링크마다 카테고리가 생기지 않고 전부 "Revit 링크" 카테고리 하나에
        // 묶인다 - 그래서 이 버튼은 "이 뷰의 링크된 모델 전체"를 한 번에 끄고 켠다.
        internal static List<ElementId> LinkedModelCategoryIds(View view)
        {
            if (!ScanLinks(view.Document).HasModelLink) return new List<ElementId>();
            return new List<ElementId> { new ElementId(BuiltInCategory.OST_RvtLinks) };
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

                case QuickToggleCategory.LinkedCad:
                    return ToggleLinkVisibility(view, LinkedCadCategoryIds(view), turnOn);

                case QuickToggleCategory.LinkedModel:
                    return ToggleLinkVisibility(view, LinkedModelCategoryIds(view), turnOn);
            }

            return true;
        }

        // "기능 버튼" 클릭 - 지정된 Revit 명령을 한 번 실행한다(2026-08-03 추가). 트랜잭션을 열지 않는다 -
        // 이 명령 자체(재료 지정 창을 연다, 동기화한다 등)가 필요하면 각자 알아서 트랜잭션을 연다. Sunny
        // Tools 자체 명령(SunnyTool)은 App.cs가 리본 버튼을 등록할 때와 같은 RevitCommandId.
        // LookupCommandId(전체 클래스 이름)로, Revit 기본 명령(NativeRevit)은 LookupPostableCommandId로
        // 조회한다 - 둘 다 조회에 성공하고 CanPostCommand가 참일 때만 PostCommand로 실행 요청을 넣는다
        // (PostCommand는 즉시 실행이 아니라 "다음 기회에 실행해달라"는 요청이라 이 메서드 반환 시점에는
        // 아직 실행되지 않았을 수 있다 - Revit 자체 리본 버튼을 누르는 것과 동일한 메커니즘).
        public static bool RunCommand(UIApplication uiapp, QuickToggleButtonConfig cfg)
        {
            if (cfg.CommandKind == null || string.IsNullOrEmpty(cfg.CommandId)) return false;

            try
            {
                RevitCommandId? id = cfg.CommandKind == QuickToggleCommandKind.NativeRevit
                    ? (Enum.TryParse(cfg.CommandId, out PostableCommand postable) ? RevitCommandId.LookupPostableCommandId(postable) : null)
                    : RevitCommandId.LookupCommandId(cfg.CommandId);

                if (id == null || !uiapp.CanPostCommand(id)) return false;

                uiapp.PostCommand(id);
                return true;
            }
            catch
            {
                // 이 시점(문서 없음, 다른 명령 진행 중 등)에 이 명령을 실행할 수 없는 경우 - 호출자가
                // 실패로 보고한다.
                return false;
            }
        }

        // "색상 버튼" 팝업이 색상 팔레트/투명도 슬라이더를 조작할 때마다 즉시 호출한다(2026-07-29 추가).
        // 매번 새 OverrideGraphicSettings로 덮어쓰지 않고 뷰에 이미 적용된 재정의를 먼저
        // 읽어(view.GetCategoryOverrides) 그 위에 이번에 바뀐 속성만 얹는다 - "켜짐/꺼짐" 두 상태가
        // 없는 실시간 조절 도구라 그래야 색상만 바꿨을 때 기존에 슬라이더로 조절해둔 투명도가
        // 지워지지 않고, 반대의 경우도 마찬가지다. color/transparency
        // 중 null인 쪽은 이번 호출에서 그 속성을 건드리지 않는다는 뜻(둘 다 채워서 호출할 수도 있음).
        public static void ApplyColorTool(View view, List<int> categoryIds, int? color, int? transparency)
        {
            Document doc = view.Document;
            foreach (int categoryId in categoryIds)
            {
                try
                {
                    ElementId catId = new ElementId(categoryId);
                    OverrideGraphicSettings ogs;
                    try { ogs = view.GetCategoryOverrides(catId); }
                    catch { ogs = new OverrideGraphicSettings(); }

                    if (color.HasValue)
                    {
                        Autodesk.Revit.DB.Color c = IntToColor(color.Value);
                        // 실채우기로 지정해야 카테고리 전체 면이 그 색으로 칠해진 것처럼 보인다(패턴만
                        // 바꾸고 색을 안 주면 흰 바탕에 무늬만 남는다) - Revit에서 "카테고리를 이 색으로
                        // 칠한다"고 할 때 실제로 쓰는 방식과 동일. 투영면/절단면 둘 다 적용해 단면도에서도
                        // 같은 색으로 보이게 한다.
                        ElementId? solidFill = ResolveFillPatternId(doc, SolidFillPatternName);
                        ogs.SetSurfaceForegroundPatternVisible(true);
                        if (solidFill != null) ogs.SetSurfaceForegroundPatternId(solidFill);
                        ogs.SetSurfaceForegroundPatternColor(c);
                        ogs.SetCutForegroundPatternVisible(true);
                        if (solidFill != null) ogs.SetCutForegroundPatternId(solidFill);
                        ogs.SetCutForegroundPatternColor(c);
                    }

                    if (transparency.HasValue) ogs.SetSurfaceTransparency(transparency.Value);

                    view.SetCategoryOverrides(catId, ogs);
                }
                catch { /* 삭제된 카테고리이거나 재정의를 지원하지 않는 경우 등 - 나머지 카테고리는 계속 적용 */ }
            }
        }

        // "재지정 지우기" 버튼 (2026-07-29 추가, "색상버튼에서 선택한 카테고리 요소에 입혀진 색상이
        // 아무것도 없게 만들어주는 버튼" 요청) - ApplyColorTool처럼 기존 재정의를 읽어 일부만 바꾸는 게
        // 아니라, 빈 OverrideGraphicSettings로 통째로 교체해 색상/패턴/투명도/하프톤 등 이 카테고리에
        // 걸린 모든 그래픽 재정의를 완전히 비운다.
        public static void ClearColorTool(View view, List<int> categoryIds)
        {
            foreach (int categoryId in categoryIds)
            {
                try { view.SetCategoryOverrides(new ElementId(categoryId), new OverrideGraphicSettings()); }
                catch { /* 삭제된 카테고리이거나 재정의를 지원하지 않는 경우 등 - 나머지 카테고리는 계속 적용 */ }
            }
        }

        // 색상 버튼 팝업을 열 때 초기 표시값으로 쓴다 - 그 카테고리에 이미 적용된 재정의를 그대로 읽어와
        // 팔레트/슬라이더가 "이번에 새로 고르는 것"이 아니라 "지금 적용된 값"에서 시작하도록 한다.
        // 색상은 OverrideGraphicSettings.SurfaceForegroundPatternColor가 유효한 값일 때만(Color.IsValid)
        // 반환하고, 아니면 null(아직 색을 지정한 적 없음)로 취급한다. 투명도는 Revit이 "재정의된 적
        // 있는지" 여부를 별도로 노출하지 않아 재정의 안 한 카테고리도 항상 0으로 읽힌다 - Revit V/G
        // 대화상자 자체도 같은 방식으로 보여주므로 그대로 따랐다.
        public static (int? Color, int Transparency) ReadCurrentColorAndTransparency(View view, int categoryId)
        {
            try
            {
                OverrideGraphicSettings ogs = view.GetCategoryOverrides(new ElementId(categoryId));
                Autodesk.Revit.DB.Color c = ogs.SurfaceForegroundPatternColor;
                int? color = c.IsValid ? ((c.Red << 16) | (c.Green << 8) | c.Blue) : (int?)null;
                return (color, ogs.Transparency);
            }
            catch
            {
                return (null, 0);
            }
        }

        private static Autodesk.Revit.DB.Color IntToColor(int rgb) =>
            new Autodesk.Revit.DB.Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        // "실채우기"는 실제 FillPatternElement 이름이 아니라 Revit이 내부적으로 특수 취급하는 값이라
        // 이름으로 검색할 수 없다 - 예약된 센티널 문자열로 쓰고, 그 외에는 이름으로 문서에서 찾는다
        // (색상 버튼이 카테고리를 그 색으로 "칠할" 때 쓰는 실채우기 패턴).
        internal const string SolidFillPatternName = "<실채우기>";

        private static ElementId? ResolveFillPatternId(Document doc, string? name)
        {
            if (name == null) return null;
            List<FillPatternElement> all = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();
            if (name == SolidFillPatternName) return all.FirstOrDefault(p => p.GetFillPattern().IsSolidFill)?.Id;
            return all.FirstOrDefault(p => p.Name == name)?.Id;
        }

        // 카테고리 목록 - 색상 버튼의 대상 선택(모델 카테고리)과 내보내기/가져오기의 이름 매칭에서 쓴다.
        // 모델/주석/해석모델은 doc.Settings.Categories(최상위 트리)를
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

        // 색상 버튼 가져오기(다른 모델 간 이식)에서 이름으로 카테고리를 다시 찾을 때 쓰는 전체 목록 -
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
