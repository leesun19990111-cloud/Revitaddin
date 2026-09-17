using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // "룸 구분선 자동 생성"의 Revit 쪽 로직 전부. 창(RoomSeparatorWindow)은 여기서 만든 목록을 보여주고
    // 고른 결과를 다시 여기로 넘기기만 한다 - 창에 Revit 조회 코드를 두지 않는 이 프로젝트의 관례를 따른다.
    //
    // **무엇을 하는 기능인가** (2026-09-06 사용자 요청): "지정한 벽체 유형의 중심선에 맞춰서(LINK모델 포함)
    // 룸 구분선을 자동으로 넣어주는 기능". 고른 벽 유형에 해당하는 벽을, 고른 레벨마다 찾아서 그 벽의
    // **중심선**을 룸 구분선(OST_RoomSeparationLines)으로 만든다. 링크된 모델의 벽도 대상이다.
    internal static class RoomSeparatorService
    {
        // 링크 벽이 "이 레벨의 벽"인지 판단하는 높이 허용오차(피트). 링크 모델의 레벨은 호스트와 다른
        // 요소라 이름도 규칙도 다를 수 있어, 이름이 아니라 **호스트 좌표로 환산한 높이**로 맞춘다
        // (층별 단면상자의 레벨 매칭과 같은 방침 - docs/quick-toggle/CLAUDE.md 참고).
        private const double LinkLevelToleranceFeet = 1.0;

        // ===== 목록 만들기 (창이 보여줄 것) =====

        // 벽 유형 한 줄. 호스트와 링크에 같은 이름의 유형이 있으면 **한 줄로 합친다** - 사용자는 "이 이름의
        // 벽"을 고르는 것이지 어느 문서의 유형인지를 고르는 게 아니기 때문이고, 이 프로젝트가 대상을 늘
        // 이름으로 다시 찾는 방침(전역 설정)과도 같다.
        internal class WallTypeChoice
        {
            public string Name { get; set; } = "";
            public bool InHost { get; set; }
            public List<string> LinkNames { get; } = new List<string>();
            public int WallCount { get; set; }

            // 목록 둘째 줄에 쓰는 요약 - 어디에 있는 유형인지 한눈에 보이게.
            public string SourceSummary()
            {
                List<string> parts = new List<string>();
                if (InHost) parts.Add("이 모델");
                if (LinkNames.Count > 0) parts.Add("링크 " + LinkNames.Count + "개");
                string where = parts.Count > 0 ? string.Join(" · ", parts) : "없음";
                return where + " · 벽 " + WallCount + "개";
            }
        }

        public static List<WallTypeChoice> CollectWallTypes(Document doc)
        {
            Dictionary<string, WallTypeChoice> byName = new Dictionary<string, WallTypeChoice>();

            foreach (Wall wall in HostWalls(doc))
            {
                string name = WallTypeName(wall);
                if (string.IsNullOrEmpty(name)) continue;
                WallTypeChoice choice = Choice(byName, name);
                choice.InHost = true;
                choice.WallCount++;
            }

            foreach ((RevitLinkInstance link, Document linkDoc) in LoadedLinks(doc))
            {
                string linkName = LinkName(link);
                foreach (Wall wall in HostWalls(linkDoc))
                {
                    string name = WallTypeName(wall);
                    if (string.IsNullOrEmpty(name)) continue;
                    WallTypeChoice choice = Choice(byName, name);
                    if (!choice.LinkNames.Contains(linkName)) choice.LinkNames.Add(linkName);
                    choice.WallCount++;
                }
            }

            return byName.Values.OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
        }

        private static WallTypeChoice Choice(Dictionary<string, WallTypeChoice> byName, string name)
        {
            if (!byName.TryGetValue(name, out WallTypeChoice? choice))
            {
                choice = new WallTypeChoice { Name = name };
                byName[name] = choice;
            }
            return choice;
        }

        // 레벨 한 줄 - 창은 Revit 타입(Level) 대신 이 평범한 데이터만 다룬다. 창에 Revit 조회 코드를 두지
        // 않는다는 관례의 연장이고, 덕분에 스크래치패드 하네스에서 창을 그대로 렌더해 볼 수 있다
        // (Level은 Revit 문서 없이 만들 수 없어서, 그대로 뒀다면 목록이 빈 채로만 확인됐을 것이다).
        internal class LevelChoice
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public double Elevation { get; set; }
        }

        // 아래에서 위로 - 사람이 층을 고르는 순서 그대로(설정 창의 레벨 목록과 같은 방침).
        public static List<LevelChoice> CollectLevels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new LevelChoice { Id = l.Id.ToInt(), Name = l.Name, Elevation = l.Elevation })
                .ToList();

        private static List<Level> ResolveLevels(Document doc, IEnumerable<int> ids)
        {
            HashSet<int> wanted = new HashSet<int>(ids);
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => wanted.Contains(l.Id.ToInt()))
                .OrderBy(l => l.Elevation)
                .ToList();
        }

        private static IEnumerable<Wall> HostWalls(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>();

        private static string WallTypeName(Wall wall)
        {
            try { return wall.WallType?.Name ?? ""; }
            catch { return ""; }
        }

        private static IEnumerable<(RevitLinkInstance Link, Document LinkDoc)> LoadedLinks(Document doc)
        {
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? linkDoc = null;
                try { linkDoc = link.GetLinkDocument(); }
                catch { linkDoc = null; }   // 언로드된 링크는 조용히 건너뛴다
                if (linkDoc != null) yield return (link, linkDoc);
            }
        }

        private static string LinkName(RevitLinkInstance link)
        {
            try { return link.Name; }
            catch { return "링크"; }
        }

        // ===== 중심선 계산 (순수 계산 - 일부러 Revit 타입을 쓰지 않는다) =====
        //
        // 벽의 위치선(Location Line)은 유형마다 다르다. "중심선에 맞춰서"라는 요청을 정확히 지키려면,
        // 위치선이 마감면/코어면인 벽은 그만큼 옮겨서 진짜 중심선을 써야 한다.
        //
        // 좌표 약속: 벽 **외부면에서 안쪽으로** 잰 거리를 d라 하자(외부면 d=0, 내부면 d=W).
        // 중심선은 d=W/2에 있다. Wall.Orientation은 **외부를 향한 법선**이므로, 위치선에서 중심선으로
        // 가려면 그 법선 방향으로 (d - W/2)만큼 옮기면 된다(음수면 안쪽으로).
        //
        // 이 계산을 Revit 타입 없이 떼어 둔 이유는 하네스에서 직접 돌려 검증하기 위해서다
        // (층별 단면상자의 RejectOutliersAndUnion과 같은 이유).
        internal static double CenterlineShift(double totalWidth, double widthBeforeCore, double coreWidth, int locationLine)
        {
            double d = locationLine switch
            {
                0 => totalWidth / 2,                        // WallCenterline
                1 => widthBeforeCore + coreWidth / 2,       // CoreCenterline
                2 => 0,                                     // FinishFaceExterior
                3 => totalWidth,                            // FinishFaceInterior
                4 => widthBeforeCore,                       // CoreExterior
                5 => widthBeforeCore + coreWidth,           // CoreInterior
                _ => totalWidth / 2,                        // 모르는 값이면 중심선으로 간주(옮기지 않음)
            };
            return d - totalWidth / 2;
        }

        // 벽 하나의 위치선 → 중심선. 실패하면 위치선을 그대로 돌려준다(그리지 못하는 것보다 낫다).
        private static Curve ToCenterline(Wall wall, Curve locationCurve)
        {
            try
            {
                Parameter? p = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                int locationLine = p?.AsInteger() ?? 0;
                if (locationLine == 0) return locationCurve;   // 이미 중심선 - 가장 흔한 경우

                double totalWidth = wall.Width;
                double widthBeforeCore = 0;
                double coreWidth = totalWidth;

                CompoundStructure? cs = wall.WallType?.GetCompoundStructure();
                if (cs != null)
                {
                    IList<CompoundStructureLayer> layers = cs.GetLayers();
                    int firstCore = cs.GetFirstCoreLayerIndex();
                    int lastCore = cs.GetLastCoreLayerIndex();
                    if (layers.Count > 0 && firstCore >= 0 && lastCore >= firstCore)
                    {
                        widthBeforeCore = layers.Take(firstCore).Sum(l => l.Width);
                        coreWidth = layers.Skip(firstCore).Take(lastCore - firstCore + 1).Sum(l => l.Width);
                    }
                }

                double shift = CenterlineShift(totalWidth, widthBeforeCore, coreWidth, locationLine);
                if (Math.Abs(shift) < 1e-9) return locationCurve;

                XYZ normal = wall.Orientation;   // 외부를 향한 법선(벽을 뒤집으면 이 값도 뒤집힌다)
                return locationCurve.CreateTransformed(Transform.CreateTranslation(normal * shift));
            }
            catch
            {
                return locationCurve;
            }
        }

        // ===== 실행 =====

        public class RoomSeparatorResult
        {
            public int Created { get; set; }
            public int SkippedWalls { get; set; }
            public List<string> LevelsWithoutPlanView { get; } = new List<string>();
            public List<string> Notes { get; } = new List<string>();
        }

        // 고른 유형·레벨로 룸 구분선을 만든다. **호출하는 쪽이 트랜잭션을 연다**(이 프로젝트의 관례).
        public static RoomSeparatorResult Create(
            Document doc,
            HashSet<string> typeNames,
            IEnumerable<int> levelIds,
            bool includeLinks)
        {
            RoomSeparatorResult result = new RoomSeparatorResult();
            List<Level> levels = ResolveLevels(doc, levelIds);
            if (typeNames.Count == 0 || levels.Count == 0) return result;

            List<(RevitLinkInstance Link, Document LinkDoc)> links =
                includeLinks ? LoadedLinks(doc).ToList() : new List<(RevitLinkInstance, Document)>();

            foreach (Level level in levels)
            {
                ViewPlan? view = FindPlanView(doc, level);
                if (view == null)
                {
                    // 룸 구분선은 평면뷰에서만 만들 수 있다 - 그 레벨에 평면뷰가 하나도 없으면 건너뛴다.
                    result.LevelsWithoutPlanView.Add(level.Name);
                    continue;
                }

                List<Curve> curves = new List<Curve>();

                foreach (Wall wall in HostWalls(doc))
                {
                    if (!typeNames.Contains(WallTypeName(wall))) continue;
                    if (BaseLevelId(wall) != level.Id) continue;
                    Curve? curve = CenterlineOnLevel(wall, level.Elevation, null);
                    if (curve != null) curves.Add(curve); else result.SkippedWalls++;
                }

                foreach ((RevitLinkInstance link, Document linkDoc) in links)
                {
                    Transform transform = link.GetTotalTransform();
                    foreach (Wall wall in HostWalls(linkDoc))
                    {
                        if (!typeNames.Contains(WallTypeName(wall))) continue;
                        if (!LinkWallIsOnLevel(linkDoc, wall, transform, level)) continue;
                        Curve? curve = CenterlineOnLevel(wall, level.Elevation, transform);
                        if (curve != null) curves.Add(curve); else result.SkippedWalls++;
                    }
                }

                if (curves.Count == 0) continue;
                result.Created += CreateLines(doc, view, level, curves, result);
            }

            return result;
        }

        // 벽의 중심선을 레벨 높이에 눕힌 곡선. 링크 벽이면 마지막에 링크 변환까지 적용한다.
        private static Curve? CenterlineOnLevel(Wall wall, double levelElevation, Transform? linkTransform)
        {
            try
            {
                if (wall.Location is not LocationCurve location || location.Curve == null) return null;

                // 중심선 보정은 **링크 문서 좌표계에서** 한다 - 그래야 Wall.Orientation과 곡선의 좌표계가
                // 서로 맞는다. 호스트 좌표로 옮기는 것은 그 다음 한 번에 처리한다.
                Curve curve = ToCenterline(wall, location.Curve);
                if (linkTransform != null) curve = curve.CreateTransformed(linkTransform);

                // 룸 구분선은 레벨의 스케치 평면 위에 있어야 한다 - 벽의 베이스 간격띄우기 때문에 곡선이
                // 레벨보다 위/아래에 있을 수 있으므로 Z만 레벨 높이로 옮긴다(벽 위치선은 항상 수평이라
                // 이 평행이동으로 충분하다).
                double z = curve.GetEndPoint(0).Z;
                double dz = levelElevation - z;
                if (Math.Abs(dz) > 1e-9)
                    curve = curve.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, dz)));

                return curve.Length > 1e-6 ? curve : null;
            }
            catch
            {
                return null;
            }
        }

        private static ElementId BaseLevelId(Wall wall)
        {
            try { return wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId; }
            catch { return ElementId.InvalidElementId; }
        }

        // 링크 벽이 이 호스트 레벨에 속하는가 - 링크의 레벨 높이를 **호스트 좌표로 환산해서** 비교한다
        // (링크가 위아래로 옮겨져 배치돼 있을 수 있으므로 링크 문서의 높이를 그대로 비교하면 틀린다).
        private static bool LinkWallIsOnLevel(Document linkDoc, Wall wall, Transform transform, Level level)
        {
            try
            {
                ElementId baseId = BaseLevelId(wall);
                if (baseId == ElementId.InvalidElementId) return false;
                if (linkDoc.GetElement(baseId) is not Level linkLevel) return false;

                double hostZ = transform.OfPoint(new XYZ(0, 0, linkLevel.Elevation)).Z;
                return Math.Abs(hostZ - level.Elevation) <= LinkLevelToleranceFeet;
            }
            catch
            {
                return false;
            }
        }

        // 그 레벨의 평면뷰 하나 - 뷰 템플릿이 아닌 실제 평면뷰만 쓴다.
        private static ViewPlan? FindPlanView(Document doc, Level level)
        {
            List<ViewPlan> plans = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id)
                .ToList();

            // 지금 보고 있는 뷰가 그 레벨의 평면뷰면 그걸 쓴다(사용자가 결과를 바로 보는 뷰).
            if (doc.ActiveView is ViewPlan active && plans.Any(v => v.Id == active.Id)) return active;
            return plans.FirstOrDefault();
        }

        // 한 레벨분을 한 번에 만들고, 실패하면 하나씩 다시 시도해 문제 있는 곡선만 버린다 -
        // 곡선 하나가 잘못돼 전체가 통째로 실패하면 "아무것도 안 생긴다"가 되기 때문이다.
        private static int CreateLines(Document doc, ViewPlan view, Level level, List<Curve> curves, RoomSeparatorResult result)
        {
            Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation));
            SketchPlane sketchPlane = SketchPlane.Create(doc, plane);

            try
            {
                CurveArray array = new CurveArray();
                foreach (Curve curve in curves) array.Append(curve);
                ModelCurveArray created = doc.Create.NewRoomBoundaryLines(sketchPlane, array, view);
                return created?.Size ?? 0;
            }
            catch
            {
                int made = 0;
                foreach (Curve curve in curves)
                {
                    try
                    {
                        CurveArray one = new CurveArray();
                        one.Append(curve);
                        ModelCurveArray created = doc.Create.NewRoomBoundaryLines(sketchPlane, one, view);
                        made += created?.Size ?? 0;
                    }
                    catch
                    {
                        result.SkippedWalls++;
                    }
                }
                result.Notes.Add(level.Name + ": 일부 곡선을 한 번에 만들 수 없어 하나씩 다시 시도했습니다.");
                return made;
            }
        }
    }
}
