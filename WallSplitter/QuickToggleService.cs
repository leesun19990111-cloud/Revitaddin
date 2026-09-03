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
        // ===== 대상 해석: 이름 → 이 문서의 ElementId (2026-09-03) =====
        //
        // 커스텀 버튼 설정이 PC 전역이 되면서(QuickToggleSettings 주석 참고) 저장된 ElementId는 더 이상
        // 믿을 수 없다 - 같은 정수 ID가 문서마다 전혀 다른 요소를 가리키기 때문이다. 그래서 뷰템플릿/
        // 필터/작업세트/색상 카테고리는 전부 **이름**으로 지금 문서에서 다시 찾는다. 이름이 비어 있는
        // 옛 설정(2026-07-28에 이름 필드가 생기기 전에 만든 버튼)만 저장된 ID로 되돌아간다.
        //
        // **반드시 캐시할 것**: DetermineState는 툴바의 Idling 갱신에서 **버튼마다 매 틱** 호출된다.
        // 여기서 FilteredElementCollector로 문서를 통째로 훑으면 이 기능이 이미 세 번 겪은 "Idling
        // 콜백에서 매 틱 비싼 일을 한다" 부류의 문제가 그대로 재발한다(ScanLinks와 같은 이유·같은 방식).
        private sealed class TargetIndex
        {
            public Dictionary<string, int> ViewTemplates = new Dictionary<string, int>();
            public Dictionary<string, int> Filters = new Dictionary<string, int>();
            public Dictionary<string, int> Worksets = new Dictionary<string, int>();
            public DateTime StampUtc;
        }

        private static readonly Dictionary<string, TargetIndex> TargetIndexes = new Dictionary<string, TargetIndex>();
        private static readonly TimeSpan TargetIndexLifetime = TimeSpan.FromSeconds(2);

        // 문서 식별 키 - 저장 안 된 새 문서는 PathName이 비어 있어 Title로 대신한다(같은 제목의 저장 안 된
        // 문서를 동시에 여러 개 열어두면 뭉뚱그려지는데, 이 파일의 다른 캐시들과 같은 이미 알려진 제약이다).
        private static string DocKey(Document doc) =>
            string.IsNullOrEmpty(doc.PathName) ? "__unsaved__:" + doc.Title : doc.PathName;

        private static TargetIndex IndexOf(Document doc)
        {
            string key = DocKey(doc);
            if (TargetIndexes.TryGetValue(key, out TargetIndex? cached) && cached != null &&
                DateTime.UtcNow - cached.StampUtc < TargetIndexLifetime)
                return cached;

            TargetIndex index = new TargetIndex { StampUtc = DateTime.UtcNow };
            try
            {
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                    if (v.IsTemplate) index.ViewTemplates[v.Name] = v.Id.ToInt();
            }
            catch
            {
                // 문서 상태에 따라 조회가 실패할 수 있다 - 그 종류만 못 찾는 것으로 두고 나머지는 계속한다.
            }
            try
            {
                foreach (ParameterFilterElement f in new FilteredElementCollector(doc)
                             .OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
                    index.Filters[f.Name] = f.Id.ToInt();
            }
            catch
            {
            }
            try
            {
                if (doc.IsWorkshared)
                    foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                        index.Worksets[w.Name] = w.Id.IntegerValue;
            }
            catch
            {
            }

            TargetIndexes[key] = index;
            return index;
        }

        // 설정 창에서 저장한 직후처럼 "방금 만든 요소를 곧바로 찾아야 하는" 경우를 위해 캐시를 버린다.
        public static void InvalidateTargetIndex() => TargetIndexes.Clear();

        public static int? ResolveViewTemplateId(Document doc, QuickToggleButtonConfig cfg)
        {
            if (string.IsNullOrEmpty(cfg.ViewTemplateName)) return cfg.ViewTemplateId;
            return IndexOf(doc).ViewTemplates.TryGetValue(cfg.ViewTemplateName!, out int id) ? id : (int?)null;
        }

        public static List<int> ResolveFilterIds(Document doc, QuickToggleButtonConfig cfg) =>
            ResolveByName(cfg.FilterNames, cfg.FilterIds, IndexOf(doc).Filters);

        public static List<int> ResolveWorksetIds(Document doc, QuickToggleButtonConfig cfg) =>
            ResolveByName(cfg.WorksetNames, cfg.WorksetIds, IndexOf(doc).Worksets);

        // 이름 목록이 있으면 그걸로만 찾고(이 문서에 없는 이름은 조용히 빠진다 - 삭제된 대상을 건너뛰는
        // 기존 방침과 같다), 이름이 아예 없는 옛 설정만 저장된 ID를 그대로 쓴다.
        private static List<int> ResolveByName(List<string> names, List<int> legacyIds, Dictionary<string, int> index)
        {
            if (names == null || names.Count == 0) return legacyIds ?? new List<int>();

            List<int> resolved = new List<int>(names.Count);
            foreach (string name in names)
                if (index.TryGetValue(name, out int id) && !resolved.Contains(id)) resolved.Add(id);
            return resolved;
        }

        // 색상 버튼의 대상 카테고리. 내장 카테고리(BuiltInCategory)의 Id는 문서가 달라도 같은 음수 값이라
        // ID만으로도 대개 맞지만, 사용자가 만든 하위 카테고리나 가져온 CAD 레이어는 문서마다 다르다 -
        // 그래서 (부모 이름, 카테고리 이름)으로 먼저 찾고 못 찾으면 저장된 Id로 되돌아간다. 이 경로는
        // Idling에서 호출되지 않으므로(ColorTool의 DetermineState는 항상 Off 고정) 캐시하지 않는다.
        public static List<int> ResolveColorCategoryIds(Document doc, QuickToggleButtonConfig cfg)
        {
            List<int> resolved = new List<int>(cfg.ColorButtonCategories.Count);
            List<Category> all = null!;

            foreach (ColorToolCategoryConfig wanted in cfg.ColorButtonCategories)
            {
                if (string.IsNullOrEmpty(wanted.CategoryName))
                {
                    if (!resolved.Contains(wanted.CategoryId)) resolved.Add(wanted.CategoryId);
                    continue;
                }

                all ??= AllCategoriesForNameMatching(doc).ToList();
                Category? match = all.FirstOrDefault(c =>
                    c.Name == wanted.CategoryName &&
                    string.Equals(c.Parent?.Name ?? "", wanted.ParentCategoryName ?? "", StringComparison.Ordinal));
                int id = match != null ? match.Id.ToInt() : wanted.CategoryId;
                if (!resolved.Contains(id)) resolved.Add(id);
            }

            return resolved;
        }

        public static QuickToggleButtonState DetermineState(View view, QuickToggleButtonConfig cfg)
        {
            try
            {
                switch (cfg.Category)
                {
                    // 아래 세 케이스의 대상은 저장된 ElementId가 아니라 이름으로 이 문서에서 다시 찾은
                    // 것이다(설정이 PC 전역이 된 2026-09-03부터 - 위 "대상 해석" 절 참고). 이름이 이
                    // 문서에 없으면 해석 결과가 비고, 그러면 예전에 "대상 미지정"이 그랬듯 Disabled(회색)로
                    // 표시된다 - 다른 프로젝트에서 없는 대상을 조용히 건드리지 않는다는 뜻이다.
                    case QuickToggleCategory.ViewTemplate:
                        int? templateId = ResolveViewTemplateId(view.Document, cfg);
                        if (templateId == null) return QuickToggleButtonState.Disabled;
                        return view.ViewTemplateId.ToInt() == templateId.Value
                            ? QuickToggleButtonState.On
                            : QuickToggleButtonState.Off;

                    case QuickToggleCategory.Filter:
                        List<int> filterIds = ResolveFilterIds(view.Document, cfg);
                        if (filterIds.Count == 0) return QuickToggleButtonState.Disabled;
                        ICollection<ElementId> appliedFilters = view.GetFilters();
                        bool allFiltersOn = filterIds.All(id =>
                        {
                            ElementId eid = new ElementId(id);
                            return appliedFilters.Contains(eid) && view.GetFilterVisibility(eid);
                        });
                        return allFiltersOn ? QuickToggleButtonState.On : QuickToggleButtonState.Off;

                    case QuickToggleCategory.Workset:
                        if (!view.Document.IsWorkshared) return QuickToggleButtonState.Disabled;
                        List<int> worksetIds = ResolveWorksetIds(view.Document, cfg);
                        if (worksetIds.Count == 0) return QuickToggleButtonState.Disabled;
                        bool allWorksetsOn = worksetIds.All(id =>
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

                    // 링크된 모델은 개별 링크를 하나씩 끄고 켤 수 있어야 한다는 요청(2026-09-02)으로
                    // 클릭하면 링크 목록 팝업이 열리는 버튼이 됐다 - 상태 색은 "이 뷰에 보이는 링크가
                    // 하나라도 있는가"를 알려주는 표시로 남는다(클릭 자체는 팝업 열기라 On/Off 어느
                    // 쪽이든 눌린다).
                    case QuickToggleCategory.LinkedModel:
                    {
                        List<LinkedModelInfo> linkedModels = LinkedModelsInView(view);
                        if (linkedModels.Count == 0) return QuickToggleButtonState.Disabled;
                        return linkedModels.Any(l => l.Visible) ? QuickToggleButtonState.On : QuickToggleButtonState.Off;
                    }

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

        // 링크된 CAD 도면의 표시 여부는 Revit의 V/G 대화상자와 같은 방식으로 "카테고리 숨기기"로 다룬다 -
        // 도면 파일마다 가져온 카테고리("가져온 카테고리" 탭)가 하나씩 생기므로 카테고리 단위가 곧 도면
        // 단위다. View.HideElements로 인스턴스 자체를 숨기는 방법도 있지만, 그건 V/G 대화상자에 아무 표시도
        // 남지 않아("숨겨진 요소 표시"를 켜야만 보임) 이 버튼으로 끈 걸 다른 경로로 되돌리기 어렵다.
        // (링크된 Revit 모델은 사정이 다르다 - 아래 LinkedModelsInView/SetLinkedModelsVisible 참고.)
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
            // 링크된 Revit 모델: (링크 인스턴스 Id, 목록에 보여줄 이름)
            public List<(int InstanceId, string Name)> Models = new List<(int, string)>();
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
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    // 중첩 링크(링크된 모델이 다시 물고 있는 링크)는 부모 링크를 끄면 같이 사라지므로
                    // 목록에서 뺀다 - V/G의 "Revit 링크" 탭도 중첩 링크는 부모 아래 하위 항목으로만
                    // 보여주고 따로 끄고 켜지 않는다.
                    try
                    {
                        if (doc.GetElement(link.GetTypeId()) is RevitLinkType linkType && linkType.IsNestedLink) continue;
                    }
                    catch { /* 중첩 여부를 판정하지 못하면 목록에 남긴다 - 안 보이는 것보다 낫다 */ }

                    scan.Models.Add((link.Id.ToInt(), LinkedModelDisplayName(doc, link)));
                }
            }
            catch { /* 위와 동일 */ }

            LinkScans[key] = scan;
            return scan;
        }

        // 목록에 보여줄 링크 이름 - RevitLinkInstance.Name은 보통 "파일이름.rvt : 위치" 형태로 같은 파일을
        // 여러 번 링크한 경우까지 Revit이 알아서 구분해준다. 비어 있거나 읽지 못하면 링크 유형(파일) 이름으로
        // 대체한다.
        private static string LinkedModelDisplayName(Document doc, RevitLinkInstance link)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(link.Name)) return link.Name;
            }
            catch { /* 아래 유형 이름으로 대체 */ }

            try
            {
                Element? linkType = doc.GetElement(link.GetTypeId());
                if (linkType != null && !string.IsNullOrWhiteSpace(linkType.Name)) return linkType.Name;
            }
            catch { /* 아래 기본 이름으로 대체 */ }

            return "링크된 모델";
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

        // ===== 링크된 모델: 링크 하나씩 끄고 켜기 (2026-09-02, "링크된 모델도 개별 링크로 끄고 켤 수
        // 있게 해줘") =====
        //
        // 링크된 CAD 도면과 달리 링크된 Revit 모델은 개별 링크마다 카테고리가 생기지 않고 전부 "Revit
        // 링크" 카테고리(OST_RvtLinks) 하나에 묶인다 - 카테고리 숨기기로는 전부 함께 끄는 것밖에 안 된다.
        // 그래서 개별 제어는 요소 단위 숨기기(View.HideElements/UnhideElements)로 한다. 이건 Revit의
        // V/G "Revit 링크" 탭에서 링크의 표시 체크박스를 끄는 것과 같은 저장소를 쓰므로, 이 팝업으로 끈
        // 링크는 V/G에서도 꺼진 것으로 보이고 거기서 다시 켤 수도 있다.
        public class LinkedModelInfo
        {
            public int InstanceId { get; set; }
            public string Name { get; set; } = "";
            public bool Visible { get; set; }
        }

        // 이 뷰에서 끄고 켤 수 있는 링크된 모델 목록 + 각각의 현재 표시 여부. 카테고리가 통째로 꺼져
        // 있으면(예: 사용자가 V/G에서 "Revit 링크"를 끈 경우) 요소 숨김 여부와 무관하게 전부 안 보이는
        // 상태이므로 그때는 모두 Visible=false로 보고한다.
        public static List<LinkedModelInfo> LinkedModelsInView(View view)
        {
            List<LinkedModelInfo> result = new List<LinkedModelInfo>();
            Document doc = view.Document;

            bool categoryHidden = false;
            try { categoryHidden = view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_RvtLinks)); }
            catch { /* 이 뷰 종류가 링크 카테고리를 다루지 못하면 개별 판정으로만 처리 */ }

            foreach ((int instanceId, string name) in ScanLinks(doc).Models)
            {
                Element? link = null;
                try { link = doc.GetElement(new ElementId(instanceId)); }
                catch { /* 캐시된 뒤 지워진 링크 - 건너뛴다 */ }
                if (link == null) continue;

                bool hidden;
                try { hidden = link.IsHidden(view); }
                catch { continue; /* 이 뷰에서 표시 여부를 읽을 수 없는 링크(일람표 등)는 목록에서 뺀다 */ }

                result.Add(new LinkedModelInfo
                {
                    InstanceId = instanceId,
                    Name = name,
                    Visible = !categoryHidden && !hidden,
                });
            }
            return result;
        }

        // 호출자가 이미 Transaction을 연 상태에서 호출해야 한다(Toggle과 같은 계약). 하나라도 실제로
        // 반영됐으면 true.
        public static bool SetLinkedModelsVisible(View view, List<int> instanceIds, bool visible)
        {
            if (instanceIds.Count == 0) return false;
            Document doc = view.Document;

            if (visible) PrepareLinkCategoryForElementControl(view);

            bool any = false;
            foreach (int instanceId in instanceIds)
            {
                Element? link = null;
                try { link = doc.GetElement(new ElementId(instanceId)); }
                catch { /* 그 사이 지워진 링크 - 나머지는 계속 적용 */ }
                if (link == null) continue;

                if (SetElementHidden(view, link, hidden: !visible)) any = true;
            }
            return any;
        }

        // "Revit 링크" 카테고리 자체가 꺼져 있으면 개별 링크를 켜도 화면에 나타나지 않는다(카테고리 숨김이
        // 요소 숨김보다 우선). 그래서 켜기 전에 카테고리를 켜되, 그 순간 같이 드러날 다른 링크는 요소
        // 숨김으로 눌러 화면에 보이던 상태(= 하나도 안 보임)를 그대로 유지한 뒤, 호출자가 원하는 링크만
        // 개별로 켠다. 설치본 v63은 이 버튼을 카테고리 숨기기로 구현했었으므로, 그때 꺼둔 뷰에서도 개별
        // 켜기가 제대로 동작하려면 이 보정이 필요하다.
        private static void PrepareLinkCategoryForElementControl(View view)
        {
            ElementId categoryId = new ElementId(BuiltInCategory.OST_RvtLinks);
            bool categoryHidden;
            try { categoryHidden = view.GetCategoryHidden(categoryId); }
            catch { return; }
            if (!categoryHidden) return;

            Document doc = view.Document;
            foreach ((int instanceId, string _) in ScanLinks(doc).Models)
            {
                try
                {
                    if (doc.GetElement(new ElementId(instanceId)) is Element link)
                        SetElementHidden(view, link, hidden: true);
                }
                catch { /* 개별 실패는 무시하고 나머지는 계속 */ }
            }

            try { view.SetCategoryHidden(categoryId, false); }
            catch { /* 뷰템플릿이 제어하는 경우 등 - 호출자가 결과로 판단한다 */ }
        }

        // 이미 원하는 상태면 아무것도 하지 않는다 - UnhideElements는 숨겨지지 않은 요소를 넘기면 예외를
        // 던지고, HideElements도 이미 숨겨진 요소에 대해 같은 문제가 있어 상태를 먼저 확인해야 한다.
        private static bool SetElementHidden(View view, Element element, bool hidden)
        {
            try
            {
                if (element.IsHidden(view) == hidden) return true;

                List<ElementId> ids = new List<ElementId> { element.Id };
                if (hidden)
                {
                    if (!element.CanBeHidden(view)) return false;
                    view.HideElements(ids);
                }
                else
                {
                    view.UnhideElements(ids);
                }
                return true;
            }
            catch
            {
                return false;
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
                        // 대상은 DetermineState와 똑같이 이름으로 다시 찾는다 - 두 곳이 서로 다른 대상을
                        // 보면 "켜졌다고 표시되는데 눌러도 그게 안 꺼지는" 어긋남이 생긴다.
                        int? resolvedTemplateId = ResolveViewTemplateId(view.Document, cfg);
                        view.ViewTemplateId = turnOn && resolvedTemplateId.HasValue
                            ? new ElementId(resolvedTemplateId.Value)
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
                    foreach (int id in ResolveFilterIds(view.Document, cfg))
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
                    foreach (int id in ResolveWorksetIds(view.Document, cfg))
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

                // LinkedModel은 여기로 오지 않는다 - 클릭하면 링크 목록 팝업이 열리고, 실제 적용은
                // 팝업이 보내는 LinkedModelApplyRequest(SetLinkedModelsVisible)로 처리한다.
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
        // 실패하면 false를 반환하고 failureReason에 "왜 안 됐는지"를 채운다 - 예전에는 성공/실패만
        // 돌려줘서 사용자가 받는 안내가 늘 "지금 상황에서 사용할 수 없는 기능일 수 있습니다" 하나뿐이었고,
        // 실제로는 명령 id 조회가 아예 안 되던 버그였는데도 그 사실이 전혀 드러나지 않았다
        // (SunnyToolsCommands.RibbonCommandIds의 CONFIRMED LIVE BUG 참고).
        public static bool RunCommand(UIApplication uiapp, QuickToggleButtonConfig cfg, out string failureReason)
        {
            failureReason = "";
            if (cfg.CommandKind == null || string.IsNullOrEmpty(cfg.CommandId))
            {
                failureReason = "이 버튼에 실행할 기능이 지정되어 있지 않습니다. 커스텀 버튼 설정에서 기능을 골라 주세요.";
                return false;
            }

            try
            {
                RevitCommandId? id;
                if (cfg.CommandKind == QuickToggleCommandKind.NativeRevit)
                {
                    id = Enum.TryParse(cfg.CommandId, out PostableCommand postable)
                        ? RevitCommandId.LookupPostableCommandId(postable)
                        : null;
                    if (id == null)
                    {
                        failureReason = "이 Revit 버전에는 없는 기본 명령입니다.";
                        return false;
                    }
                }
                else
                {
                    // Sunny Tools 자체 명령은 리본에 실제로 만들어진 버튼에서 읽어 둔 id로 조회한다.
                    // 클래스 이름으로는 조회되지 않는다(매니페스트가 명령을 등록하지 않으므로) - 자세한
                    // 이유와 저널 실측 근거는 SunnyToolsCommands.RibbonCommandIds 주석 참고.
                    string? ribbonId = SunnyToolsCommands.RibbonCommandIdFor(cfg.CommandId!);
                    id = ribbonId != null ? RevitCommandId.LookupCommandId(ribbonId) : null;

                    // 혹시 이 명령이 매니페스트에 <AddIn Type="Command">로도 등록된 환경이라면 클래스
                    // 이름으로도 잡힌다 - 마지막으로 한 번 더 시도한다(있으면 이득, 없으면 그대로 null).
                    id ??= RevitCommandId.LookupCommandId(cfg.CommandId);

                    if (id == null)
                    {
                        failureReason = ribbonId == null
                            ? "이 기능의 리본 버튼을 찾지 못했습니다. Revit을 다시 시작해도 같으면 알려 주세요."
                            : "Revit이 이 기능의 명령을 찾지 못했습니다(id: " + ribbonId + ").";
                        return false;
                    }
                }

                if (!uiapp.CanPostCommand(id))
                {
                    failureReason = "지금은 이 기능을 실행할 수 없습니다. 진행 중인 명령이나 열려 있는 대화상자를 먼저 끝내고 다시 눌러 주세요.";
                    return false;
                }

                uiapp.PostCommand(id);
                return true;
            }
            catch (Exception ex)
            {
                // 문서 없음, 다른 명령이 이미 posted 상태(InvalidOperationException) 등.
                failureReason = ex.GetBaseException().Message;
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
