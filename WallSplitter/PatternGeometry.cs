using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace WallSplitter
{
    // Revit의 3차원 좌표를 패턴 면의 2차원 좌표로 내린 뒤 사용하는 공통 기하 형식이다.
    // WPF Point/Vector와 Autodesk.Revit.DB.XYZ를 직접 섞지 않아 창 코드와 Revit 코드 양쪽에서 재사용한다.
    internal readonly struct PatternPoint
    {
        public PatternPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
        public double Length => Math.Sqrt(X * X + Y * Y);

        public static PatternPoint operator +(PatternPoint a, PatternPoint b) => new PatternPoint(a.X + b.X, a.Y + b.Y);
        public static PatternPoint operator -(PatternPoint a, PatternPoint b) => new PatternPoint(a.X - b.X, a.Y - b.Y);
        public static PatternPoint operator *(PatternPoint value, double scale) => new PatternPoint(value.X * scale, value.Y * scale);
        public static PatternPoint operator /(PatternPoint value, double scale) => new PatternPoint(value.X / scale, value.Y / scale);
    }

    internal readonly struct PatternSegment
    {
        public PatternSegment(PatternPoint start, PatternPoint end, int gridIndex = -1)
        {
            Start = start;
            End = end;
            GridIndex = gridIndex;
        }

        public PatternPoint Start { get; }
        public PatternPoint End { get; }
        public int GridIndex { get; }
        public double Length => (End - Start).Length;
    }

    internal readonly struct PatternBounds
    {
        public PatternBounds(double minX, double minY, double maxX, double maxY)
        {
            MinX = Math.Min(minX, maxX);
            MinY = Math.Min(minY, maxY);
            MaxX = Math.Max(minX, maxX);
            MaxY = Math.Max(minY, maxY);
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public PatternPoint Center => new PatternPoint((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);

        public PatternBounds Expand(double amount) => new PatternBounds(MinX - amount, MinY - amount, MaxX + amount, MaxY + amount);

        public bool Contains(PatternPoint point, double tolerance = 0.0) =>
            point.X >= MinX - tolerance && point.X <= MaxX + tolerance &&
            point.Y >= MinY - tolerance && point.Y <= MaxY + tolerance;

        public static PatternBounds FromPoints(IEnumerable<PatternPoint> points)
        {
            List<PatternPoint> list = points.ToList();
            if (list.Count == 0) return new PatternBounds(-1, -1, 1, 1);
            return new PatternBounds(list.Min(p => p.X), list.Min(p => p.Y), list.Max(p => p.X), list.Max(p => p.Y));
        }
    }

    internal sealed class PatternLineGenerationResult
    {
        public List<PatternSegment> Segments { get; } = new List<PatternSegment>();
        public List<string> Warnings { get; } = new List<string>();
        public bool WasLimited { get; set; }
    }

    // PatternStudioWindow에서 라이브 검증된 FillGrid 반복 규칙(Offset/Shift와 양수 선·음수 공백·0 점)을
    // 창과 타공 엔진이 똑같이 사용하도록 순수 2차원 선분 목록으로 분리한 구현이다.
    internal static class PatternLineGenerator
    {
        private const double Tiny = 1e-10;

        public static PatternLineGenerationResult Generate(
            PatternDefinition definition,
            PatternBounds bounds,
            double lengthScale,
            int maximumLines = 1800,
            int maximumSegments = 30000)
        {
            var result = new PatternLineGenerationResult();
            for (int gridIndex = 0; gridIndex < definition.Grids.Count; gridIndex++)
            {
                PatternGridDefinition source = definition.Grids[gridIndex];
                double offset = source.Offset * lengthScale;
                if (Math.Abs(offset) < Tiny)
                {
                    result.Warnings.Add($"선군 {gridIndex + 1}: 간격이 0이라 영역 계산에서 제외했습니다.");
                    continue;
                }

                double radians = source.AngleDegrees * Math.PI / 180.0;
                var direction = new PatternPoint(Math.Cos(radians), Math.Sin(radians));
                var normal = new PatternPoint(-direction.Y, direction.X);
                var origin = new PatternPoint(source.OriginX * lengthScale, source.OriginY * lengthScale);
                double shift = source.Shift * lengthScale;

                PatternPoint[] corners =
                {
                    new PatternPoint(bounds.MinX, bounds.MinY), new PatternPoint(bounds.MinX, bounds.MaxY),
                    new PatternPoint(bounds.MaxX, bounds.MinY), new PatternPoint(bounds.MaxX, bounds.MaxY),
                };
                double originProjection = Dot(origin, normal);
                double minProjection = corners.Min(point => Dot(point, normal));
                double maxProjection = corners.Max(point => Dot(point, normal));
                double first = (minProjection - originProjection) / offset;
                double last = (maxProjection - originProjection) / offset;
                int start = SafeFloor(Math.Min(first, last)) - 2;
                int end = SafeCeiling(Math.Max(first, last)) + 2;
                if (end - start + 1 > maximumLines)
                {
                    int center = SafeFloor((first + last) * 0.5);
                    start = center - maximumLines / 2;
                    end = center + maximumLines / 2;
                    result.WasLimited = true;
                    result.Warnings.Add($"선군 {gridIndex + 1}: 표시 선 수가 너무 많아 {maximumLines:N0}개까지만 계산했습니다.");
                }

                List<double> scaledSegments = source.Segments.Select(value => value * lengthScale).ToList();
                for (int k = start; k <= end; k++)
                {
                    PatternPoint anchor = origin + (direction * shift + normal * offset) * k;
                    if (!TryClipInfiniteLine(anchor, direction, bounds, out double t0, out double t1)) continue;

                    if (scaledSegments.Count == 0)
                    {
                        Add(result, new PatternSegment(anchor + direction * t0, anchor + direction * t1, gridIndex), maximumSegments);
                    }
                    else
                    {
                        AddDashedSegments(result, anchor, direction, t0, t1, scaledSegments, gridIndex, bounds, maximumSegments);
                    }

                    if (result.Segments.Count >= maximumSegments)
                    {
                        result.WasLimited = true;
                        result.Warnings.Add($"패턴 선분이 너무 많아 {maximumSegments:N0}개에서 계산을 멈췄습니다.");
                        return result;
                    }
                }
            }
            return result;
        }

        private static void AddDashedSegments(
            PatternLineGenerationResult result,
            PatternPoint anchor,
            PatternPoint direction,
            double t0,
            double t1,
            IReadOnlyList<double> segments,
            int gridIndex,
            PatternBounds bounds,
            int maximumSegments)
        {
            double cycle = segments.Sum(segment => Math.Abs(segment));
            if (cycle < Tiny)
            {
                // 점은 미리보기에는 보이되 폐영역 경계로 오인되지 않도록 아주 짧은 선분 하나로만 둔다.
                double dotLength = Math.Max(Math.Max(bounds.Width, bounds.Height) / 900.0, 1e-5);
                if (t0 <= 0.0 && t1 >= 0.0)
                    Add(result, new PatternSegment(anchor, anchor + direction * dotLength, gridIndex), maximumSegments);
                return;
            }

            long firstCycle = SafeCycleIndex(Math.Floor(t0 / cycle));
            long lastCycle = SafeCycleIndex(Math.Ceiling(t1 / cycle));
            const long maximumCycles = 50000;
            if (lastCycle - firstCycle > maximumCycles) lastCycle = firstCycle + maximumCycles;

            for (long cycleIndex = firstCycle; cycleIndex <= lastCycle && result.Segments.Count < maximumSegments; cycleIndex++)
            {
                double cursor = cycleIndex * cycle;
                foreach (double segment in segments)
                {
                    double length = Math.Abs(segment);
                    if (length < Tiny) continue;
                    double segmentEnd = cursor + length;
                    if (segment > 0.0 && segmentEnd >= t0 && cursor <= t1)
                    {
                        double visibleStart = Math.Max(cursor, t0);
                        double visibleEnd = Math.Min(segmentEnd, t1);
                        if (visibleEnd - visibleStart > Tiny)
                            Add(result, new PatternSegment(anchor + direction * visibleStart, anchor + direction * visibleEnd, gridIndex), maximumSegments);
                    }
                    cursor = segmentEnd;
                }
            }
        }

        private static void Add(PatternLineGenerationResult result, PatternSegment segment, int maximumSegments)
        {
            if (result.Segments.Count < maximumSegments && segment.Length > Tiny)
                result.Segments.Add(segment);
        }

        private static bool TryClipInfiniteLine(PatternPoint anchor, PatternPoint direction, PatternBounds bounds, out double t0, out double t1)
        {
            t0 = double.NegativeInfinity;
            t1 = double.PositiveInfinity;
            if (!ClipAxis(anchor.X, direction.X, bounds.MinX, bounds.MaxX, ref t0, ref t1) ||
                !ClipAxis(anchor.Y, direction.Y, bounds.MinY, bounds.MaxY, ref t0, ref t1)) return false;
            return t1 >= t0 && !double.IsInfinity(t0) && !double.IsInfinity(t1);
        }

        private static bool ClipAxis(double origin, double direction, double minimum, double maximum, ref double t0, ref double t1)
        {
            if (Math.Abs(direction) < 1e-12) return origin >= minimum && origin <= maximum;
            double a = (minimum - origin) / direction;
            double b = (maximum - origin) / direction;
            if (a > b) (a, b) = (b, a);
            t0 = Math.Max(t0, a);
            t1 = Math.Min(t1, b);
            return t1 >= t0;
        }

        private static int SafeFloor(double value)
        {
            if (value <= int.MinValue + 10) return int.MinValue + 10;
            if (value >= int.MaxValue - 10) return int.MaxValue - 10;
            return (int)Math.Floor(value);
        }

        private static int SafeCeiling(double value)
        {
            if (value <= int.MinValue + 10) return int.MinValue + 10;
            if (value >= int.MaxValue - 10) return int.MaxValue - 10;
            return (int)Math.Ceiling(value);
        }

        private static long SafeCycleIndex(double value)
        {
            if (value <= -9000000000000000.0) return -9000000000000000L;
            if (value >= 9000000000000000.0) return 9000000000000000L;
            return (long)value;
        }

        private static double Dot(PatternPoint a, PatternPoint b) => a.X * b.X + a.Y * b.Y;
    }

    internal sealed class PatternRegion
    {
        public PatternRegion(IEnumerable<PatternPoint> points)
        {
            Points = Simplify(points.ToList());
            SignedArea = CalculateSignedArea(Points);
            Area = Math.Abs(SignedArea);
            Perimeter = CalculatePerimeter(Points);
            Centroid = CalculateCentroid(Points, SignedArea);
        }

        public List<PatternPoint> Points { get; }
        public double SignedArea { get; }
        public double Area { get; }
        public double Perimeter { get; }
        public PatternPoint Centroid { get; }

        public bool IsSimilarTo(PatternRegion other, double relativeTolerance = 0.025)
        {
            if (Points.Count != other.Points.Count || Points.Count < 3) return false;
            if (RelativeDifference(Area, other.Area) > relativeTolerance) return false;
            if (RelativeDifference(Perimeter, other.Perimeter) > relativeTolerance) return false;

            List<double> a = EdgeLengths(Points).Select(length => length / Math.Max(Perimeter, 1e-12)).OrderBy(v => v).ToList();
            List<double> b = EdgeLengths(other.Points).Select(length => length / Math.Max(other.Perimeter, 1e-12)).OrderBy(v => v).ToList();
            for (int i = 0; i < a.Count; i++)
                if (Math.Abs(a[i] - b[i]) > relativeTolerance) return false;
            return true;
        }

        private static double RelativeDifference(double a, double b) => Math.Abs(a - b) / Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1e-12);

        private static IEnumerable<double> EdgeLengths(IReadOnlyList<PatternPoint> points)
        {
            for (int i = 0; i < points.Count; i++) yield return (points[(i + 1) % points.Count] - points[i]).Length;
        }

        private static List<PatternPoint> Simplify(List<PatternPoint> points)
        {
            if (points.Count > 1 && (points[0] - points[points.Count - 1]).Length < 1e-9) points.RemoveAt(points.Count - 1);
            bool changed = true;
            while (changed && points.Count > 3)
            {
                changed = false;
                for (int i = 0; i < points.Count; i++)
                {
                    PatternPoint a = points[(i - 1 + points.Count) % points.Count];
                    PatternPoint b = points[i];
                    PatternPoint c = points[(i + 1) % points.Count];
                    PatternPoint ab = b - a;
                    PatternPoint bc = c - b;
                    double cross = Math.Abs(Cross(ab, bc));
                    if (cross <= 1e-9 * Math.Max(1.0, ab.Length + bc.Length))
                    {
                        points.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            return points;
        }

        private static double CalculateSignedArea(IReadOnlyList<PatternPoint> points)
        {
            double area = 0.0;
            for (int i = 0; i < points.Count; i++) area += Cross(points[i], points[(i + 1) % points.Count]);
            return area * 0.5;
        }

        private static double CalculatePerimeter(IReadOnlyList<PatternPoint> points) => EdgeLengths(points).Sum();

        private static PatternPoint CalculateCentroid(IReadOnlyList<PatternPoint> points, double signedArea)
        {
            if (points.Count == 0) return new PatternPoint(0, 0);
            if (Math.Abs(signedArea) < 1e-12)
                return new PatternPoint(points.Average(p => p.X), points.Average(p => p.Y));

            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                PatternPoint a = points[i];
                PatternPoint b = points[(i + 1) % points.Count];
                double cross = Cross(a, b);
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            double factor = 1.0 / (6.0 * signedArea);
            return new PatternPoint(cx * factor, cy * factor);
        }

        private static double Cross(PatternPoint a, PatternPoint b) => a.X * b.Y - a.Y * b.X;
    }

    // 보이는 패턴 선분을 교차점에서 자르고 반시계 방향 반변(half-edge)을 따라가 실제 폐영역만 찾는다.
    // 화면 경계선 자체는 그래프에 넣지 않으므로 미리보기 가장자리가 가짜 폐영역으로 선택되지 않는다.
    internal static class PatternRegionDetector
    {
        public static List<PatternRegion> Detect(IReadOnlyList<PatternSegment> input, PatternBounds bounds, out string warning)
        {
            warning = "";
            if (input.Count == 0) return new List<PatternRegion>();
            const int maximumInputSegments = 12000;
            if (input.Count > maximumInputSegments)
            {
                warning = $"폐영역 분석 선분이 {input.Count:N0}개라 안전 한도 {maximumInputSegments:N0}개를 넘었습니다.";
                return new List<PatternRegion>();
            }

            double snap = Math.Max(Math.Max(bounds.Width, bounds.Height) * 1e-8, 1e-7);
            var splitParameters = new List<List<double>>(input.Count);
            for (int i = 0; i < input.Count; i++) splitParameters.Add(new List<double> { 0.0, 1.0 });

            if (!AddAllIntersections(input, bounds, splitParameters, snap, out warning))
                return new List<PatternRegion>();

            var nodes = new List<PatternPoint>();
            var nodeByKey = new Dictionary<NodeKey, int>();
            var edges = new HashSet<EdgeKey>();
            for (int segmentIndex = 0; segmentIndex < input.Count; segmentIndex++)
            {
                PatternSegment segment = input[segmentIndex];
                List<double> values = splitParameters[segmentIndex]
                    .Where(value => value >= -1e-9 && value <= 1.0 + 1e-9)
                    .Select(value => Math.Max(0.0, Math.Min(1.0, value)))
                    .OrderBy(value => value)
                    .ToList();
                values = Distinct(values, 1e-9);
                for (int i = 0; i + 1 < values.Count; i++)
                {
                    PatternPoint a = Lerp(segment.Start, segment.End, values[i]);
                    PatternPoint b = Lerp(segment.Start, segment.End, values[i + 1]);
                    if ((b - a).Length <= snap) continue;
                    int na = GetNode(a, snap, nodeByKey, nodes);
                    int nb = GetNode(b, snap, nodeByKey, nodes);
                    if (na != nb) edges.Add(new EdgeKey(na, nb));
                }
            }

            var adjacency = new Dictionary<int, List<int>>();
            foreach (EdgeKey edge in edges)
            {
                AddNeighbor(adjacency, edge.A, edge.B);
                AddNeighbor(adjacency, edge.B, edge.A);
            }
            foreach (KeyValuePair<int, List<int>> pair in adjacency)
            {
                PatternPoint center = nodes[pair.Key];
                pair.Value.Sort((a, b) => Math.Atan2(nodes[a].Y - center.Y, nodes[a].X - center.X)
                    .CompareTo(Math.Atan2(nodes[b].Y - center.Y, nodes[b].X - center.X)));
            }

            var visited = new HashSet<DirectedEdgeKey>();
            var regions = new List<PatternRegion>();
            foreach (EdgeKey edge in edges)
            {
                Trace(edge.A, edge.B);
                Trace(edge.B, edge.A);
            }
            return regions;

            void Trace(int startA, int startB)
            {
                var first = new DirectedEdgeKey(startA, startB);
                if (visited.Contains(first)) return;

                var loop = new List<int>();
                int a = startA;
                int b = startB;
                int safety = 0;
                while (safety++ < Math.Max(100, edges.Count * 3))
                {
                    var directed = new DirectedEdgeKey(a, b);
                    if (visited.Contains(directed))
                    {
                        if (a == startA && b == startB) break;
                        return;
                    }
                    visited.Add(directed);
                    loop.Add(a);

                    if (!adjacency.TryGetValue(b, out List<int>? neighbors) || neighbors.Count < 2) return;
                    int reverseIndex = neighbors.IndexOf(a);
                    if (reverseIndex < 0) return;
                    int next = neighbors[(reverseIndex - 1 + neighbors.Count) % neighbors.Count];
                    a = b;
                    b = next;
                    if (a == startA && b == startB) break;
                }

                if (loop.Count < 3 || a != startA || b != startB) return;
                var region = new PatternRegion(loop.Select(index => nodes[index]));
                if (region.SignedArea <= snap * snap * 10.0 || region.Points.Count < 3) return;
                if (!bounds.Contains(region.Centroid, snap * 2.0)) return;
                if (regions.Any(existing => (existing.Centroid - region.Centroid).Length <= snap * 4.0 &&
                                            Math.Abs(existing.Area - region.Area) <= snap * snap * 20.0)) return;
                regions.Add(region);
            }
        }

        private static bool AddAllIntersections(IReadOnlyList<PatternSegment> input, PatternBounds bounds,
            IReadOnlyList<List<double>> splitParameters, double snap, out string warning)
        {
            warning = "";
            double span = Math.Max(bounds.Width, bounds.Height);
            double cellSize = Math.Max(span / Math.Max(8.0, Math.Sqrt(input.Count) * 0.7), snap * 100.0);
            var buckets = new Dictionary<GridCell, List<int>>();
            for (int index = 0; index < input.Count; index++)
            {
                PatternSegment segment = input[index];
                int minX = SafeGridIndex(Math.Floor((Math.Min(segment.Start.X, segment.End.X) - bounds.MinX) / cellSize));
                int maxX = SafeGridIndex(Math.Floor((Math.Max(segment.Start.X, segment.End.X) - bounds.MinX) / cellSize));
                int minY = SafeGridIndex(Math.Floor((Math.Min(segment.Start.Y, segment.End.Y) - bounds.MinY) / cellSize));
                int maxY = SafeGridIndex(Math.Floor((Math.Max(segment.Start.Y, segment.End.Y) - bounds.MinY) / cellSize));
                long cellCount = (long)(maxX - minX + 1) * (maxY - minY + 1);
                if (cellCount > 20000)
                {
                    warning = "매우 긴 패턴 선 때문에 폐영역 교차점 계산 범위를 안전하게 제한할 수 없습니다.";
                    return false;
                }
                for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                {
                    var key = new GridCell(x, y);
                    if (!buckets.TryGetValue(key, out List<int>? values)) buckets[key] = values = new List<int>();
                    values.Add(index);
                }
            }

            const int maximumCandidatePairs = 8000000;
            var tested = new HashSet<long>();
            foreach (List<int> indices in buckets.Values)
            {
                for (int a = 0; a < indices.Count; a++)
                for (int b = a + 1; b < indices.Count; b++)
                {
                    int first = indices[a];
                    int second = indices[b];
                    if (first == second) continue;
                    if (first > second) (first, second) = (second, first);
                    long pairKey = ((long)first << 32) | (uint)second;
                    if (!tested.Add(pairKey)) continue;
                    if (tested.Count > maximumCandidatePairs)
                    {
                        warning = $"폐영역 교차 후보가 {maximumCandidatePairs:N0}쌍을 넘어 안전하게 분석을 중단했습니다.";
                        return false;
                    }
                    AddIntersections(input[first], input[second], splitParameters[first], splitParameters[second], snap);
                }
            }
            return true;
        }

        private static int SafeGridIndex(double value)
        {
            if (value <= int.MinValue + 10) return int.MinValue + 10;
            if (value >= int.MaxValue - 10) return int.MaxValue - 10;
            return (int)value;
        }

        private static void AddIntersections(PatternSegment first, PatternSegment second, List<double> firstValues, List<double> secondValues, double tolerance)
        {
            PatternPoint p = first.Start;
            PatternPoint r = first.End - first.Start;
            PatternPoint q = second.Start;
            PatternPoint s = second.End - second.Start;
            double denominator = Cross(r, s);
            PatternPoint qp = q - p;
            if (Math.Abs(denominator) > tolerance * tolerance)
            {
                double t = Cross(qp, s) / denominator;
                double u = Cross(qp, r) / denominator;
                if (t >= -1e-9 && t <= 1.0 + 1e-9 && u >= -1e-9 && u <= 1.0 + 1e-9)
                {
                    firstValues.Add(t);
                    secondValues.Add(u);
                }
                return;
            }

            if (Math.Abs(Cross(qp, r)) > tolerance * Math.Max(1.0, r.Length)) return;
            AddEndpointIfOn(first.Start, second, 0.0, firstValues, secondValues, tolerance);
            AddEndpointIfOn(first.End, second, 1.0, firstValues, secondValues, tolerance);
            AddEndpointIfOn(second.Start, first, 0.0, secondValues, firstValues, tolerance);
            AddEndpointIfOn(second.End, first, 1.0, secondValues, firstValues, tolerance);
        }

        private static void AddEndpointIfOn(PatternPoint point, PatternSegment target, double sourceParameter,
            List<double> sourceValues, List<double> targetValues, double tolerance)
        {
            PatternPoint vector = target.End - target.Start;
            double lengthSquared = Dot(vector, vector);
            if (lengthSquared < tolerance * tolerance) return;
            double targetParameter = Dot(point - target.Start, vector) / lengthSquared;
            if (targetParameter < -1e-9 || targetParameter > 1.0 + 1e-9) return;
            PatternPoint projected = target.Start + vector * targetParameter;
            if ((projected - point).Length > tolerance) return;
            sourceValues.Add(sourceParameter);
            targetValues.Add(targetParameter);
        }

        private static int GetNode(PatternPoint point, double snap, Dictionary<NodeKey, int> byKey, List<PatternPoint> nodes)
        {
            var key = new NodeKey((long)Math.Round(point.X / snap), (long)Math.Round(point.Y / snap));
            if (byKey.TryGetValue(key, out int existing)) return existing;
            int index = nodes.Count;
            nodes.Add(point);
            byKey.Add(key, index);
            return index;
        }

        private static List<double> Distinct(List<double> source, double tolerance)
        {
            var result = new List<double>();
            foreach (double value in source)
                if (result.Count == 0 || Math.Abs(result[result.Count - 1] - value) > tolerance) result.Add(value);
            return result;
        }

        private static void AddNeighbor(Dictionary<int, List<int>> adjacency, int node, int neighbor)
        {
            if (!adjacency.TryGetValue(node, out List<int>? list)) adjacency[node] = list = new List<int>();
            if (!list.Contains(neighbor)) list.Add(neighbor);
        }

        private static PatternPoint Lerp(PatternPoint a, PatternPoint b, double t) => a + (b - a) * t;
        private static double Dot(PatternPoint a, PatternPoint b) => a.X * b.X + a.Y * b.Y;
        private static double Cross(PatternPoint a, PatternPoint b) => a.X * b.Y - a.Y * b.X;

        private readonly struct NodeKey : IEquatable<NodeKey>
        {
            public NodeKey(long x, long y) { X = x; Y = y; }
            private long X { get; }
            private long Y { get; }
            public bool Equals(NodeKey other) => X == other.X && Y == other.Y;
            public override bool Equals(object? obj) => obj is NodeKey other && Equals(other);
            public override int GetHashCode() => unchecked((X.GetHashCode() * 397) ^ Y.GetHashCode());
        }

        private readonly struct GridCell : IEquatable<GridCell>
        {
            public GridCell(int x, int y) { X = x; Y = y; }
            private int X { get; }
            private int Y { get; }
            public bool Equals(GridCell other) => X == other.X && Y == other.Y;
            public override bool Equals(object? obj) => obj is GridCell other && Equals(other);
            public override int GetHashCode() => unchecked((X * 397) ^ Y);
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            public EdgeKey(int a, int b) { A = Math.Min(a, b); B = Math.Max(a, b); }
            public int A { get; }
            public int B { get; }
            public bool Equals(EdgeKey other) => A == other.A && B == other.B;
            public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => unchecked((A * 397) ^ B);
        }

        private readonly struct DirectedEdgeKey : IEquatable<DirectedEdgeKey>
        {
            public DirectedEdgeKey(int a, int b) { A = a; B = b; }
            private int A { get; }
            private int B { get; }
            public bool Equals(DirectedEdgeKey other) => A == other.A && B == other.B;
            public override bool Equals(object? obj) => obj is DirectedEdgeKey other && Equals(other);
            public override int GetHashCode() => unchecked((A * 397) ^ B);
        }
    }

    internal static class PatternClipper
    {
        private const double IntegerScale = 1000000.0;

        public static Path64 ToPath(IReadOnlyList<PatternPoint> points)
        {
            var result = new Path64(points.Count);
            foreach (PatternPoint point in points)
                result.Add(new Point64(ToInteger(point.X), ToInteger(point.Y)));
            return result;
        }

        public static List<PatternPoint> FromPath(Path64 path)
        {
            return path.Select(point => new PatternPoint(point.X / IntegerScale, point.Y / IntegerScale)).ToList();
        }

        public static Paths64 Union(Paths64 paths) => Clipper.Union(paths, FillRule.NonZero);
        public static Paths64 Intersect(Paths64 subject, Paths64 clip) => Clipper.Intersect(subject, clip, FillRule.NonZero);
        public static Paths64 Difference(Paths64 subject, Paths64 clip) => Clipper.Difference(subject, clip, FillRule.NonZero);
        public static double Area(Path64 path) => Math.Abs(Clipper.Area(path)) / (IntegerScale * IntegerScale);

        public static bool Contains(Paths64 paths, PatternPoint point)
        {
            var p = new Point64(ToInteger(point.X), ToInteger(point.Y));
            int winding = 0;
            foreach (Path64 path in paths)
            {
                PointInPolygonResult result = Clipper.PointInPolygon(p, path);
                if (result == PointInPolygonResult.IsOn) return true;
                if (result == PointInPolygonResult.IsInside) winding++;
            }
            return winding % 2 == 1;
        }

        private static long ToInteger(double value)
        {
            double scaled = value * IntegerScale;
            if (scaled > long.MaxValue * 0.5 || scaled < long.MinValue * 0.5)
                throw new InvalidOperationException("패턴 좌표가 계산 가능한 범위를 벗어났습니다.");
            return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }
    }
}
