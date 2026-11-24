using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace WallSplitter
{
    // "일괄 결합금지/허용"의 Revit 쪽 로직 전부. 창(BatchJoinWindow)은 고르는 일만 한다(RoomBoundingService와 같은 구조).
    //
    // **무엇을 하는 기능인가** (2026-09-23 사용자 요청): "부재의 끝에 결합되는 요소와의 결합을 일괄적으로
    // 금지 또는 허용". Revit에서 벽/보의 끝을 하나씩만 오른쪽 클릭해 걸 수 있는 "결합 허용 안 함"
    // (Disallow Join)을, 고른 요소 전부 또는 고른 유형의 모든 인스턴스에 한 번에 건다.
    //
    // 두 종류의 API가 필요하다 - 같은 개념이지만 Revit은 벽과 구조 프레임을 서로 다른 유틸리티로 다룬다:
    //   벽         : WallUtils.{Allow,Disallow}WallJoinAtEnd / IsWallJoinAllowedAtEnd
    //   구조 프레임 : StructuralFramingUtils.{Allow,Disallow}JoinAtEnd / IsJoinAllowedAtEnd
    // 2023~2027 참조 어셈블리에 시그니처까지 동일하게 존재함을 MetadataLoadContext로 확인했다.
    //
    // **StructuralFramingUtils는 보/가새가 아닌 패밀리 인스턴스에 부르면 예외를 던진다** - 그래서 조회조차
    // StructuralType으로 먼저 거르고, 그래도 남는 예외는 요소 단위로 삼킨다(아래 주석 참고).
    internal static class BatchJoinService
    {
        // 어느 쪽 끝에 적용할지. Revit의 끝 번호는 0(시작)/1(끝)이다.
        internal enum EndChoice
        {
            Start,
            End,
            Both,
        }

        internal static int[] EndsOf(EndChoice choice)
        {
            if (choice == EndChoice.Start) return new[] { 0 };
            if (choice == EndChoice.End) return new[] { 1 };
            return new[] { 0, 1 };
        }

        // ===== 요소 분류 =====

        private static Wall? AsWall(Element element) => element as Wall;

        // 보/가새만 끝 결합 설정을 가진다. 기둥(StructuralType.Column)이나 일반 패밀리는 대상이 아니며,
        // 그런 요소에 StructuralFramingUtils를 부르면 ArgumentException이 난다.
        private static FamilyInstance? AsFraming(Element element)
        {
            if (element is FamilyInstance instance)
            {
                try
                {
                    StructuralType type = instance.StructuralType;
                    if (type == StructuralType.Beam || type == StructuralType.Brace) return instance;
                }
                catch
                {
                    // StructuralType 조회 자체가 실패하는 특이한 인스턴스는 대상이 아닌 것으로 본다.
                }
            }
            return null;
        }

        public static bool IsSupported(Element element) => AsWall(element) != null || AsFraming(element) != null;

        // 지금 그 끝이 "결합 허용"인지. 조회할 수 없으면 null - 스택 벽의 하위 벽처럼 Revit이 예외를 던지는
        // 경우가 있으므로 상태를 안다고 단정하지 않는다(모르는 것은 "변경 불가"로 센다).
        public static bool? IsJoinAllowed(Element element, int end)
        {
            try
            {
                Wall? wall = AsWall(element);
                if (wall != null) return WallUtils.IsWallJoinAllowedAtEnd(wall, end);

                FamilyInstance? framing = AsFraming(element);
                if (framing != null) return StructuralFramingUtils.IsJoinAllowedAtEnd(framing, end);
            }
            catch
            {
                // 요소 하나의 실패가 목록 전체를 못 만들게 두지 않는다.
            }
            return null;
        }

        private static void SetJoinAllowed(Element element, int end, bool allow)
        {
            Wall? wall = AsWall(element);
            if (wall != null)
            {
                if (allow) WallUtils.AllowWallJoinAtEnd(wall, end);
                else WallUtils.DisallowWallJoinAtEnd(wall, end);
                return;
            }

            FamilyInstance? framing = AsFraming(element);
            if (framing != null)
            {
                if (allow) StructuralFramingUtils.AllowJoinAtEnd(framing, end);
                else StructuralFramingUtils.DisallowJoinAtEnd(framing, end);
            }
        }

        // ===== 적용 =====

        public class ApplyResult
        {
            public int Elements { get; set; }          // 처리 대상이 된 요소 수(지원 대상만)
            public int ChangedElements { get; set; }   // 실제로 하나라도 바뀐 요소 수
            public int ChangedEnds { get; set; }       // 바뀐 "끝"의 수 (양쪽이면 요소당 최대 2)
            public int AlreadyEnds { get; set; }       // 이미 그 상태였던 끝
            public int FailedEnds { get; set; }        // Revit이 거부한 끝
            public int Unsupported { get; set; }       // 벽/보/가새가 아니라 건너뛴 요소

            public string Summary(bool allow)
            {
                List<string> lines = new List<string>
                {
                    (allow ? "결합을 허용했습니다" : "결합을 금지했습니다") +
                    " - 요소 " + ChangedElements + "개의 끝 " + ChangedEnds + "곳을 바꿨습니다.",
                };
                if (AlreadyEnds > 0) lines.Add("이미 그 상태였던 끝 " + AlreadyEnds + "곳은 그대로 뒀습니다.");
                if (FailedEnds > 0) lines.Add("바꾸지 못한 끝 " + FailedEnds + "곳이 있습니다(스택 벽의 하위 벽 등은 Revit이 거부합니다).");
                if (Unsupported > 0) lines.Add("벽·보·가새가 아니라 건너뛴 요소가 " + Unsupported + "개 있습니다.");
                return string.Join(" ", lines);
            }
        }

        // **호출하는 쪽이 트랜잭션을 연다**(이 프로젝트의 관례 - RoomBoundingService.Apply와 같다).
        public static ApplyResult Apply(IEnumerable<Element> elements, EndChoice endChoice, bool allow)
        {
            ApplyResult result = new ApplyResult();
            int[] ends = EndsOf(endChoice);

            foreach (Element element in elements)
            {
                if (!IsSupported(element)) { result.Unsupported++; continue; }
                result.Elements++;

                bool changedAny = false;
                foreach (int end in ends)
                {
                    bool? current = IsJoinAllowed(element, end);
                    if (current == null) { result.FailedEnds++; continue; }

                    // 이미 그 상태면 건드리지 않는다 - 쓸데없는 변경으로 워크셋 체크아웃/동기화 부담을
                    // 만들지 않기 위함(RoomBoundingService와 같은 방침).
                    if (current.Value == allow) { result.AlreadyEnds++; continue; }

                    try
                    {
                        SetJoinAllowed(element, end, allow);
                        result.ChangedEnds++;
                        changedAny = true;
                    }
                    catch
                    {
                        // 요소 하나가 거부해도 나머지는 계속 처리한다 - 여기서 예외가 올라가면 트랜잭션이
                        // 통째로 취소돼 "아무것도 안 바뀐다"가 된다.
                        result.FailedEnds++;
                    }
                }

                if (changedAny) result.ChangedElements++;
            }

            return result;
        }

        // ===== 유형 목록 =====

        // 유형 한 줄 - 지금 그 유형의 끝들이 몇 곳이나 금지/허용 상태인지까지 보여준다.
        internal class TypeState
        {
            public int TypeId { get; set; }
            public string Name { get; set; } = "";
            public string CategoryName { get; set; } = "";
            public int Instances { get; set; }
            public int AllowedEnds { get; set; }
            public int DisallowedEnds { get; set; }
            public int UnknownEnds { get; set; }

            public string Summary()
            {
                List<string> parts = new List<string>
                {
                    "요소 " + Instances + "개",
                    "결합 금지 " + DisallowedEnds + "곳",
                    "허용 " + AllowedEnds + "곳",
                };
                if (UnknownEnds > 0) parts.Add("변경 불가 " + UnknownEnds + "곳");
                return CategoryName + " · " + string.Join(" · ", parts);
            }

            public bool Matches(string filter) =>
                Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                CategoryName.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        // 벽 + 구조 프레임(보/가새) 인스턴스를 모은다. view가 주어지면 그 뷰에 보이는 것만 센다.
        public static List<Element> CollectCandidates(Document doc, View? view)
        {
            List<Element> found = new List<Element>();

            foreach (Element element in NewCollector(doc, view).OfClass(typeof(Wall)))
                found.Add(element);

            foreach (Element element in NewCollector(doc, view)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType())
            {
                if (AsFraming(element) != null) found.Add(element);
            }

            return found;
        }

        private static FilteredElementCollector NewCollector(Document doc, View? view) =>
            view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);

        public static List<TypeState> CollectTypes(Document doc, View? view)
        {
            Dictionary<int, TypeState> byType = new Dictionary<int, TypeState>();

            foreach (Element element in CollectCandidates(doc, view))
            {
                int typeId = element.GetTypeId().ToInt();
                if (!byType.TryGetValue(typeId, out TypeState? state))
                {
                    state = new TypeState
                    {
                        TypeId = typeId,
                        Name = TypeNameOf(doc, element, typeId),
                        CategoryName = CategoryNameOf(element),
                    };
                    byType[typeId] = state;
                }

                state.Instances++;
                foreach (int end in new[] { 0, 1 })
                {
                    bool? allowed = IsJoinAllowed(element, end);
                    if (allowed == null) state.UnknownEnds++;
                    else if (allowed.Value) state.AllowedEnds++;
                    else state.DisallowedEnds++;
                }
            }

            return byType.Values
                .OrderBy(t => t.CategoryName, StringComparer.CurrentCulture)
                .ThenBy(t => t.Name, StringComparer.CurrentCulture)
                .ToList();
        }

        // 고른 유형에 속한 인스턴스를 모은다. 유형 이름이 아니라 **유형 ElementId**로 찾는다 - 이름은
        // 벽과 보에서 겹칠 수 있다.
        public static List<Element> CollectByTypes(Document doc, View? view, HashSet<int> typeIds)
        {
            if (typeIds.Count == 0) return new List<Element>();
            return CollectCandidates(doc, view)
                .Where(e => typeIds.Contains(e.GetTypeId().ToInt()))
                .ToList();
        }

        private static string TypeNameOf(Document doc, Element element, int typeId)
        {
            try
            {
                Element? type = doc.GetElement(element.GetTypeId());
                if (type != null && !string.IsNullOrWhiteSpace(type.Name)) return type.Name;
            }
            catch
            {
                // 이름을 못 읽어도 목록에서 빠지지는 않게 한다.
            }
            return "(유형 " + typeId + ")";
        }

        private static string CategoryNameOf(Element element)
        {
            try
            {
                Category? category = element.Category;
                if (category != null && !string.IsNullOrWhiteSpace(category.Name)) return category.Name;
            }
            catch
            {
                // 위와 같다.
            }
            return "(분류 없음)";
        }
    }
}
