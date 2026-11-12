using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public sealed class StructuralWallOpeningCommand : IExternalCommand
    {
        private const string Title = "구조벽 개구부";
        private static readonly double GeometryTolerance = Mm(0.5);
        private static readonly double ContactTolerance = Mm(5);
        private static readonly double ProbeInset = Mm(1);
        private static double Mm(double value) => UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters);

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var ui = commandData.Application.ActiveUIDocument;
            if (ui == null || ui.Document.IsFamilyDocument || ui.Document.IsReadOnly)
                return Result.Cancelled;
            var doc = ui.Document;
            try
            {
                var targetRef = ui.Selection.PickObject(ObjectType.Element, new PickFilter(IsStructuralWall),
                    "개구부를 만들 구조벽 한 개를 선택하세요.");
                var target = (Wall)doc.GetElement(targetRef);
                if (target.Pinned || target.GroupId != ElementId.InvalidElementId)
                    throw new InvalidOperationException("고정되어 있거나 그룹에 속한 구조벽은 편집할 수 없습니다.");
                var axis = RequireStraightWall(target);
                var origin = axis.GetEndPoint(0);
                var along = axis.Direction;
                var normal = along.CrossProduct(XYZ.BasisZ).Normalize();
                var depth = SideDepth(target, origin, normal);
                var solids = WallSolids(target);

                var refs = ui.Selection.PickObjects(ObjectType.Element, new PickFilter(IsDoorOrWindow),
                    "인접 건축벽의 문·창을 선택하고 완료를 누르세요.");
                var getCut = FindCutReader();
                var candidates = new List<OpeningCandidate>();
                var skipped = new List<string>();
                foreach (var id in refs.Select(r => r.ElementId).Distinct())
                {
                    var instance = (FamilyInstance)doc.GetElement(id);
                    try
                    {
                        var rect = GetRectangle(doc, instance, target, origin, along, normal, depth, getCut);
                        if (candidates.Any(c => c.Rect.Intersects(rect)))
                            throw new InvalidOperationException("이미 검토한 다른 문·창의 개구부와 겹칩니다.");
                        var probe = BuildProbe(rect, origin, along, normal, depth);
                        var fullVolume = (rect.Width - 2 * ProbeInset) * (rect.Height - 2 * ProbeInset) * (depth.Max - depth.Min);
                        var volume = OccupiedVolume(solids, probe);
                        if (volume < fullVolume * 0.99999)
                            throw new InvalidOperationException(volume <= fullVolume * 1e-7
                                ? "이 위치에 구조벽 재료가 없거나 이미 개구부가 있습니다."
                                : "구조벽 경계를 벗어나거나 기존 개구부와 일부 겹칩니다.");
                        candidates.Add(new OpeningCandidate(instance.Id, rect, probe, fullVolume * 1e-7));
                    }
                    catch (Exception ex)
                    {
                        skipped.Add($"{id} · {ex.Message}");
                    }
                }

                var preview = new TaskDialog(Title)
                {
                    MainInstruction = $"구조벽 {target.Id} · 생성 {candidates.Count}개 · 제외 {skipped.Count}개",
                    MainContent = candidates.Count == 0 ? "생성할 수 있는 개구부가 없습니다." :
                        "문·창의 실제 직사각형 개구부와 같은 폭·높이로 구조벽을 관통합니다.",
                    ExpandedContent = Details(candidates, skipped),
                    CommonButtons = candidates.Count == 0 ? TaskDialogCommonButtons.Close :
                        TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                    DefaultButton = candidates.Count == 0 ? TaskDialogResult.Close : TaskDialogResult.Cancel
                };
                if (preview.Show() != TaskDialogResult.Ok) return Result.Cancelled;

                var center = origin + normal * ((depth.Min + depth.Max) / 2);
                var created = Apply(doc, target, center, along, candidates);
                try { ui.Selection.SetElementIds(created); }
                catch (Exception) { /* 개구부 선택 실패는 생성 결과와 별개다. */ }
                TaskDialog.Show(Title, $"개구부 {created.Count}개를 생성했습니다. 제외 {skipped.Count}개.");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, ex.Message);
                return Result.Cancelled;
            }
        }

        private static bool IsStructuralWall(Element element) => element is Wall wall &&
            wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;

        private static bool IsDoorOrWindow(Element element) => element is FamilyInstance &&
            (element.Category?.Id == new ElementId(BuiltInCategory.OST_Doors) ||
             element.Category?.Id == new ElementId(BuiltInCategory.OST_Windows));

        private static Line RequireStraightWall(Wall wall)
        {
            if (wall.WallType.Kind != WallKind.Basic || wall.CrossSection != WallCrossSection.Vertical ||
                wall.Location is not LocationCurve location || location.Curve is not Line line ||
                !line.IsBound || Math.Abs(line.Direction.Z) > 1e-6)
                throw new InvalidOperationException("수직 직선 기본벽만 지원합니다.");
            return line;
        }

        private static (double Min, double Max) SideDepth(Wall wall, XYZ origin, XYZ normal)
        {
            var offsets = new List<double>();
            foreach (var side in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                var refs = HostObjectUtils.GetSideFaces(wall, side);
                if (refs.Count == 0)
                    throw new InvalidOperationException("벽의 안쪽·바깥쪽 면을 읽을 수 없습니다.");
                foreach (var reference in refs)
                {
                    if (wall.GetGeometryObjectFromReference(reference) is not PlanarFace face ||
                        Math.Abs(face.FaceNormal.DotProduct(normal)) < 0.999999)
                        throw new InvalidOperationException("벽의 안쪽·바깥쪽 면이 평행하지 않습니다.");
                    offsets.Add((face.Origin - origin).DotProduct(normal));
                }
            }
            var min = offsets.Min();
            var max = offsets.Max();
            if (max - min <= GeometryTolerance || Math.Abs(max - min - wall.Width) > GeometryTolerance)
                throw new InvalidOperationException("벽의 양면 사이 두께가 일정하지 않습니다.");
            return (min, max);
        }

        // IFC 개구부 API는 Revit에 설치된 별도 어셈블리에 있다. 현재 실행 중인 Revit의 파일만 참조한다.
        private static MethodInfo FindCutReader()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RevitAPIIFC")
                ?? Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Document).Assembly.Location)!, "RevitAPIIFC.dll"));
            var method = assembly.GetType("Autodesk.Revit.DB.IFC.ExporterIFCUtils")?.GetMethod("GetInstanceCutoutFromWall",
                new[] { typeof(Document), typeof(Wall), typeof(FamilyInstance), typeof(XYZ).MakeByRefType() });
            return method ?? throw new InvalidOperationException("이 Revit 버전에서 문·창의 개구부 윤곽 API를 찾을 수 없습니다.");
        }

        private static Rectangle GetRectangle(Document doc, FamilyInstance instance, Wall target, XYZ origin,
            XYZ along, XYZ normal, (double Min, double Max) targetDepth, MethodInfo getCut)
        {
            if (instance.Host is not Wall source || source.Id == target.Id)
                throw new InvalidOperationException("별도의 건축벽에 호스트된 문·창을 선택하세요.");
            var axis = RequireStraightWall(source);
            if (axis.Direction.CrossProduct(along).GetLength() > 1e-5)
                throw new InvalidOperationException("건축벽과 구조벽이 평행하지 않습니다.");
            var sourceDepth = SideDepth(source, origin, normal);
            var gap = Math.Max(sourceDepth.Min - targetDepth.Max, targetDepth.Min - sourceDepth.Max);
            if (gap > ContactTolerance)
                throw new InvalidOperationException("건축벽과 구조벽 사이가 5 mm보다 멉니다.");

            object?[] arguments = { doc, source, instance, null };
            CurveLoop? loop;
            try { loop = getCut.Invoke(null, arguments) as CurveLoop; }
            catch (TargetInvocationException ex)
            { throw new InvalidOperationException("건축벽에서 문·창의 실제 개구부 윤곽을 읽을 수 없습니다.", ex.InnerException); }
            if (loop == null || arguments[3] is not XYZ cutDirection ||
                cutDirection.GetLength() < 1e-9 || Math.Abs(cutDirection.Normalize().DotProduct(normal)) < 0.999999)
                throw new InvalidOperationException("건축벽을 직각으로 관통하는 문·창 개구부를 확인할 수 없습니다.");
            return ReadRectangle(loop, origin, along, normal);
        }

        internal static Rectangle ReadRectangle(CurveLoop loop, XYZ origin, XYZ along, XYZ normal)
        {
            var curves = loop.ToList();
            if (loop.IsOpen() || curves.Count != 4 || curves.Any(c => c is not Line || !c.IsBound))
                throw new InvalidOperationException("문·창 개구부가 네 직선으로 이루어진 직사각형이 아닙니다.");
            var points = curves.Select(c => c.GetEndPoint(0) - origin).ToList();
            var x = points.Select(p => p.DotProduct(along)).ToArray();
            var z = points.Select(p => p.Z).ToArray();
            var n = points.Select(p => p.DotProduct(normal)).ToArray();
            var rect = new Rectangle(x.Min(), x.Max(), z.Min(), z.Max());
            if (n.Max() - n.Min() > GeometryTolerance ||
                Math.Min(rect.Width, rect.Height) <= 2 * ProbeInset + GeometryTolerance)
                throw new InvalidOperationException("문·창 개구부의 평면이나 크기를 확인할 수 없습니다.");

            var corners = new HashSet<int>();
            for (int i = 0; i < 4; i++)
            {
                var atLeft = Math.Abs(x[i] - rect.Left) <= GeometryTolerance;
                var atBottom = Math.Abs(z[i] - rect.Bottom) <= GeometryTolerance;
                if ((!atLeft && Math.Abs(x[i] - rect.Right) > GeometryTolerance) ||
                    (!atBottom && Math.Abs(z[i] - rect.Top) > GeometryTolerance) ||
                    !corners.Add((atLeft ? 0 : 1) + (atBottom ? 0 : 2)))
                    throw new InvalidOperationException("문·창 개구부의 네 모서리가 직사각형을 이루지 않습니다.");
                var next = (i + 1) % 4;
                if ((Math.Abs(x[i] - x[next]) <= GeometryTolerance) == (Math.Abs(z[i] - z[next]) <= GeometryTolerance))
                    throw new InvalidOperationException("문·창 개구부의 변이 수평·수직이 아닙니다.");
            }
            return rect;
        }

        private static Solid BuildProbe(Rectangle rect, XYZ origin, XYZ along, XYZ normal, (double Min, double Max) depth)
        {
            // 경계가 맞닿는 문 하단과 불리언 연산 오차를 고려해 검증용 솔리드만 1 mm 안쪽에 둔다.
            var start = origin + normal * (depth.Min - ProbeInset);
            XYZ At(double x, double z) => start + along * x + XYZ.BasisZ * z;
            var points = new[] { At(rect.Left + ProbeInset, rect.Bottom + ProbeInset),
                At(rect.Right - ProbeInset, rect.Bottom + ProbeInset), At(rect.Right - ProbeInset, rect.Top - ProbeInset),
                At(rect.Left + ProbeInset, rect.Top - ProbeInset) };
            var loop = CurveLoop.Create(Enumerable.Range(0, 4).Select(i => (Curve)Line.CreateBound(points[i], points[(i + 1) % 4])).ToList());
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, normal, depth.Max - depth.Min + 2 * ProbeInset);
        }

        private static List<Solid> WallSolids(Wall wall)
        {
            var geometry = wall.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            var solids = geometry?.OfType<Solid>().Where(s => s.Faces.Size > 0 && s.Volume > 0).ToList();
            if (solids == null || solids.Count == 0)
                throw new InvalidOperationException("구조벽의 실제 솔리드를 확인할 수 없습니다.");
            return solids;
        }

        private static double OccupiedVolume(IEnumerable<Solid> solids, Solid probe) =>
            solids.Sum(s => Math.Abs(BooleanOperationsUtils.ExecuteBooleanOperation(s, probe, BooleanOperationsType.Intersect).Volume));

        private static List<ElementId> Apply(Document doc, Wall wall, XYZ origin, XYZ along, List<OpeningCandidate> candidates)
        {
            using var group = new TransactionGroup(doc, Title);
            if (group.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("구조벽 편집을 시작할 수 없습니다.");
            using var tx = new Transaction(doc, Title);
            if (tx.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("구조벽 편집을 시작할 수 없습니다.");
            var failureHandler = new RollBackErrors();
            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failureHandler).SetClearAfterRollback(true));
            try
            {
                var ids = new List<ElementId>();
                foreach (var candidate in candidates)
                {
                    var rect = candidate.Rect;
                    var opening = doc.Create.NewOpening(wall,
                        origin + along * rect.Left + XYZ.BasisZ * rect.Bottom,
                        origin + along * rect.Right + XYZ.BasisZ * rect.Top);
                    if (opening == null || opening.Host?.Id != wall.Id)
                        throw new InvalidOperationException($"문·창 {candidate.Source}: 구조벽 개구부가 생성되지 않았습니다.");
                    ids.Add(opening.Id);
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException(failureHandler.Error ?? "Revit에서 구조벽 편집을 완료하지 못했습니다.");
                var solids = WallSolids(wall);
                foreach (var candidate in candidates)
                    if (OccupiedVolume(solids, candidate.Probe) > candidate.VolumeTolerance)
                        throw new InvalidOperationException($"문·창 {candidate.Source}: 구조벽을 완전히 관통했는지 확인할 수 없습니다.");
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("검증된 구조벽 개구부를 확정할 수 없습니다.");
                return ids;
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                throw;
            }
        }

        private static string Details(List<OpeningCandidate> candidates, List<string> skipped)
        {
            double Millimetres(double length) => UnitUtils.ConvertFromInternalUnits(length, UnitTypeId.Millimeters);
            var accepted = candidates.Take(30).Select(c => $"{c.Source} · {Millimetres(c.Rect.Width):0.0} × {Millimetres(c.Rect.Height):0.0} mm");
            return string.Join("\n", accepted.Concat(skipped.Take(30))) +
                (candidates.Count > 30 || skipped.Count > 30 ? "\n생성·제외 항목별로 최대 30개를 표시합니다." : "");
        }

        internal readonly struct Rectangle
        {
            public Rectangle(double left, double right, double bottom, double top)
            { Left = left; Right = right; Bottom = bottom; Top = top; }
            public double Left { get; }
            public double Right { get; }
            public double Bottom { get; }
            public double Top { get; }
            public double Width => Right - Left;
            public double Height => Top - Bottom;
            public bool Intersects(Rectangle other) =>
                Math.Min(Right, other.Right) - Math.Max(Left, other.Left) > GeometryTolerance &&
                Math.Min(Top, other.Top) - Math.Max(Bottom, other.Bottom) > GeometryTolerance;
        }

        private sealed class OpeningCandidate
        {
            public OpeningCandidate(ElementId source, Rectangle rect, Solid probe, double volumeTolerance)
            { Source = source; Rect = rect; Probe = probe; VolumeTolerance = volumeTolerance; }
            public ElementId Source { get; }
            public Rectangle Rect { get; }
            public Solid Probe { get; }
            public double VolumeTolerance { get; }
        }

        private sealed class PickFilter : ISelectionFilter
        {
            private readonly Func<Element, bool> _matches;
            public PickFilter(Func<Element, bool> matches) => _matches = matches;
            public bool AllowElement(Element element) => _matches(element);
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private sealed class RollBackErrors : IFailuresPreprocessor
        {
            public string? Error { get; private set; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                var failure = accessor.GetFailureMessages().FirstOrDefault(f => f.GetSeverity() >= FailureSeverity.Error);
                if (failure == null) return FailureProcessingResult.Continue;
                Error = failure.GetDescriptionText();
                return FailureProcessingResult.ProceedWithRollBack;
            }
        }
    }
}
