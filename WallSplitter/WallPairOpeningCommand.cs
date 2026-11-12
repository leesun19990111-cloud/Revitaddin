using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using static WallSplitter.StructuralWallOpeningCommand;
using OpeningRectangle = WallSplitter.StructuralWallOpeningCommand.Rectangle;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public sealed class WallPairOpeningCommand : IExternalCommand
    {
        private const string PairTitle = "벽 쌍 개구부";
        private static readonly Dictionary<string, PendingRehost> Pending = new(StringComparer.Ordinal);
        private static double Mm(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            Execute(commandData.Application.ActiveUIDocument, moveHost: true);

        internal static Result Execute(UIDocument? ui, bool moveHost)
        {
            if (ui == null || ui.Document.IsFamilyDocument || ui.Document.IsReadOnly)
                return Result.Cancelled;
            var doc = ui.Document;
            var title = moveHost ? PairTitle : StructuralWallOpeningCommand.Title;
            try
            {
                string key = doc.ProjectInformation.UniqueId + "|" + doc.PathName + "|" + doc.Title;
                if (moveHost && Pending.TryGetValue(key, out var pending))
                {
                    var resume = new TaskDialog(PairTitle)
                    {
                        MainInstruction = "호스트 이동을 확인한 뒤 원래 벽에 개구부 만들기",
                        CommonButtons = TaskDialogCommonButtons.Cancel
                    };
                    resume.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "호스트 이동 확인하고 이어서 진행");
                    resume.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "새로 선택한 두 벽으로 작업 시작");
                    var action = resume.Show();
                    if (action == TaskDialogResult.CommandLink1)
                        return Resume(ui, key, pending);
                    if (action != TaskDialogResult.CommandLink2) return Result.Cancelled;
                }

                var walls = ui.Selection.GetElementIds().Select(doc.GetElement).OfType<Wall>().ToList();
                if (walls.Count != 2)
                    throw new InvalidOperationException("현재 모델에서 벽 두 개를 먼저 선택한 뒤 버튼을 누르세요.");
                if (moveHost) Pending.Remove(key);
                var (frames, moves, direct, skipped) = PlanSelection(doc, walls[0], walls[1], moveHost);
                if (moves.Count == 0) return ReviewOpenings(ui, frames, direct, skipped, title);
                return StartRehost(ui, key, frames, moves, skipped);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show(title, ex.Message);
                return Result.Cancelled;
            }
        }

        private static Result StartRehost(UIDocument ui, string key, WallFrame[] frames,
            List<RehostItem> moves, List<string> skipped)
        {
            var doc = ui.Document;
            var dialog = new TaskDialog(PairTitle)
            {
                MainInstruction = $"호스트를 옮길 문·창 {moves.Count}개 · 제외 {skipped.Count}개",
                MainContent = "선택 표시된 문·창에 Revit의 ‘새 호스트 선택’을 실행해 상세정보에 표시된 상대 벽으로 옮기세요. 위치와 높이를 유지한 뒤 이 버튼을 다시 누르세요. 남은 문·창이 있으면 다음 항목을 선택합니다.",
                ExpandedContent = string.Join("\n", moves.Select(p => p.Description()).Concat(skipped).Take(60)),
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel
            };
            if (dialog.Show() != TaskDialogResult.Ok) return Result.Cancelled;
            var stamps = frames.ToDictionary(f => f.Wall.UniqueId, f => f.Stamp);
            var originals = frames.ToDictionary(f => f.Wall.UniqueId,
                f => new HashSet<string>(HostedDoorsAndWindows(doc, f.Wall).Select(i => i.UniqueId)));
            Pending[key] = new PendingRehost(stamps, moves, originals, skipped);
            TrySelect(ui, new[] { doc.GetElement(moves[0].UniqueId).Id });
            return Result.Succeeded;
        }

        private static (WallFrame[] Frames, List<RehostItem> Moves, List<OpeningRoute> Direct, List<string> Skipped)
            PlanSelection(Document doc, Wall first, Wall second, bool moveHost)
        {
            var frames = new[] { new WallFrame(first), new WallFrame(second) };
            var a = frames[0];
            var b = frames[1];
            RequireContact(a.Wall, b.Origin, b.Along, b.Normal, b.Depth);
            var reader = FindCutReader();
            var moves = new List<RehostItem>();
            var direct = new List<OpeningRoute>();
            var skipped = new List<string>();
            for (int i = 0; i < frames.Length; i++)
            {
                var source = frames[i];
                var target = frames[1 - i];
                foreach (var instance in HostedDoorsAndWindows(doc, source.Wall))
                {
                    try
                    {
                        RequireEditableWall(target.Wall);
                        var rect = ReadCutout(doc, source.Wall, instance, target.Origin, target.Along, target.Normal, reader);
                        PrepareOpening(target, instance.Id, rect);
                        if (!moveHost)
                        {
                            direct.Add(new OpeningRoute(instance.UniqueId, source.Wall.UniqueId, target.Wall.UniqueId));
                            continue;
                        }

                        RequireEditableWall(source.Wall);
                        if (instance.Pinned || instance.GroupId != ElementId.InvalidElementId)
                            throw new InvalidOperationException("고정되어 있거나 그룹에 속한 문·창입니다.");
                        RequireClearOfHostedInserts(doc, target, rect, reader);
                        if (moves.Any(m => m.NewHost == target.Wall.UniqueId && m.Rect.Intersects(rect)))
                            throw new InvalidOperationException("같은 벽으로 옮길 다른 문·창과 겹칩니다.");
                        var originalRect = ReadCutout(doc, source.Wall, instance, source.Origin, source.Along, source.Normal, reader);
                        moves.Add(new RehostItem(instance, source.Wall.UniqueId, target.Wall, rect, originalRect));
                    }
                    catch (Exception ex) { skipped.Add($"{instance.Id} · {ex.Message}"); }
                }
            }
            if (moves.Count == 0 && direct.Count == 0 && skipped.Count == 0)
                skipped.Add("선택한 두 벽에 호스트된 문·창이 없습니다.");
            return (frames, moves, direct, skipped);
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
                    throw new InvalidOperationException("벽 위치, 두께 또는 높이이 바뀌었습니다. 새 벽으로 작업을 시작하세요.");
                frames.Add(stamp.Key, frame);
            }
            var reader = FindCutReader();
            var available = pending.OriginalHostItems.Keys.ToDictionary(id => id,
                id => HostedDoorsAndWindows(doc, frames[id].Wall).ToList());
            var verified = new HashSet<ElementId>();
            var routes = new List<OpeningRoute>();
            var orientationChanges = new List<string>();
            var incomplete = new List<string>();
            var toSelect = new List<ElementId>();
            foreach (var plan in pending.Items)
            {
                var newHost = frames[plan.NewHost];
                var oldHost = frames[plan.OriginalHost];
                RequireContact(newHost.Wall, oldHost.Origin, oldHost.Along, oldHost.Normal, oldHost.Depth);
                var original = doc.GetElement(plan.UniqueId) as FamilyInstance;
                var matches = original != null
                    ? available[plan.NewHost].Where(i => i.UniqueId == original.UniqueId)
                    : available[plan.NewHost].Where(i => !pending.OriginalHostItems[plan.NewHost].Contains(i.UniqueId));
                var confirmed = matches.Where(i => !verified.Contains(i.Id) && plan.Matches(doc, i, newHost, reader)).Take(2).ToList();
                if (confirmed.Count == 1)
                {
                    verified.Add(confirmed[0].Id);
                    routes.Add(new OpeningRoute(confirmed[0].UniqueId, plan.NewHost, plan.OriginalHost, plan.OriginalRect));
                    if (!plan.HasSameOrientation(confirmed[0]))
                        orientationChanges.Add($"{confirmed[0].Id} · 호스트 이동으로 문·창 방향이 달라졌습니다.");
                }
                else
                {
                    incomplete.Add(plan.Description());
                    if (original != null) toSelect.Add(original.Id);
                }
            }
            if (incomplete.Count != 0)
            {
                if (toSelect.Count > 0) TrySelect(ui, toSelect.Take(1).ToList());
                var dialog = new TaskDialog(PairTitle)
                {
                    MainInstruction = $"호스트 이동 확인 필요 {incomplete.Count}개",
                    MainContent = "표시된 상대 벽에 옮겼는지 확인하세요. 이동 전 유형, 마크 및 개구부 위치·크기가 일치해야 이어서 진행할 수 있습니다.",
                    ExpandedContent = string.Join("\n", incomplete.Take(30)),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                dialog.Show();
                return Result.Cancelled;
            }
            var result = ReviewOpenings(ui, frames.Values, routes, pending.Skipped.ToList(), PairTitle, orientationChanges);
            if (result == Result.Succeeded) Pending.Remove(key);
            return result;
        }

        private static IEnumerable<FamilyInstance> HostedDoorsAndWindows(Document doc, Wall wall) =>
            wall.FindInserts(false, false, false, false).Select(doc.GetElement).OfType<FamilyInstance>()
                .Where(i => IsDoorOrWindow(i) && i.Host?.Id == wall.Id);

        private static Result ReviewOpenings(UIDocument ui, IEnumerable<WallFrame> frames,
            IEnumerable<OpeningRoute> routes, List<string> skipped, string title, List<string>? orientationChanges = null)
        {
            var doc = ui.Document;
            var reader = FindCutReader();
            var index = frames.ToDictionary(f => f.Wall.UniqueId);
            var jobs = new Dictionary<WallFrame, List<OpeningCandidate>>();
            var existing = new HashSet<ElementId>();
            foreach (var route in routes)
            {
                var instance = doc.GetElement(route.Instance) as FamilyInstance;
                try
                {
                    if (instance?.Host?.UniqueId != route.Host)
                        throw new InvalidOperationException("문·창의 호스트가 바뀌었습니다.");
                    var target = index[route.Target];
                    RequireEditableWall(target.Wall);
                    var rect = route.OriginalRect ?? GetRectangle(doc, instance, target.Wall, target.Origin, target.Along, target.Normal, target.Depth, reader);
                    if (FindMatchingOpening(doc, target, rect) is Opening existingOpening)
                    {
                        existing.Add(existingOpening.Id);
                        continue;
                    }
                    var job = PrepareOpening(target, instance.Id, rect);
                    if (!jobs.TryGetValue(target, out var entries)) jobs[target] = entries = new List<OpeningCandidate>();
                    if (entries.Any(e => e.Rect.Intersects(rect)))
                        throw new InvalidOperationException("같은 벽에 만들 다른 개구부와 겹칩니다.");
                    entries.Add(job);
                }
                catch (Exception ex) { skipped.Add($"{instance?.Id.ToString() ?? route.Instance} · {ex.Message}"); }
            }
            var descriptions = jobs.SelectMany(j => j.Value.Select(c => $"{c.Source} → 벽 {j.Key.Wall.Id} · {Dimensions(c.Rect)}")).ToList();
            bool hasWork = descriptions.Count > 0;
            var notes = orientationChanges ?? new List<string>();
            string summary = hasWork ? "문·창의 실제 윤곽으로 독립 벽 개구부를 만듭니다." :
                string.Join("\n", skipped.Take(3).DefaultIfEmpty("같은 위치와 크기의 독립 개구부가 이미 있습니다. 닫으면 해당 개구부를 선택합니다."));
            if (notes.Count > 0)
                summary += $"\n호스트 이동 후 방향이 달라진 문·창 {notes.Count}개가 있습니다. 상세정보에서 확인하세요.";
            var preview = new TaskDialog(title)
            {
                MainInstruction = $"생성 {descriptions.Count}개 · 기존 개구부 {existing.Count}개 · 제외 {skipped.Count}개",
                MainContent = summary,
                ExpandedContent = string.Join("\n", descriptions.Take(30).Concat(notes.Take(30))
                    .Concat(existing.Take(30).Select(id => $"기존 독립 개구부 {id}")).Concat(skipped.Take(30))) +
                    (descriptions.Count > 30 || skipped.Count > 30 ? "\n생성·제외 항목별 최대 30개를 표시합니다." : ""),
                CommonButtons = hasWork ? TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel : TaskDialogCommonButtons.Close,
                DefaultButton = hasWork ? TaskDialogResult.Cancel : TaskDialogResult.Close
            };
            foreach (var reason in skipped.Take(30))
                doc.Application.WriteJournalComment($"{title} 제외 · {reason.Replace('\r', ' ').Replace('\n', ' ')}", false);
            var decision = preview.Show();
            if (!hasWork)
            {
                if (existing.Count > 0) TrySelect(ui, existing);
                return Result.Succeeded;
            }
            if (decision != TaskDialogResult.Ok) return Result.Cancelled;
            using var group = new TransactionGroup(doc, title);
            if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("벽 편집을 시작할 수 없습니다.");
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
                        if (FindMatchingOpening(doc, current, candidate.Rect) is Opening existingOpening)
                            existing.Add(existingOpening.Id);
                        else
                            ready.Add(PrepareOpening(current, candidate.Source, candidate.Rect));
                    }
                    if (ready.Count > 0) created.AddRange(Apply(doc, current.Wall, current.Center, current.Along, ready, title));
                }
            }
            catch
            {
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                throw;
            }
            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("벽 개구부를 확정할 수 없습니다.");
            TrySelect(ui, created.Concat(existing).ToList());
            TaskDialog.Show(title, $"개구부 {created.Count}개를 생성했습니다. 기존 개구부 {existing.Count}개 · 제외 {skipped.Count}개.");
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

        private static OpeningCandidate PrepareOpening(WallFrame target, ElementId source, OpeningRectangle rect)
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
            // 결합 벽에서 문·창의 절단이 전파되면 부피가 0이어도 독립 Opening은 없을 수 있다.
            if (volume > fullVolume * 1e-7 && volume < fullVolume * 0.99999)
                throw new InvalidOperationException("반대쪽 벽이 개구부 범위를 일부만 덮거나 기존 개구부와 일부 겹칩니다.");
            return new OpeningCandidate(source, rect, probe, fullVolume * 1e-7);
        }

        private static Opening? FindMatchingOpening(Document doc, WallFrame target, OpeningRectangle rect)
        {
            foreach (var id in target.Wall.FindInserts(true, false, false, false))
            {
                if (doc.GetElement(id) is not Opening opening || opening.Host?.Id != target.Wall.Id ||
                    !opening.IsRectBoundary || opening.BoundaryRect is not { Count: 2 } points)
                    continue;
                var a = points[0] - target.Origin;
                var b = points[1] - target.Origin;
                var x0 = a.DotProduct(target.Along);
                var x1 = b.DotProduct(target.Along);
                var existing = new OpeningRectangle(Math.Min(x0, x1), Math.Max(x0, x1), Math.Min(a.Z, b.Z), Math.Max(a.Z, b.Z));
                if (SameRectangle(rect, existing)) return opening;
            }
            return null;
        }

        private static bool SameRectangle(OpeningRectangle a, OpeningRectangle b) =>
            Math.Abs(a.Left - b.Left) <= GeometryTolerance && Math.Abs(a.Right - b.Right) <= GeometryTolerance &&
            Math.Abs(a.Bottom - b.Bottom) <= GeometryTolerance && Math.Abs(a.Top - b.Top) <= GeometryTolerance;

        private static void RequireClearOfHostedInserts(Document doc, WallFrame target, OpeningRectangle rect, MethodInfo reader)
        {
            foreach (var other in HostedDoorsAndWindows(doc, target.Wall))
            {
                var box = other.get_BoundingBox(null);
                if (box != null)
                {
                    var xs = new[] { box.Min.X, box.Max.X };
                    var ys = new[] { box.Min.Y, box.Max.Y };
                    var along = xs.SelectMany(x => ys.Select(y => (new XYZ(x, y, target.Origin.Z) - target.Origin).DotProduct(target.Along))).ToList();
                    var bounds = new OpeningRectangle(along.Min(), along.Max(), box.Min.Z - target.Origin.Z, box.Max.Z - target.Origin.Z);
                    if (!bounds.Intersects(rect)) continue;
                }
                var otherRect = ReadCutout(doc, target.Wall, other, target.Origin, target.Along, target.Normal, reader);
                if (otherRect.Intersects(rect))
                    throw new InvalidOperationException($"새 호스트 벽의 문·창 {other.Id}와 겹칩니다.");
            }
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
                Box = wall.get_BoundingBox(null) ?? throw new InvalidOperationException("벽 경계를 확인할 수 없습니다.");
            }
            public Wall Wall { get; }
            public BoundingBoxXYZ Box { get; }
            public Line Axis { get; }
            public XYZ Origin { get; }
            public XYZ Along { get; }
            public XYZ Normal { get; }
            public (double Min, double Max) Depth { get; }
            public XYZ Center => Origin + Normal * ((Depth.Min + Depth.Max) / 2);
            public List<Solid> Solids => _solids ??= WallSolids(Wall);
            public string Stamp => VectorStamp(Origin) + "|" + VectorStamp(Axis.GetEndPoint(1)) +
                FormattableString.Invariant($"|{Depth.Min:F6}|{Depth.Max:F6}|{Box.Min.Z:F6}|{Box.Max.Z:F6}");
        }

        private static string VectorStamp(XYZ v) => FormattableString.Invariant($"{v.X:F6}|{v.Y:F6}|{v.Z:F6}");

        private sealed class OpeningRoute
        {
            public OpeningRoute(string instance, string host, string target, OpeningRectangle? originalRect = null)
            {
                Instance = instance;
                Host = host;
                Target = target;
                OriginalRect = originalRect;
            }

            public string Instance { get; }
            public string Host { get; }
            public string Target { get; }
            public OpeningRectangle? OriginalRect { get; }
        }

        private sealed class PendingRehost
        {
            public PendingRehost(Dictionary<string, string> walls, List<RehostItem> items,
                Dictionary<string, HashSet<string>> originalHostItems, List<string> skipped)
            {
                Walls = walls;
                Items = items;
                OriginalHostItems = originalHostItems;
                Skipped = skipped;
            }

            public Dictionary<string, string> Walls { get; }
            public List<RehostItem> Items { get; }
            public Dictionary<string, HashSet<string>> OriginalHostItems { get; }
            public List<string> Skipped { get; }
        }

        private sealed class RehostItem
        {
            private readonly string _type;
            private readonly string _mark;
            private readonly XYZ _facing;
            private readonly XYZ _hand;
            public RehostItem(FamilyInstance item, string originalHost, Wall newHost, OpeningRectangle rect, OpeningRectangle originalRect)
            {
                UniqueId = item.UniqueId;
                Label = item.Id.ToString();
                OriginalHost = originalHost;
                NewHostId = newHost.Id.ToString();
                NewHost = newHost.UniqueId;
                Rect = rect;
                OriginalRect = originalRect;
                _type = item.Symbol.UniqueId;
                _mark = Mark(item);
                _facing = item.FacingOrientation;
                _hand = item.HandOrientation;
            }
            public string UniqueId { get; }
            public string Label { get; }
            public string OriginalHost { get; }
            public string NewHost { get; }
            private string NewHostId { get; }
            public string Description() => $"{Label} → 벽 {NewHostId} · {Dimensions(Rect)}";
            public OpeningRectangle Rect { get; }
            public OpeningRectangle OriginalRect { get; }
            public bool Matches(Document doc, FamilyInstance item, WallFrame frame, MethodInfo reader)
            {
                if (item.Symbol.UniqueId != _type || Mark(item) != _mark) return false;
                try
                {
                    var rect = ReadCutout(doc, frame.Wall, item, frame.Origin, frame.Along, frame.Normal, reader);
                    return SameRectangle(Rect, rect);
                }
                catch (Exception) { return false; }
            }
            private static string Mark(FamilyInstance i) => i.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
            public bool HasSameOrientation(FamilyInstance item) =>
                _facing.DotProduct(item.FacingOrientation) > 0.999999 && _hand.DotProduct(item.HandOrientation) > 0.999999;
        }

    }
}
