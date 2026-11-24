using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // FillGrid의 원점은 패턴 "정의" 좌표일 뿐, 사용자가 면에서 정렬·이동한 실제 표시 원점은 아니다.
    // 같은 2D 뷰를 패턴선 제외/포함으로 두 번 내보내고 차집합해 일반 패밀리 선과 실제 패턴선을 분리한다.
    internal static class PatternDisplayedLineCollector
    {
        internal static Dictionary<ElementId, List<PatternSegment>> Collect(Document document, View view,
            IReadOnlyList<PatternPunchTarget> targets, out Dictionary<ElementId, string> diagnostics)
        {
            double quantization = Math.Max(document.Application.VertexTolerance, 1e-7);
            List<RawDisplayedSegment> withoutPatterns = Export(document, view, false);
            List<RawDisplayedSegment> withPatterns = Export(document, view, true);
            List<RawDisplayedSegment> patternOnly = SubtractMultiset(withPatterns, withoutPatterns, quantization);

            var results = targets.ToDictionary(target => target.ElementId, _ => new List<PatternSegment>());
            var keys = targets.ToDictionary(target => target.ElementId,
                _ => new HashSet<string>(StringComparer.Ordinal));
            var counters = targets.ToDictionary(target => target.ElementId,
                _ => new CollectionCounters());

            var exactOwners = targets.GroupBy(target => target.ElementId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var aliases = new Dictionary<ElementId, List<PatternPunchTarget>>();
            foreach (PatternPunchTarget target in targets)
            {
                foreach (ElementId alias in target.DisplayElementIds.DefaultIfEmpty(target.ElementId).Distinct())
                {
                    if (!aliases.TryGetValue(alias, out List<PatternPunchTarget>? mapped))
                    {
                        mapped = new List<PatternPunchTarget>();
                        aliases.Add(alias, mapped);
                    }
                    if (!mapped.Contains(target)) mapped.Add(target);
                }
            }

            XYZ viewDirection = view.ViewDirection.Normalize();
            var unowned = new List<RawDisplayedSegment>();
            foreach (RawDisplayedSegment raw in patternOnly)
            {
                IReadOnlyList<PatternPunchTarget> candidates;
                if (exactOwners.TryGetValue(raw.ElementId, out List<PatternPunchTarget>? exact))
                {
                    candidates = exact;
                }
                else if (aliases.TryGetValue(raw.ElementId, out List<PatternPunchTarget>? aliased))
                {
                    candidates = aliased;
                }
                else
                {
                    unowned.Add(raw);
                    continue;
                }

                Assign(raw, candidates, false, viewDirection, quantization, results, keys, counters);
            }

            // 소유 ElementNode를 Revit이 패널/호스트/내부 부품 어느 쪽으로 보고할지는 패밀리마다 다르다.
            // 미소유 패턴선도 면 위치로 보완하되, 같은 선이 둘 이상의 선택 면에 들어가면
            // 앞뒤 패널 오염을 막기 위해 어느 쪽에도 배정하지 않는다.
            List<PatternPunchTarget> fallbackTargets = targets
                .Where(target => !TryCacheCompleteOwnedRegions(target, results[target.ElementId], quantization))
                .ToList();
            if (fallbackTargets.Count > 0)
            {
                foreach (RawDisplayedSegment raw in unowned)
                    Assign(raw, fallbackTargets, true, viewDirection, quantization, results, keys, counters);
            }

            diagnostics = new Dictionary<ElementId, string>();
            foreach (PatternPunchTarget target in targets)
            {
                CollectionCounters counter = counters[target.ElementId];
                diagnostics[target.ElementId] =
                    $"전체선 {withPatterns.Count:N0} · 일반선 {withoutPatterns.Count:N0} · 패턴후보 {patternOnly.Count:N0} · " +
                    $"소유후보 {counter.OwnedCandidates:N0} · 공간후보 {counter.SpatialCandidates:N0} · " +
                    $"범위제외 {counter.BoundsRejected:N0} · 면제외 {counter.FaceRejected:N0} · " +
                    $"모호 {counter.Ambiguous:N0} · 중복 {counter.Duplicates:N0} · 채택 {counter.Accepted:N0}";
            }
            return results;
        }

        private static bool TryCacheCompleteOwnedRegions(PatternPunchTarget target,
            IReadOnlyList<PatternSegment> ownedSegments, double tolerance)
        {
            if (ownedSegments.Count < 3) return false;
            try
            {
                List<PatternRegion> expectedInterior = target.Regions
                    .Where(region => IsFullyInsideFace(region, target.FacePaths, tolerance))
                    .ToList();
                if (expectedInterior.Count == 0) return false;
                PatternRegion prototype = expectedInterior[0];

                List<PatternRegion> ownedRegions = PatternRegionDetector.Detect(
                        ownedSegments, target.Bounds, out string warning)
                    .Where(region => PatternClipper.Contains(target.FacePaths, region.Centroid) ||
                                     PatternClipper.Intersect(
                                         new Clipper2Lib.Paths64 { PatternClipper.ToPath(region.Points) },
                                         target.FacePaths).Count > 0)
                    .ToList();
                int matchingInteriorCount = ownedRegions.Count(region =>
                    IsFullyInsideFace(region, target.FacePaths, tolerance) && region.IsSimilarTo(prototype));
                if (matchingInteriorCount < expectedInterior.Count) return false;

                target.PrecomputedDisplayedRegions = ownedRegions;
                target.PrecomputedDisplayedWarning = warning;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFullyInsideFace(PatternRegion region, Clipper2Lib.Paths64 facePaths, double tolerance)
        {
            double clearance = tolerance * 5.0;
            foreach (PatternPoint point in region.Points)
            {
                if (!PatternClipper.Contains(facePaths, point)) return false;
                foreach (Clipper2Lib.Path64 path in facePaths)
                {
                    List<PatternPoint> boundary = PatternClipper.FromPath(path);
                    for (int i = 0; i < boundary.Count; i++)
                        if (DistanceToSegment(point, boundary[i], boundary[(i + 1) % boundary.Count]) <= clearance)
                            return false;
                }
            }
            return true;
        }

        private static double DistanceToSegment(PatternPoint point, PatternPoint start, PatternPoint end)
        {
            PatternPoint direction = end - start;
            double lengthSquared = direction.X * direction.X + direction.Y * direction.Y;
            if (lengthSquared <= 1e-18) return (point - start).Length;
            double parameter = ((point.X - start.X) * direction.X + (point.Y - start.Y) * direction.Y) /
                               lengthSquared;
            parameter = Math.Max(0.0, Math.Min(1.0, parameter));
            return (point - (start + direction * parameter)).Length;
        }

        private static void Assign(RawDisplayedSegment raw, IReadOnlyList<PatternPunchTarget> candidates,
            bool spatialFallback, XYZ viewDirection, double tolerance,
            IDictionary<ElementId, List<PatternSegment>> results,
            IDictionary<ElementId, HashSet<string>> keys,
            IDictionary<ElementId, CollectionCounters> counters)
        {
            var projections = new List<CandidateProjection>();
            foreach (PatternPunchTarget target in candidates)
            {
                CollectionCounters counter = counters[target.ElementId];
                if (spatialFallback) counter.SpatialCandidates++;
                else counter.OwnedCandidates++;
                if (!TryProjectAndClip(raw, target, viewDirection, tolerance,
                        out PatternSegment projected, out ProjectionRejectReason rejectReason))
                {
                    if (rejectReason == ProjectionRejectReason.ViewParallel) counter.ViewParallelRejected++;
                    else if (rejectReason == ProjectionRejectReason.Bounds) counter.BoundsRejected++;
                    else if (rejectReason == ProjectionRejectReason.Face) counter.FaceRejected++;
                    continue;
                }

                string key = LocalSegmentKey(projected.Start, projected.End, tolerance);
                projections.Add(new CandidateProjection(target, projected, key, keys[target.ElementId].Contains(key)));
            }

            if (projections.Select(item => item.Target.ElementId).Distinct().Skip(1).Any())
            {
                foreach (CandidateProjection projection in projections)
                    counters[projection.Target.ElementId].Ambiguous++;
                return;
            }

            foreach (CandidateProjection projection in projections)
            {
                CollectionCounters counter = counters[projection.Target.ElementId];
                if (projection.IsDuplicate)
                {
                    counter.Duplicates++;
                    continue;
                }
                keys[projection.Target.ElementId].Add(projection.Key);
                results[projection.Target.ElementId].Add(projection.Segment);
                counter.Accepted++;
            }
        }

        private static List<RawDisplayedSegment> Export(Document document, View view, bool includePatternLines)
        {
            var context = new RawLineContext(document);
            using var exporter = new CustomExporter(document, context)
            {
                IncludeGeometricObjects = true,
                Export2DGeometricObjectsIncludingPatternLines = includePatternLines,
                Export2DIncludingAnnotationObjects = false,
                Export2DForceDisplayStyle = DisplayStyle.HLR,
                ShouldStopOnError = false,
            };
            exporter.Export(view);
            return context.Segments;
        }

        private static List<RawDisplayedSegment> SubtractMultiset(IReadOnlyList<RawDisplayedSegment> withPatterns,
            IReadOnlyList<RawDisplayedSegment> withoutPatterns, double tolerance)
        {
            var baselineCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var baselineIndex = new BaselineDirectionIndex(withoutPatterns);
            foreach (RawDisplayedSegment segment in withoutPatterns)
            {
                string key = RawSegmentKey(segment, tolerance);
                baselineCounts.TryGetValue(key, out int count);
                baselineCounts[key] = count + 1;
            }

            var result = new List<RawDisplayedSegment>();
            foreach (RawDisplayedSegment segment in withPatterns)
            {
                string key = RawSegmentKey(segment, tolerance);
                if (baselineCounts.TryGetValue(key, out int count) && count > 0)
                {
                    if (count == 1) baselineCounts.Remove(key);
                    else baselineCounts[key] = count - 1;
                }
                else
                {
                    result.AddRange(SubtractCollinearCoverage(segment, baselineIndex.Find(segment), tolerance));
                }
            }
            return result;
        }

        private static IEnumerable<RawDisplayedSegment> SubtractCollinearCoverage(RawDisplayedSegment source,
            IEnumerable<RawDisplayedSegment> baseline, double tolerance)
        {
            XYZ direction = source.End - source.Start;
            double length = direction.GetLength();
            double lengthSquared = direction.DotProduct(direction);
            if (length <= tolerance || lengthSquared <= 1e-18) yield break;

            var covered = new List<(double Start, double End)>();
            foreach (RawDisplayedSegment candidate in baseline)
            {
                if (DistanceToInfiniteLine(candidate.Start, source.Start, direction, length) > tolerance ||
                    DistanceToInfiniteLine(candidate.End, source.Start, direction, length) > tolerance)
                    continue;

                double a = (candidate.Start - source.Start).DotProduct(direction) / lengthSquared;
                double b = (candidate.End - source.Start).DotProduct(direction) / lengthSquared;
                double start = Math.Max(0.0, Math.Min(a, b));
                double end = Math.Min(1.0, Math.Max(a, b));
                if (end > start) covered.Add((start, end));
            }

            if (covered.Count == 0)
            {
                yield return source;
                yield break;
            }

            covered.Sort((left, right) => left.Start.CompareTo(right.Start));
            double parameterTolerance = Math.Min(0.25, tolerance / length);
            double cursor = 0.0;
            foreach ((double start, double end) in covered)
            {
                if (start > cursor + parameterTolerance)
                    yield return new RawDisplayedSegment(source.ElementId,
                        source.Start + direction * cursor, source.Start + direction * start);
                if (end > cursor) cursor = end;
                if (cursor >= 1.0 - parameterTolerance) yield break;
            }
            if (cursor < 1.0 - parameterTolerance)
                yield return new RawDisplayedSegment(source.ElementId,
                    source.Start + direction * cursor, source.End);
        }

        private static double DistanceToInfiniteLine(XYZ point, XYZ lineStart, XYZ lineDirection, double lineLength) =>
            (point - lineStart).CrossProduct(lineDirection).GetLength() / lineLength;

        private static bool TryProjectAndClip(RawDisplayedSegment raw, PatternPunchTarget target, XYZ viewDirection,
            double tolerance, out PatternSegment segment, out ProjectionRejectReason rejectReason)
        {
            double denominator = viewDirection.DotProduct(target.Basis.Normal);
            if (Math.Abs(denominator) < 1e-6)
            {
                segment = default;
                rejectReason = ProjectionRejectReason.ViewParallel;
                return false;
            }

            XYZ worldA = ProjectAlongView(raw.Start, target.Basis.Origin, target.Basis.Normal, viewDirection, denominator);
            XYZ worldB = ProjectAlongView(raw.End, target.Basis.Origin, target.Basis.Normal, viewDirection, denominator);
            PatternPoint localA = target.Basis.ToLocal(worldA);
            PatternPoint localB = target.Basis.ToLocal(worldB);
            PatternBounds expanded = target.Bounds.Expand(tolerance * 5.0);
            if (!TryClipToBounds(localA, localB, expanded, out PatternPoint clippedA, out PatternPoint clippedB))
            {
                segment = default;
                rejectReason = ProjectionRejectReason.Bounds;
                return false;
            }
            if ((clippedB - clippedA).Length <= tolerance)
            {
                segment = default;
                rejectReason = ProjectionRejectReason.Bounds;
                return false;
            }
            if (!TouchesFace(clippedA, clippedB, target.FacePaths, tolerance))
            {
                segment = default;
                rejectReason = ProjectionRejectReason.Face;
                return false;
            }
            if (IsFaceBoundary(clippedA, clippedB, target.FacePaths, tolerance))
            {
                segment = default;
                rejectReason = ProjectionRejectReason.Face;
                return false;
            }

            segment = new PatternSegment(clippedA, clippedB);
            rejectReason = ProjectionRejectReason.None;
            return true;
        }

        private static XYZ ProjectAlongView(XYZ point, XYZ planeOrigin, XYZ planeNormal,
            XYZ viewDirection, double denominator)
        {
            double parameter = (planeOrigin - point).DotProduct(planeNormal) / denominator;
            return point + viewDirection * parameter;
        }

        private static bool TryClipToBounds(PatternPoint a, PatternPoint b, PatternBounds bounds,
            out PatternPoint clippedA, out PatternPoint clippedB)
        {
            PatternPoint delta = b - a;
            double t0 = 0.0;
            double t1 = 1.0;
            if (!Clip(-delta.X, a.X - bounds.MinX, ref t0, ref t1) ||
                !Clip(delta.X, bounds.MaxX - a.X, ref t0, ref t1) ||
                !Clip(-delta.Y, a.Y - bounds.MinY, ref t0, ref t1) ||
                !Clip(delta.Y, bounds.MaxY - a.Y, ref t0, ref t1))
            {
                clippedA = default;
                clippedB = default;
                return false;
            }
            clippedA = a + delta * t0;
            clippedB = a + delta * t1;
            return true;
        }

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (Math.Abs(p) < 1e-14) return q >= 0.0;
            double ratio = q / p;
            if (p < 0.0)
            {
                if (ratio > t1) return false;
                if (ratio > t0) t0 = ratio;
            }
            else
            {
                if (ratio < t0) return false;
                if (ratio < t1) t1 = ratio;
            }
            return true;
        }

        private static bool TouchesFace(PatternPoint a, PatternPoint b, Clipper2Lib.Paths64 facePaths, double tolerance)
        {
            if (PatternClipper.Contains(facePaths, a) || PatternClipper.Contains(facePaths, b) ||
                PatternClipper.Contains(facePaths, (a + b) * 0.5)) return true;
            foreach (Clipper2Lib.Path64 path in facePaths)
            {
                List<PatternPoint> boundary = PatternClipper.FromPath(path);
                for (int i = 0; i < boundary.Count; i++)
                    if (SegmentsIntersect(a, b, boundary[i], boundary[(i + 1) % boundary.Count], tolerance)) return true;
            }
            return false;
        }

        private static bool SegmentsIntersect(PatternPoint a, PatternPoint b, PatternPoint c, PatternPoint d, double tolerance)
        {
            PatternPoint r = b - a;
            PatternPoint s = d - c;
            double denominator = Cross(r, s);
            if (Math.Abs(denominator) <= tolerance * Math.Max(1.0, r.Length + s.Length)) return false;
            double t = Cross(c - a, s) / denominator;
            double u = Cross(c - a, r) / denominator;
            return t >= -tolerance && t <= 1.0 + tolerance && u >= -tolerance && u <= 1.0 + tolerance;
        }

        private static bool IsFaceBoundary(PatternPoint a, PatternPoint b, Clipper2Lib.Paths64 facePaths, double tolerance)
        {
            PatternPoint direction = b - a;
            foreach (Clipper2Lib.Path64 path in facePaths)
            {
                List<PatternPoint> boundary = PatternClipper.FromPath(path);
                for (int i = 0; i < boundary.Count; i++)
                {
                    PatternPoint c = boundary[i];
                    PatternPoint d = boundary[(i + 1) % boundary.Count];
                    PatternPoint edge = d - c;
                    double crossDirection = Math.Abs(Cross(direction, edge));
                    if (crossDirection > tolerance * Math.Max(1.0, direction.Length + edge.Length)) continue;
                    if (DistanceToInfiniteLine(a, c, d) <= tolerance && DistanceToInfiniteLine(b, c, d) <= tolerance)
                        return true;
                }
            }
            return false;
        }

        private static double DistanceToInfiniteLine(PatternPoint point, PatternPoint a, PatternPoint b)
        {
            PatternPoint line = b - a;
            if (line.Length <= 1e-12) return (point - a).Length;
            return Math.Abs(Cross(point - a, line)) / line.Length;
        }

        private static string RawSegmentKey(RawDisplayedSegment segment, double tolerance)
        {
            QuantizedPoint a = QuantizedPoint.From(segment.Start, tolerance);
            QuantizedPoint b = QuantizedPoint.From(segment.End, tolerance);
            if (a.CompareTo(b) > 0) (a, b) = (b, a);
            return $"{PatternPunchExecutor.GetElementIdValue(segment.ElementId)}|{a}|{b}";
        }

        private static string LocalSegmentKey(PatternPoint a, PatternPoint b, double tolerance)
        {
            long ax = Quantize(a.X, tolerance);
            long ay = Quantize(a.Y, tolerance);
            long bx = Quantize(b.X, tolerance);
            long by = Quantize(b.Y, tolerance);
            if (ax > bx || ax == bx && ay > by) (ax, ay, bx, by) = (bx, by, ax, ay);
            return $"{ax},{ay}|{bx},{by}";
        }

        private static long Quantize(double value, double tolerance) =>
            (long)Math.Round(value / tolerance, MidpointRounding.AwayFromZero);

        private static double Cross(PatternPoint a, PatternPoint b) => a.X * b.Y - a.Y * b.X;

        private sealed class RawLineContext : IExportContext2D
        {
            private readonly string _hostDocumentKey;
            private readonly Stack<ElementScope> _elementStack = new Stack<ElementScope>();
            internal List<RawDisplayedSegment> Segments { get; } = new List<RawDisplayedSegment>();

            internal RawLineContext(Document hostDocument)
            {
                _hostDocumentKey = DocumentKey(hostDocument);
            }

            // Document를 Equals/ReferenceEquals로 비교하지 말 것. Revit API는 같은 열린 문서에 대해
            // 호출 시점마다 다른 래퍼 인스턴스를 돌려줄 수 있고 Document는 Equals를 값 비교로
            // 재정의하지 않는다(경고Pick의 CONFIRMED LIVE BUG와 같은 원인 - docs/warning-pick).
            // 여기서 잘못 false가 나오면 내보낸 선을 전부 버려서 패턴 타공이 항상
            // "표시 패턴 선을 찾지 못했습니다"로 실패한다. 경로(저장 안 된 문서는 제목)로 비교한다.
            private static string DocumentKey(Document? document)
            {
                if (document == null) return "";
                try { return string.IsNullOrEmpty(document.PathName) ? document.Title ?? "" : document.PathName; }
                catch { return ""; }
            }

            public bool Start() => true;
            public void Finish() { }
            public bool IsCanceled() => false;

            public RenderNodeAction OnElementBegin2D(ElementNode node)
            {
                // 링크가 아닌 노드는 곧 호스트 문서의 요소다. 문서 키는 보조 확인으로만 쓰고,
                // 키를 읽지 못한 경우(빈 문자열)에는 링크 여부만으로 판정해 전부 버리지 않는다.
                bool isLinked = node.LinkInstanceId != ElementId.InvalidElementId;
                string nodeKey = DocumentKey(node.Document);
                bool sameDocument = nodeKey.Length == 0 || _hostDocumentKey.Length == 0 ||
                                    string.Equals(nodeKey, _hostDocumentKey, StringComparison.OrdinalIgnoreCase);
                bool isHostElement = !isLinked && sameDocument;
                _elementStack.Push(new ElementScope(node.ElementId, isHostElement));
                return RenderNodeAction.Proceed;
            }

            public void OnElementEnd2D(ElementNode node)
            {
                if (_elementStack.Count == 0) return;
                if (_elementStack.Peek().ElementId == node.ElementId) _elementStack.Pop();
                else _elementStack.Clear();
            }

            public RenderNodeAction OnCurve(CurveNode node) => RenderNodeAction.Proceed;
            public RenderNodeAction OnPolyline(PolylineNode node) => RenderNodeAction.Proceed;
            public RenderNodeAction OnFaceEdge2D(FaceEdgeNode node) => RenderNodeAction.Proceed;
            public RenderNodeAction OnFaceSilhouette2D(FaceSilhouetteNode node) => RenderNodeAction.Proceed;

            public void OnLineSegment(LineSegment segment) => Add(segment.StartPoint, segment.EndPoint);

            public void OnPolylineSegments(PolylineSegments segments)
            {
                IList<XYZ> points = segments.GetVertices();
                for (int i = 0; i + 1 < points.Count; i++) Add(points[i], points[i + 1]);
            }

            private void Add(XYZ start, XYZ end)
            {
                if (_elementStack.Count == 0 || !_elementStack.Peek().Accept || start.IsAlmostEqualTo(end)) return;
                Segments.Add(new RawDisplayedSegment(_elementStack.Peek().ElementId, start, end));
            }

            public RenderNodeAction OnElementBegin(ElementId elementId) => RenderNodeAction.Proceed;
            public void OnElementEnd(ElementId elementId) { }
            public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;
            public void OnFaceEnd(FaceNode node) { }
            public RenderNodeAction OnInstanceBegin(InstanceNode node) => RenderNodeAction.Proceed;
            public void OnInstanceEnd(InstanceNode node) { }
            public RenderNodeAction OnLinkBegin(LinkNode node) => RenderNodeAction.Skip;
            public void OnLinkEnd(LinkNode node) { }
            public void OnLight(LightNode node) { }
            public void OnMaterial(MaterialNode node) { }
            public void OnPolymesh(PolymeshTopology node) { }
            public void OnRPC(RPCNode node) { }
            public void OnText(TextNode node) { }
            public RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;
            public void OnViewEnd(ElementId elementId) { }

            private readonly struct ElementScope
            {
                internal ElementScope(ElementId elementId, bool accept)
                {
                    ElementId = elementId;
                    Accept = accept;
                }

                internal ElementId ElementId { get; }
                internal bool Accept { get; }
            }
        }

        private readonly struct RawDisplayedSegment
        {
            internal RawDisplayedSegment(ElementId elementId, XYZ start, XYZ end)
            {
                ElementId = elementId;
                Start = start;
                End = end;
            }

            internal ElementId ElementId { get; }
            internal XYZ Start { get; }
            internal XYZ End { get; }
        }

        private readonly struct CandidateProjection
        {
            internal CandidateProjection(PatternPunchTarget target, PatternSegment segment, string key, bool isDuplicate)
            {
                Target = target;
                Segment = segment;
                Key = key;
                IsDuplicate = isDuplicate;
            }

            internal PatternPunchTarget Target { get; }
            internal PatternSegment Segment { get; }
            internal string Key { get; }
            internal bool IsDuplicate { get; }
        }

        private sealed class BaselineDirectionIndex
        {
            private readonly Dictionary<ElementId, Dictionary<DirectionBucket, List<RawDisplayedSegment>>> _byOwner =
                new Dictionary<ElementId, Dictionary<DirectionBucket, List<RawDisplayedSegment>>>();

            internal BaselineDirectionIndex(IEnumerable<RawDisplayedSegment> segments)
            {
                foreach (RawDisplayedSegment segment in segments)
                {
                    if (!DirectionBucket.TryCreate(segment, out DirectionBucket bucket)) continue;
                    if (!_byOwner.TryGetValue(segment.ElementId,
                            out Dictionary<DirectionBucket, List<RawDisplayedSegment>>? owner))
                    {
                        owner = new Dictionary<DirectionBucket, List<RawDisplayedSegment>>();
                        _byOwner.Add(segment.ElementId, owner);
                    }
                    if (!owner.TryGetValue(bucket, out List<RawDisplayedSegment>? values))
                    {
                        values = new List<RawDisplayedSegment>();
                        owner.Add(bucket, values);
                    }
                    values.Add(segment);
                }
            }

            internal IEnumerable<RawDisplayedSegment> Find(RawDisplayedSegment segment)
            {
                if (!DirectionBucket.TryCreate(segment, out DirectionBucket bucket) ||
                    !_byOwner.TryGetValue(segment.ElementId,
                        out Dictionary<DirectionBucket, List<RawDisplayedSegment>>? owner))
                    yield break;

                for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                {
                    var neighbor = new DirectionBucket(bucket.X + dx, bucket.Y + dy, bucket.Z + dz);
                    if (!owner.TryGetValue(neighbor, out List<RawDisplayedSegment>? values)) continue;
                    foreach (RawDisplayedSegment value in values) yield return value;
                }
            }
        }

        private readonly struct DirectionBucket : IEquatable<DirectionBucket>
        {
            private const double Scale = 10000.0;

            internal DirectionBucket(long x, long y, long z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            internal long X { get; }
            internal long Y { get; }
            internal long Z { get; }

            internal static bool TryCreate(RawDisplayedSegment segment, out DirectionBucket bucket)
            {
                XYZ direction = segment.End - segment.Start;
                double length = direction.GetLength();
                if (length <= 1e-12)
                {
                    bucket = default;
                    return false;
                }
                direction /= length;
                if (direction.X < 0.0 ||
                    Math.Abs(direction.X) <= 1e-12 && direction.Y < 0.0 ||
                    Math.Abs(direction.X) <= 1e-12 && Math.Abs(direction.Y) <= 1e-12 && direction.Z < 0.0)
                    direction = -direction;
                bucket = new DirectionBucket(
                    (long)Math.Round(direction.X * Scale, MidpointRounding.AwayFromZero),
                    (long)Math.Round(direction.Y * Scale, MidpointRounding.AwayFromZero),
                    (long)Math.Round(direction.Z * Scale, MidpointRounding.AwayFromZero));
                return true;
            }

            public bool Equals(DirectionBucket other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object? obj) => obj is DirectionBucket other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = X.GetHashCode();
                    hash = hash * 397 ^ Y.GetHashCode();
                    return hash * 397 ^ Z.GetHashCode();
                }
            }
        }

        private readonly struct QuantizedPoint : IComparable<QuantizedPoint>
        {
            private QuantizedPoint(long x, long y, long z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            private long X { get; }
            private long Y { get; }
            private long Z { get; }

            internal static QuantizedPoint From(XYZ point, double tolerance) =>
                new QuantizedPoint(Quantize(point.X, tolerance), Quantize(point.Y, tolerance), Quantize(point.Z, tolerance));

            public int CompareTo(QuantizedPoint other)
            {
                int x = X.CompareTo(other.X);
                if (x != 0) return x;
                int y = Y.CompareTo(other.Y);
                return y != 0 ? y : Z.CompareTo(other.Z);
            }

            public override string ToString() => $"{X},{Y},{Z}";
        }

        private sealed class CollectionCounters
        {
            internal int OwnedCandidates;
            internal int SpatialCandidates;
            internal int ViewParallelRejected;
            internal int BoundsRejected;
            internal int FaceRejected;
            internal int Ambiguous;
            internal int Duplicates;
            internal int Accepted;
        }

        private enum ProjectionRejectReason
        {
            None,
            ViewParallel,
            Bounds,
            Face,
        }
    }
}
