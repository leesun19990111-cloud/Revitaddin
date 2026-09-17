using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // "룸 경계 ON/OFF"의 Revit 쪽 로직 전부. 창(RoomBoundingWindow)은 목록을 보여주고 고른 결과를 넘기기만
    // 한다(RoomSeparatorService와 같은 구조).
    //
    // **무엇을 하는 기능인가** (2026-09-17 사용자 요청): "모델에 존재하는 모든 벽, 바닥, 지붕, 기초 등등
    // 룸경계를 결정짓는 모델요소들의 룸경계를 ON/OFF 하는 기능". 요소의 "룸 경계"(Room Bounding) 인스턴스
    // 파라미터를 카테고리 단위로 한 번에 켜고 끈다.
    //
    // **카테고리 목록을 하드코딩하지 않는다**: 룸 경계 파라미터를 가진 카테고리는 벽/바닥/지붕/기초 말고도
    // 천장·기둥·커튼시스템·매스·RVT 링크 등 여러 가지이고 Revit 버전마다 늘어날 수 있다. 그래서 목록을
    // 적어 두는 대신 **모델 요소를 훑어 그 파라미터를 실제로 가진 것만** 모은다 - 사용자가 말한 "등등"이
    // 자동으로 포함되고, 새 Revit에서 카테고리가 늘어도 코드를 고칠 필요가 없다.
    internal static class RoomBoundingService
    {
        // 룸 경계 파라미터. 이름은 WALL_로 시작하지만 벽 전용이 아니라 바닥·지붕·기둥·링크 등이 함께 쓰는
        // 공용 파라미터다(Revit API의 오래된 이름 - 헷갈려서 벽만 처리하게 만들지 말 것).
        private const BuiltInParameter RoomBoundingParam = BuiltInParameter.WALL_ATTR_ROOM_BOUNDING;

        // 카테고리 한 줄 - 지금 몇 개가 켜져 있고 몇 개가 꺼져 있는지까지 보여준다.
        internal class CategoryState
        {
            public int CategoryId { get; set; }
            public string Name { get; set; } = "";
            public int OnCount { get; set; }
            public int OffCount { get; set; }
            public int LockedCount { get; set; }   // 읽기 전용이라 바꿀 수 없는 것

            public int Total => OnCount + OffCount + LockedCount;

            public string Summary()
            {
                List<string> parts = new List<string> { "켜짐 " + OnCount, "꺼짐 " + OffCount };
                if (LockedCount > 0) parts.Add("변경 불가 " + LockedCount);
                return string.Join(" · ", parts) + " (모두 " + Total + "개)";
            }
        }

        public static List<CategoryState> Collect(Document doc)
        {
            Dictionary<int, CategoryState> byCategory = new Dictionary<int, CategoryState>();

            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                Category? category = null;
                try { category = element.Category; }
                catch { continue; }
                if (category == null || category.CategoryType != CategoryType.Model) continue;

                Parameter? p = GetRoomBounding(element);
                if (p == null) continue;

                int id = category.Id.ToInt();
                if (!byCategory.TryGetValue(id, out CategoryState? state))
                {
                    state = new CategoryState { CategoryId = id, Name = category.Name };
                    byCategory[id] = state;
                }

                if (p.IsReadOnly) state.LockedCount++;
                else if (p.AsInteger() != 0) state.OnCount++;
                else state.OffCount++;
            }

            return byCategory.Values
                .OrderByDescending(c => c.Total)
                .ThenBy(c => c.Name, StringComparer.CurrentCulture)
                .ToList();
        }

        // 정수(0/1) 파라미터일 때만 우리가 다룰 수 있는 것으로 본다 - 혹시 다른 저장 타입이면 건드리지 않는다.
        private static Parameter? GetRoomBounding(Element element)
        {
            try
            {
                Parameter? p = element.get_Parameter(RoomBoundingParam);
                return p != null && p.StorageType == StorageType.Integer ? p : null;
            }
            catch
            {
                return null;
            }
        }

        public class ApplyResult
        {
            public int Changed { get; set; }
            public int AlreadySet { get; set; }
            public int Locked { get; set; }
            public int Failed { get; set; }
        }

        // 고른 카테고리의 요소 전부의 룸 경계를 켜거나 끈다. **호출하는 쪽이 트랜잭션을 연다**(이 프로젝트의 관례).
        public static ApplyResult Apply(Document doc, HashSet<int> categoryIds, bool on)
        {
            ApplyResult result = new ApplyResult();
            if (categoryIds.Count == 0) return result;

            int want = on ? 1 : 0;

            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                Category? category = null;
                try { category = element.Category; }
                catch { continue; }
                if (category == null || category.CategoryType != CategoryType.Model) continue;
                if (!categoryIds.Contains(category.Id.ToInt())) continue;

                Parameter? p = GetRoomBounding(element);
                if (p == null) continue;

                if (p.IsReadOnly) { result.Locked++; continue; }
                if (p.AsInteger() == want) { result.AlreadySet++; continue; }

                try
                {
                    if (p.Set(want)) result.Changed++;
                    else result.Failed++;
                }
                catch
                {
                    // 요소 하나가 거부해도 나머지는 계속 처리한다 - 여기서 예외가 올라가면 트랜잭션이
                    // 통째로 취소돼 "아무것도 안 바뀐다"가 된다.
                    result.Failed++;
                }
            }

            return result;
        }
    }
}
