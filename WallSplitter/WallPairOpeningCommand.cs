using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using static WallSplitter.StructuralWallOpeningCommand;
using OpeningRectangle = WallSplitter.StructuralWallOpeningCommand.Rectangle;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public sealed class WallPairOpeningCommand : IExternalCommand
    {
        private const string Title = "벽 쌍 개구부";
        private static readonly Dictionary<string, PendingRehost> Pending = new(StringComparer.Ordinal);
        private static double Mm(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var ui = commandData.Application.ActiveUIDocument;
            if (ui == null || ui.Document.IsFamilyDocument || ui.Document.IsReadOnly)
                return Result.Cancelled;
            var doc = ui.Document;
            try
            {
                string key = doc.ProjectInformation.UniqueId + "|" + doc.PathName + "|" + doc.Title;
                if (Pending.TryGetValue(key, out var pending))
                {
                    var resume = new TaskDialog(Title)
                    {
                        MainInstruction = "새 호스트 선택 후 구조벽 개구부 만들기",
                        CommonButtons = TaskDialogCommonButtons.Cancel
                    };
                    resume.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "호스트 이동 확인하고 이어서 진행");
                    resume.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "새로 선택한 벽으로 작업 시작");
                    var action = resume.Show();
                    if (action == TaskDialogResult.CommandLink1)
                        return Resume(ui, key, pending);
                    if (action != TaskDialogResult.CommandLink2) return Result.Cancelled;
                    Pending.Remove(key);
                }

                var walls = ui.Selection.GetElementIds().Select(doc.GetElement).OfType<Wall>().ToList();
                if (walls.Count < 2)
                    walls = ui.Selection.PickObjects(ObjectType.Element, new PickFilter(e => e is Wall),
                        "개구부를 맞출 벽을 두 개 이상 선택하고 완료를 누르세요.").Select(doc.GetElement).OfType<Wall>().ToList();
                walls = walls.GroupBy(w => w.Id).Select(g => g.First()).ToList();
                if (walls.Count < 2 || !walls.Any(IsStructuralWall))
                    throw new InvalidOperationException("현재 모델에서 구조벽을 포함해 서로 다른 벽을 두 개 이상 선택하세요.");
                return ReviewSelection(ui, key, walls);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, ex.Message);
                return Result.Cancelled;
            }
        }

        private static Result ReviewSelection(UIDocument ui, string key, List<Wall> walls)
        {
            var doc = ui.Document;
            var (frames, moves, direct, skipped) = PlanSelection(doc, walls);
            if (moves.Count == 0) return ReviewOpenings(ui, frames, direct, skipped);
            var frameIndex = frames.ToDictionary(f => f.Wall.UniqueId);
            var dialog = new TaskDialog(Title)
            {
                MainInstruction = $"호스트를 옮길 문·창 {moves.Count}개 · 바로 타공할 대상 {direct.Count}개",
                MainContent = "선택된 문·창을 Revit의 ‘새 호스트 선택’으로 표시된 건축벽에 옮기세요. 벽을 따라 잰 위치와 높이를 유지하고 이 버튼을 다시 실행하세요. 남은 문·창이 있으면 다음 항목을 표시합니다.",
                ExpandedContent = string.Join("\n", moves.Select(p => p.Description(frameIndex[p.Architecture].Wall)).Concat(skipped).Take(60)),
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel
            };
            if (dialog.Show() != TaskDialogResult.Ok) return Result.Cancelled;
            var involved = new HashSet<string>(moves.SelectMany(m => new[] { m.Structure, m.Architecture })
                .Concat(direct.SelectMany(d => new[] { d.Host, d.Target })));
            var stamps = frames.Where(f => involved.Contains(f.Wall.UniqueId)).ToDictionary(f => f.Wall.UniqueId, f => f.Stamp);
            var originals = moves.Select(m => m.Architecture).Distinct().ToDictionary(id => id,
                id => new HashSet<string>(HostedDoorsAndWindows(doc, frameIndex[id].Wall).Select(i => i.UniqueId)));
            Pending[key] = new PendingRehost(stamps, moves, direct, originals, skipped);
            TrySelect(ui, new[] { doc.GetElement(moves[0].UniqueId).Id });
            return Result.Succeeded;
        }

        private static (List<WallFrame> Frames, List<RehostItem> Moves, List<OpeningRoute> Direct, List<string> Skipped)
            PlanSelection(Document doc, IEnumerable<Wall> walls)
        {
            var frames = new List<WallFrame>();
            var skipped = new List<string>();
            foreach (var wall in walls)
            {
                try { frames.Add(new WallFrame(wall)); }
                catch (Exception ex) { skipped.Add($"벽 {wall.Id} · {ex.Message}"); }
            }
            var reader = FindCutReader();
            var moves = new List<RehostItem>();
            var direct = new List<OpeningRoute>();
            foreach (var source in frames)
            {
                var neighbors = frames.Where(target => target.Wall.Id != source.Wall.Id &&
                    (source.Structural || target.Structural) && Neighboring(source, target)).ToList();
                foreach (var instance in HostedDoorsAndWindows(doc, source.Wall))
                {
                    var matches = new List<(WallFrame Wall, OpeningRectangle Rect)>();
                    string? reason = null;
                    foreach (var target in neighbors)
                    {
                        try
                        {
                            RequireEditableWall(target.Wall);
                            if (!target.Structural)
                            {
                                RequireEditableWall(source.Wall);
                                if (instance.Pinned || instance.GroupId != ElementId.InvalidElementId)
                                    throw new InvalidOperationException("고정되어 있거나 그룹에 속한 문·창입니다.");
                            }
                            var rect = GetRectangle(doc, instance, target.Wall, target.Origin, target.Along, target.Normal, target.Depth, reader);
                            PrepareOpening(target, instance.Id, rect);
                            matches.Add((target, rect));
                        }
                        catch (Exception ex) { reason ??= ex.Message; }
                    }
                    if (matches.Count != 1)
                    {
                        skipped.Add($"{instance.Id} · " + (matches.Count > 1
                            ? "이 위치에서 조건을 만족하는 상대 벽이 여러 개입니다. 해당 벽만 다시 선택하세요."
                            : reason ?? "선택한 벽 중 이 문·창에 맞는 인접 벽을 찾지 못했습니다."));
                        continue;
                    }
                    var match = matches[0];
                    if (match.Wall.Structural)
                        direct.Add(new OpeningRoute(instance.UniqueId, source.Wall.UniqueId, match.Wall.Wall.UniqueId));
                    else if (moves.Any(m => m.Architecture == match.Wall.Wall.UniqueId && m.Rect.Intersects(match.Rect)))
                        skipped.Add($"{instance.Id} · 같은 건축벽으로 옮길 다른 문·창과 겹칩니다.");
                    else
                        moves.Add(new RehostItem(instance, source.Wall.UniqueId, match.Wall.Wall.UniqueId, match.Rect));
                }
            }
            return (frames, moves, direct, skipped);
        }

        private static bool Neighboring(WallFrame source, WallFrame target)
        {
            if (source.Along.CrossProduct(target.Along).GetLength() > 1e-5) return false;
            var x0 = (source.Origin - target.Origin).DotProduct(target.Along);
            var x1 = (source.Axis.GetEndPoint(1) - target.Origin).DotProduct(target.Along);
            if (Math.Max(x0, x1) <= 0 || Math.Min(x0, x1) >= target.Axis.Length ||
                source.Box.Min.Z >= target.Box.Max.Z || source.Box.Max.Z <= target.Box.Min.Z) return false;
            var originOffset = (source.Origin - target.Origin).DotProduct(target.Normal);
            var sign = source.Normal.DotProduct(target.Normal);
            var n0 = originOffset + source.Depth.Min * sign;
            var n1 = originOffset + source.Depth.Max * sign;
            return Math.Max(Math.Min(n0, n1) - target.Depth.Max, target.Depth.Min - Math.Max(n0, n1))
                <= ContactTolerance + Mm(0.001);
        }

        private static Result Resume(UIDocument ui, string key, PendingRehost pending)
        {
            var doc = ui.Document;
            var frames = new Dictionary<string, WallFrame>();
            foreach (var stamp in pending.Walls)
            {
                if (doc.GetElement(stamp.Key) is not Wall wall)
                    throw new InvalidOperationException("이전에 선택한 벽이 없어졌습니다. 새 벽으로 작업을 시작하세요.");
                var frame = new WallFrame(wall);
                if (frame.Stamp != stamp.Value)
                    throw new InvalidOperationException("벽 위치, 두께 또는 구조 설정이 바뀌었습니다. 새 벽으로 작업을 시작하세요.");
                frames.Add(stamp.Key, frame);
            }
            var reader = FindCutReader();
            var available = pending.OriginalArchitectureItems.Keys.ToDictionary(id => id,
                id => HostedDoorsAndWindows(doc, frames[id].Wall).ToList());
            var verified = new HashSet<ElementId>();
            var routes = pending.Direct.ToList();
            var incomplete = new List<string>();
            var toSelect = new List<ElementId>();
            foreach (var plan in pending.Items)
            {
                var arch = frames[plan.Architecture];
                var structure = frames[plan.Structure];
                RequireContact(arch.Wall, structure.Origin, structure.Along, structure.Normal, structure.Depth);
                var original = doc.GetElement(plan.UniqueId) as FamilyInstance;
                var matches = original != null
                    ? available[plan.Architecture].Where(i => i.UniqueId == original.UniqueId)
                    : available[plan.Architecture].Where(i => !pending.OriginalArchitectureItems[plan.Architecture].Contains(i.UniqueId));
                var confirmed = matches.Where(i => !verified.Contains(i.Id) && plan.Matches(doc, i, arch, reader)).Take(2).ToList();
                if (confirmed.Count == 1)
                {
                    verified.Add(confirmed[0].Id);
                    routes.Add(new OpeningRoute(confirmed[0].UniqueId, plan.Architecture, plan.Structure));
                }
                else
                {
                    incomplete.Add(plan.Description(arch.Wall));
                    if (original != null) toSelect.Add(original.Id);
                }
            }
            if (incomplete.Count != 0)
            {
                if (toSelect.Count > 0) TrySelect(ui, toSelect.Take(1).ToList());
                var dialog = new TaskDialog(Title)
                {
                    MainInstruction = $"호스트 이동 확인 필요 {incomplete.Count}개",
                    MainContent = "표시된 건축벽에 옮겼는지 확인하세요. 이동 전 유형, 마크, 열림 방향 및 개구부 위치·크기가 일치해야 이어서 진행할 수 있습니다.",
                    ExpandedContent = string.Join("\n", incomplete.Take(30)),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                dialog.Show();
                return Result.Cancelled;
            }
            var result = ReviewOpenings(ui, frames.Values, routes, pending.Skipped.ToList());
            if (result == Result.Succeeded) Pending.Remove(key);
            return result;
        }

        private static IEnumerable<FamilyInstance> HostedDoorsAndWindows(Document doc, Wall wall) =>
            wall.FindInserts(false, false, false, false).Select(doc.GetElement).OfType<FamilyInstance>()
                .Where(i => IsDoorOrWindow(i) && i.Host?.Id == wall.Id);

        private static Result ReviewOpenings(UIDocument ui, IEnumerable<WallFrame> frames,
            IEnumerable<OpeningRoute> routes, List<string> skipped)
        {
            var doc = ui.Document;
            var reader = FindCutReader();
            var index = frames.ToDictionary(f => f.Wall.UniqueId);
            var jobs = new Dictionary<WallFrame, List<OpeningCandidate>>();
            foreach (var route in routes)
            {
                var instance = doc.GetElement(route.Instance) as FamilyInstance;
                try
                {
                    if (instance?.Host?.UniqueId != route.Host)
                        throw new InvalidOperationException("문·창의 호스트가 바뀌었습니다.");
                    var target = index[route.Target];
                    RequireEditableWall(target.Wall);
                    var rect = GetRectangle(doc, instance, target.Wall, target.Origin, target.Along, target.Normal, target.Depth, reader);
                    var job = PrepareOpening(target, instance.Id, rect);
                    if (job == null)
                    {
                        skipped.Add($"{instance.Id} · 구조벽 {target.Wall.Id}의 같은 위치가 이미 뚫려 있습니다.");
                        continue;
                    }
                    if (!jobs.TryGetValue(target, out var entries)) jobs[target] = entries = new List<OpeningCandidate>();
                    if (entries.Any(e => e.Rect.Intersects(rect)))
                        throw new InvalidOperationException("같은 벽에 만들 다른 개구부와 겹칩니다.");
                    entries.Add(job);
                }
                catch (Exception ex) { skipped.Add($"{instance?.Id.ToString() ?? route.Instance} · {ex.Message}"); }
            }
            var descriptions = jobs.SelectMany(j => j.Value.Select(c => $"{c.Source} → 벽 {j.Key.Wall.Id} · {Dimensions(c.Rect)}")).ToList();
            bool hasWork = descriptions.Count > 0;
            var preview = new TaskDialog(Title)
            {
                MainInstruction = $"생성 {descriptions.Count}개 · 제외 {skipped.Count}개",
                MainContent = hasWork ? "문·창의 실제 직사각형 윤곽으로 반대쪽 구조벽에 관통 개구부를 만듭니다." : "추가로 생성할 개구부가 없습니다.",
                ExpandedContent = string.Join("\n", descriptions.Take(30).Concat(skipped.Take(30))) +
                    (descriptions.Count > 30 || skipped.Count > 30 ? "\n생성·제외 항목별 최대 30개를 표시합니다." : ""),
                CommonButtons = hasWork ? TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel : TaskDialogCommonButtons.Close,
                DefaultButton = hasWork ? TaskDialogResult.Cancel : TaskDialogResult.Close
            };
            var decision = preview.Show();
            if (!hasWork) return Result.Succeeded;
            if (decision != TaskDialogResult.Ok) return Result.Cancelled;
            using var group = new TransactionGroup(doc, Title);
            if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("구조벽 편집을 시작할 수 없습니다.");
            var created = new List<ElementId>();
            try
            {
                foreach (var job in jobs)
                {
                    var current = new WallFrame(job.Key.Wall);
                    if (current.Stamp != job.Key.Stamp)
                        throw new InvalidOperationException("벽 위치나 두께가 바뀌었습니다. 벽을 다시 선택하세요.");
                    var ready = new List<OpeningCandidate>();
                    foreach (var candidate in job.Value)
                    {
                        var checkedCandidate = PrepareOpening(current, candidate.Source, candidate.Rect);
                        if (checkedCandidate != null) ready.Add(checkedCandidate);
                        else skipped.Add($"{candidate.Source} · 같은 위치가 이미 뚫려 있습니다.");
                    }
                    if (ready.Count > 0) created.AddRange(Apply(doc, current.Wall, current.Center, current.Along, ready));
                }
            }
            catch
            {
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                throw;
            }
            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("구조벽 개구부를 확정할 수 없습니다.");
            TrySelect(ui, created);
            TaskDialog.Show(Title, $"개구부 {created.Count}개를 생성했습니다. 제외 {skipped.Count}개.");
            return Result.Succeeded;
        }

        private static void TrySelect(UIDocument ui, ICollection<ElementId> ids)
        {
            try { ui.Selection.SetElementIds(ids); }
            catch (Exception) { /* 선택 표시 실패는 개구부 결과와 별개다. */ }
        }

        private static void RequireEditableWall(Wall wall)
        {
            if (wall.Pinned || wall.GroupId != ElementId.InvalidElementId)
                throw new InvalidOperationException($"벽 {wall.Id}이(가) 고정되어 있거나 그룹에 속해 있습니다.");
        }

        private static OpeningRectangle GetRectangle(Document doc, FamilyInstance instance, Wall target, XYZ origin,
            XYZ along, XYZ normal, (double Min, double Max) targetDepth, MethodInfo getCut)
        {
            if (instance.Host is not Wall source || source.Id == target.Id)
                throw new InvalidOperationException("반대쪽 벽에 호스트된 문·창을 선택하세요.");
            RequireContact(source, origin, along, normal, targetDepth);
            return ReadCutout(doc, source, instance, origin, along, normal, getCut);
        }

        private static OpeningRectangle ReadCutout(Document doc, Wall source, FamilyInstance instance, XYZ origin,
            XYZ along, XYZ normal, MethodInfo getCut)
        {
            object?[] arguments = { doc, source, instance, null };
            CurveLoop? loop;
            try { loop = getCut.Invoke(null, arguments) as CurveLoop; }
            catch (TargetInvocationException ex)
            { throw new InvalidOperationException("호스트 벽에서 문·창의 실제 개구부 윤곽을 읽을 수 없습니다.", ex.InnerException); }
            if (loop == null || arguments[3] is not XYZ cutDirection ||
                cutDirection.GetLength() < 1e-9 || Math.Abs(cutDirection.Normalize().DotProduct(normal)) < 0.999999)
                throw new InvalidOperationException("호스트 벽을 직각으로 관통하는 문·창 개구부를 확인할 수 없습니다.");
            return ReadRectangle(loop, origin, along, normal);
        }

        private static void RequireContact(Wall source, XYZ origin, XYZ along, XYZ normal, (double Min, double Max) targetDepth)
        {
            var axis = RequireStraightWall(source);
            if (axis.Direction.CrossProduct(along).GetLength() > 1e-5)
                throw new InvalidOperationException("두 벽이 평행하지 않습니다.");
            var sourceDepth = SideDepth(source, origin, normal);
            var gap = Math.Max(sourceDepth.Min - targetDepth.Max, targetDepth.Min - sourceDepth.Max);
            if (gap > ContactTolerance + Mm(0.001))
                throw new InvalidOperationException("두 벽 사이가 5 mm보다 멉니다.");
        }

        private static OpeningCandidate? PrepareOpening(WallFrame target, ElementId source, OpeningRectangle rect)
        {
            var box = target.Box;
            var alongEnd = (target.Axis.GetEndPoint(1) - target.Origin).DotProduct(target.Along);
            if (rect.Left < -GeometryTolerance || rect.Right > alongEnd + GeometryTolerance ||
                rect.Bottom < box.Min.Z - target.Origin.Z - GeometryTolerance ||
                rect.Top > box.Max.Z - target.Origin.Z + GeometryTolerance)
                throw new InvalidOperationException("문·창 위치가 반대쪽 벽의 경계를 벗어납니다.");
            var probe = BuildProbe(rect, target.Origin, target.Along, target.Normal, target.Depth);
            var fullVolume = (rect.Width - 2 * ProbeInset) * (rect.Height - 2 * ProbeInset) * (target.Depth.Max - target.Depth.Min);
            var volume = OccupiedVolume(target.Solids, probe);
            if (volume <= fullVolume * 1e-7) return null;
            if (volume < fullVolume * 0.99999)
                throw new InvalidOperationException("반대쪽 벽이 개구부 범위를 일부만 덮거나 기존 개구부와 일부 겹칩니다.");
            return new OpeningCandidate(source, rect, probe, fullVolume * 1e-7);
        }

        private static string Dimensions(OpeningRectangle rect)
        {
            double Millimetres(double length) => UnitUtils.ConvertFromInternalUnits(length, UnitTypeId.Millimeters);
            return $"{Millimetres(rect.Width):0.0} × {Millimetres(rect.Height):0.0} mm";
        }

        private sealed class WallFrame
        {
            private List<Solid>? _solids;
            public WallFrame(Wall wall)
            {
                Wall = wall;
                Axis = RequireStraightWall(wall);
                Origin = Axis.GetEndPoint(0);
                Along = Axis.Direction;
                Normal = Along.CrossProduct(XYZ.BasisZ).Normalize();
                Depth = SideDepth(wall, Origin, Normal);
                Structural = IsStructuralWall(wall);
                Box = wall.get_BoundingBox(null) ?? throw new InvalidOperationException("벽 경계를 확인할 수 없습니다.");
            }
            public Wall Wall { get; }
            public bool Structural { get; }
            public BoundingBoxXYZ Box { get; }
            public Line Axis { get; }
            public XYZ Origin { get; }
            public XYZ Along { get; }
            public XYZ Normal { get; }
            public (double Min, double Max) Depth { get; }
            public XYZ Center => Origin + Normal * ((Depth.Min + Depth.Max) / 2);
            public List<Solid> Solids => _solids ??= WallSolids(Wall);
            public string Stamp => VectorStamp(Origin) + "|" + VectorStamp(Axis.GetEndPoint(1)) +
                FormattableString.Invariant($"|{Depth.Min:F6}|{Depth.Max:F6}|{Box.Min.Z:F6}|{Box.Max.Z:F6}|{Structural}");
        }

        private static string VectorStamp(XYZ v) => FormattableString.Invariant($"{v.X:F6}|{v.Y:F6}|{v.Z:F6}");

        private sealed class OpeningRoute
        {
            public OpeningRoute(string instance, string host, string target)
            {
                Instance = instance;
                Host = host;
                Target = target;
            }

            public string Instance { get; }
            public string Host { get; }
            public string Target { get; }
        }

        private sealed class PendingRehost
        {
            public PendingRehost(Dictionary<string, string> walls, List<RehostItem> items,
                List<OpeningRoute> direct, Dictionary<string, HashSet<string>> originalArchitectureItems, List<string> skipped)
            {
                Walls = walls;
                Items = items;
                Direct = direct;
                OriginalArchitectureItems = originalArchitectureItems;
                Skipped = skipped;
            }

            public Dictionary<string, string> Walls { get; }
            public List<RehostItem> Items { get; }
            public List<OpeningRoute> Direct { get; }
            public Dictionary<string, HashSet<string>> OriginalArchitectureItems { get; }
            public List<string> Skipped { get; }
        }

        private sealed class RehostItem
        {
            private readonly string _type;
            private readonly string _mark;
            private readonly string _orientation;
            public RehostItem(FamilyInstance item, string structure, string architecture, OpeningRectangle rect)
            {
                UniqueId = item.UniqueId;
                Label = item.Id.ToString();
                Structure = structure;
                Architecture = architecture;
                Rect = rect;
                _type = item.Symbol.UniqueId;
                _mark = Mark(item);
                _orientation = Orientation(item);
            }
            public string UniqueId { get; }
            public string Label { get; }
            public string Structure { get; }
            public string Architecture { get; }
            public string Description(Wall wall) => $"{Label} → 건축벽 {wall.Id} · {Dimensions(Rect)}";
            public OpeningRectangle Rect { get; }
            public bool Matches(Document doc, FamilyInstance item, WallFrame frame, MethodInfo reader)
            {
                if (item.Symbol.UniqueId != _type || Mark(item) != _mark || Orientation(item) != _orientation) return false;
                try
                {
                    var rect = ReadCutout(doc, frame.Wall, item, frame.Origin, frame.Along, frame.Normal, reader);
                    return Math.Abs(Rect.Left - rect.Left) <= GeometryTolerance && Math.Abs(Rect.Right - rect.Right) <= GeometryTolerance &&
                        Math.Abs(Rect.Bottom - rect.Bottom) <= GeometryTolerance && Math.Abs(Rect.Top - rect.Top) <= GeometryTolerance;
                }
                catch (Exception) { return false; }
            }
            private static string Mark(FamilyInstance i) => i.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
            private static string Orientation(FamilyInstance i) => VectorStamp(i.FacingOrientation) + "|" + VectorStamp(i.HandOrientation);
        }

    }
}
