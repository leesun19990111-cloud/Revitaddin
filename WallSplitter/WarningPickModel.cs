using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 경고 하나(발생 건)에 걸린 요소 하나 - 3단 트리의 맨 아래 잎(leaf). GetFailingElements()(직접 원인)와
    // GetAdditionalElements()(같이 관련된 요소, 예: "두 벽이 겹칩니다"의 상대 벽)를 구분하지 않고
    // 하나로 합친다 - 사용자가 원한 건 "이 경고에 어떤 요소들이 얽혀 있는지"를 한눈에 보는 것이라
    // 실패/관련 구분보다 그게 더 중요하다.
    public class WarningPickElement
    {
        public ElementId ElementId { get; }
        public string ElementName { get; }
        public string Category { get; }

        public WarningPickElement(ElementId elementId, string elementName, string category)
        {
            ElementId = elementId;
            ElementName = elementName;
            Category = category;
        }

        // ElementId.IntegerValue(int)는 2023 API에만 있고 2024+에서는 Value(long)로 완전히 대체됐다
        // (QuickToggleSettings.cs의 ElementIdCompat.ToInt() 참고 - 실측으로 이미 확인된 연도별 API 차이).
        public string IdText => ElementId.ToInt().ToString();
    }

    // 경고 "발생 건" 하나(FailureMessage 인스턴스 하나) = 3단 트리의 중간 노드. 예를 들어 벽 A-B, B-C,
    // A-C가 서로 겹치면 "벽이 겹칩니다"라는 같은 종류의 경고가 3번 따로 발생하는데, 이 클래스는 그 발생
    // 건 하나(=한 쌍)를 나타낸다. Elements가 이 발생 건에 얽힌 요소들(맨 아래 잎)이다.
    public class WarningPickGroup
    {
        public string Description { get; }
        public FailureSeverity Severity { get; }
        public List<WarningPickElement> Elements { get; }

        public WarningPickGroup(string description, FailureSeverity severity, List<WarningPickElement> elements)
        {
            Description = description;
            Severity = severity;
            Elements = elements;
        }

        public string SeverityLabel => Severity == FailureSeverity.Error ? "오류" : "경고";

        // 새로고침을 해도 "같은 발생 건"임을 알아보기 위한 키 (2026-09-03, 실시간 갱신용으로 추가).
        // FailureMessage 자체에는 세션을 넘어 안정적인 식별자가 없으므로 "설명 문구 + 얽힌 요소 ID 집합"을
        // 쓴다 - 같은 요소들 사이의 같은 경고면 다시 조회해도 같은 값이 나온다. 목록 안에서의 순서(index)는
        // 다른 발생 건이 사라지면 밀리므로 키로 쓸 수 없다. 요소 ID는 정렬해서 넣는다(GetFailingElements/
        // GetAdditionalElements가 매번 같은 순서를 준다는 보장이 없다).
        public string Key
        {
            get
            {
                var ids = new List<int>();
                foreach (WarningPickElement e in Elements) ids.Add(e.ElementId.ToInt());
                ids.Sort();
                return Description + "|" + string.Join(",", ids);
            }
        }

        // 경고 하나(FailureMessage)에서 발생 건 노드를 만든다. 표시할 요소가 하나도 안 남으면(전부
        // 삭제됐거나 조회 실패) null - 이 발생 건 자체를 트리에서 숨긴다.
        internal static WarningPickGroup? TryBuild(Document doc, FailureMessage warning)
        {
            var ids = new List<ElementId>();
            var seen = new HashSet<ElementId>();
            void AddIds(IEnumerable<ElementId> source)
            {
                foreach (ElementId id in source)
                    if (seen.Add(id)) ids.Add(id);
            }
            AddIds(warning.GetFailingElements());
            AddIds(warning.GetAdditionalElements());

            var elements = new List<WarningPickElement>();
            foreach (ElementId id in ids)
            {
                Element? element = doc.GetElement(id);
                if (element == null) continue; // 조회 시점 사이 삭제되었을 수 있는 방어적 처리
                elements.Add(new WarningPickElement(
                    id,
                    string.IsNullOrEmpty(element.Name) ? "(이름 없음)" : element.Name,
                    element.Category?.Name ?? "-"));
            }
            if (elements.Count == 0) return null;

            return new WarningPickGroup(warning.GetDescriptionText(), warning.GetSeverity(), elements);
        }
    }

    // 경고 "종류" 하나 = 3단 트리의 맨 위 노드. Revit은 같은 종류의 경고(예: "벽이 겹칩니다")를 발생 건마다
    // 별도 FailureMessage로 쪼개 내보내는 경우가 흔해서(요소 2개짜리 발생 건이 여러 개), 발생 건을 평평하게
    // 늘어놓으면 같은 문구가 화면에 여러 번 반복되어 "이게 다 같은 종류의 문제구나"를 알아보기 어렵다.
    // FailureDefinitionId(Revit이 구분하는 "이 경고가 정확히 어떤 검사인지"의 안정적인 식별자)로 먼저
    // 묶어, 그 종류 안에서 실제 발생 건(WarningPickGroup)들을 자식으로 담는다.
    public class WarningPickTypeGroup
    {
        public string TypeLabel { get; }
        public FailureSeverity Severity { get; }
        public List<WarningPickGroup> Occurrences { get; }

        public WarningPickTypeGroup(string typeLabel, FailureSeverity severity, List<WarningPickGroup> occurrences)
        {
            TypeLabel = typeLabel;
            Severity = severity;
            Occurrences = occurrences;
        }

        public string SeverityLabel => Severity == FailureSeverity.Error ? "오류" : "경고";

        // 목록 전체가 "실제로 달라졌는지"를 한 문자열로 비교하기 위한 지문 (2026-09-03, 실시간 갱신용).
        // 트랜잭션이 일어날 때마다 다시 조회하지만, 경고가 그대로면 화면을 다시 그리지 않아야 한다 -
        // 매번 다시 그리면 체크 상태/펼침/스크롤이 계속 튀고, 이 프로젝트가 여러 번 겪은 "필요 없는데
        // 다시 그려서 클릭이 씹힌다" 문제도 그대로 재현된다.
        public static string SignatureOf(List<WarningPickTypeGroup> typeGroups)
        {
            var parts = new List<string>();
            foreach (WarningPickTypeGroup type in typeGroups)
                foreach (WarningPickGroup occurrence in type.Occurrences)
                    parts.Add(occurrence.Key);
            parts.Sort(StringComparer.Ordinal);
            return string.Join("\n", parts);
        }

        public static List<WarningPickTypeGroup> BuildTypeGroups(Document doc, IEnumerable<FailureMessage> warnings)
        {
            // 먼저 FailureDefinitionId별로 원본 FailureMessage를 모으되, 처음 등장한 순서를 그대로
            // 유지한다(Dictionary는 순서를 보장하지 않으므로 별도 순서 목록을 둔다).
            var byType = new Dictionary<Guid, List<FailureMessage>>();
            var order = new List<Guid>();
            foreach (FailureMessage warning in warnings)
            {
                Guid typeId = warning.GetFailureDefinitionId().Guid;
                if (!byType.TryGetValue(typeId, out List<FailureMessage>? list))
                {
                    list = new List<FailureMessage>();
                    byType[typeId] = list;
                    order.Add(typeId);
                }
                list.Add(warning);
            }

            var typeGroups = new List<WarningPickTypeGroup>();
            foreach (Guid typeId in order)
            {
                var occurrences = new List<WarningPickGroup>();
                foreach (FailureMessage warning in byType[typeId])
                {
                    WarningPickGroup? occurrence = WarningPickGroup.TryBuild(doc, warning);
                    if (occurrence != null) occurrences.Add(occurrence);
                }
                if (occurrences.Count == 0) continue; // 이 종류에 남은 요소가 하나도 없으면 통째로 숨김

                // 종류 이름은 대표로 첫 발생 건의 설명 문구를 쓴다 - 같은 FailureDefinitionId의 경고는
                // 보통 요소별 고유값 없이 같은 생성 문구를 쓰므로(예: "강조 표시된 벽이 겹칩니다.") 대부분
                // 실제로 동일하고, 드물게 발생 건마다 문구가 다르더라도 각 발생 건 항목에서 실제 문구를
                // 다시 보여주므로 오해할 일은 없다.
                typeGroups.Add(new WarningPickTypeGroup(occurrences[0].Description, occurrences[0].Severity, occurrences));
            }
            return typeGroups;
        }
    }
}
