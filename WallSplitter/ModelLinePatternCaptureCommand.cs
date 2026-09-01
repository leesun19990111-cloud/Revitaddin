using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ModelLinePatternCaptureCommand : IExternalCommand
    {
        private const int MaximumSlopeDenominator = 48;
        private const int MaximumSlopeNumerator = MaximumSlopeDenominator * 8;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null)
            {
                TaskDialog.Show("모델선 패턴 캡처", "열려 있는 Revit 문서가 없습니다.");
                return Result.Cancelled;
            }

            Document document = uiDocument.Document;
            View view = document.ActiveView;
            if (view == null || view.IsTemplate || view is not ViewPlan && view is not ViewSection)
            {
                TaskDialog.Show("모델선 패턴 캡처", "평면·천장평면·입면·단면 2D 뷰에서 실행해 주세요.");
                return Result.Cancelled;
            }

            string stage = "패턴 종류 선택";
            try
            {
                SketchPlane? sketchPlane = view.SketchPlane;
                if (sketchPlane == null)
                    throw new InvalidOperationException("현재 뷰에 활성 작업 기준면이 없습니다.\n\nRevit의 '작업 기준면 설정'에서 현재 뷰와 평행한 기준면을 지정한 뒤 다시 실행해 주세요.");
                Plane workPlane = sketchPlane.GetPlane();
                double workPlaneAlignment = Math.Abs(workPlane.Normal.Normalize().DotProduct(view.ViewDirection.Normalize()));
                if (workPlaneAlignment < 0.999999)
                    throw new InvalidOperationException("현재 작업 기준면이 뷰와 평행하지 않습니다.\n\n현재 뷰를 정면으로 보는 작업 기준면을 다시 설정해 주세요.");

                FillPatternTarget? selectedTarget = AskTarget();
                if (selectedTarget == null) return Result.Cancelled;
                FillPatternTarget target = selectedTarget.Value;

                stage = "반복 틀 세 점 지정";
                ObjectSnapTypes snaps = ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints |
                                        ObjectSnapTypes.Nearest | ObjectSnapTypes.Intersections |
                                        ObjectSnapTypes.Centers | ObjectSnapTypes.Perpendicular |
                                        ObjectSnapTypes.Quadrants;
                XYZ origin = uiDocument.Selection.PickPoint(snaps, "ㄱ자 반복 틀의 첫 번째 모서리를 지정하세요.");
                XYZ widthPoint = uiDocument.Selection.PickPoint(snaps, "첫 번째 변의 끝이자 두 번째 모서리를 지정하세요.");
                XYZ heightPoint = uiDocument.Selection.PickPoint(snaps, "두 번째 변의 끝을 지정하세요. 사각형은 자동으로 직교 보정됩니다.");

                XYZ viewNormal = view.ViewDirection.Normalize();
                XYZ normal = workPlane.Normal.Normalize();
                if (normal.DotProduct(viewNormal) < 0.0) normal = -normal;
                XYZ xAxis = ProjectToPlane(widthPoint - origin, normal);
                if (xAxis.GetLength() <= document.Application.ShortCurveTolerance)
                    throw new InvalidOperationException("반복 틀의 폭이 너무 작습니다.");
                double width = xAxis.GetLength();
                xAxis = xAxis.Normalize();
                XYZ yAxis = normal.CrossProduct(xAxis).Normalize();
                XYZ secondEdge = ProjectToPlane(heightPoint - widthPoint, normal);
                if (secondEdge.DotProduct(yAxis) < 0.0) yAxis = -yAxis;
                double height = Math.Abs(secondEdge.DotProduct(yAxis));
                if (height <= document.Application.ShortCurveTolerance)
                    throw new InvalidOperationException("두 번째 변의 높이가 너무 작습니다. 세 점을 ㄱ자 순서로 지정해 주세요.");

                stage = "ㄱ자 사각 틀 안의 모델선·상세선 수집";
                List<PatternSegment> captured = CollectSegments(document, view, origin, normal, xAxis, yAxis, width, height, out string captureDiagnostics);
                if (captured.Count == 0)
                {
                    TaskDialog.Show("모델선 패턴 캡처", "지정한 ㄱ자 사각 틀 안이나 경계에 걸친 Revit 선을 찾지 못했습니다.\n\n모델선과 상세선을 모두 읽습니다. CAD 링크 자체는 읽지 않으므로 CAD를 따라 Revit 모델선 또는 상세선을 그려 주세요.\n\n" + captureDiagnostics);
                    return Result.Cancelled;
                }

                stage = "캡처 선을 Revit 패턴 선군으로 변환";
                var warnings = new List<string>();
                List<PatternGridDefinition> grids = CompileSegments(captured, width, height, document.Application.ShortCurveTolerance, warnings);
                if (grids.Count == 0)
                    throw new InvalidOperationException("캡처한 모델선·상세선을 Revit 패턴 선군으로 변환하지 못했습니다.");

                stage = "패턴 출력 축척 적용";
                double outputScale = target == FillPatternTarget.Drafting ? 1.0 / Math.Max(1, view.Scale) : 1.0;
                if (Math.Abs(outputScale - 1.0) > 1e-12)
                {
                    foreach (PatternGridDefinition grid in grids)
                    {
                        grid.OriginX *= outputScale;
                        grid.OriginY *= outputScale;
                        grid.Shift *= outputScale;
                        grid.Offset *= outputScale;
                        for (int i = 0; i < grid.Segments.Count; i++) grid.Segments[i] *= outputScale;
                    }
                }

                var definition = new PatternDefinition
                {
                    Name = "모델선 캡처 패턴",
                    Description = $"{width * 304.8:0.##} × {height * 304.8:0.##} mm 반복 틀",
                    Target = target,
                    HostOrientation = target == FillPatternTarget.Model ? FillPatternHostOrientation.ToHost : FillPatternHostOrientation.ToView,
                    SourceLabel = "현재 뷰의 모델선·상세선",
                    SourceUnitLabel = target == FillPatternTarget.Model
                        ? $"실제 크기 1:1 · {width * 304.8:0.##} × {height * 304.8:0.##} mm"
                        : $"현재 뷰 1:{Math.Max(1, view.Scale)} · 종이상 {width * 304.8 / Math.Max(1, view.Scale):0.###} × {height * 304.8 / Math.Max(1, view.Scale):0.###} mm",
                    Grids = grids,
                };

                stage = "패턴 편집 창 준비";
                var window = new PatternStudioWindow(
                    new List<PatternDefinition> { definition },
                    PatternStudioCommand.CollectPatternNames(document));
                new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
                if (window.ShowDialog() != true || window.SaveRequest == null) return Result.Cancelled;

                stage = "Revit 패턴 저장";
                Result saveResult = Save(document, window.SaveRequest, ref message);
                if (warnings.Count > 0 && saveResult == Result.Succeeded)
                    TaskDialog.Show("모델선 패턴 캡처", "패턴은 저장되었지만 변환 중 다음 항목을 확인해 주세요.\n\n" + string.Join("\n", warnings.Take(12)));
                return saveResult;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                message = string.Empty;
                return Result.Cancelled;
            }
            catch (InvalidOperationException ex)
            {
                TaskDialog.Show("모델선 패턴 캡처", "패턴을 만들지 못했습니다.\n\n" + ex.Message);
                message = string.Empty;
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Exception root = ex.GetBaseException();
                string detail = $"진행 단계: {stage}\n오류 종류: {root.GetType().Name}\n\n{root.Message}";
                TaskDialog.Show("모델선 패턴 캡처", "패턴을 만들지 못했습니다.\n\n" + detail);
                message = string.Empty;
                return Result.Cancelled;
            }
        }

        private static FillPatternTarget? AskTarget()
        {
            var dialog = new TaskDialog("모델선 패턴 캡처")
            {
                MainInstruction = "저장할 패턴 종류를 선택하세요.",
                MainContent = "모델 패턴은 실제 크기 1:1로 저장됩니다. 제도 패턴은 현재 뷰 축척으로 나눈 종이상 크기로 저장됩니다.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "모델 패턴", "캡처한 Revit 선을 실제 치수 그대로 저장");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "제도 패턴", "캡처한 Revit 선에 현재 뷰 축척을 적용해 저장");
            // Revit은 DefaultButton을 대입하는 즉시 해당 버튼이 이미 등록됐는지 검사한다.
            dialog.DefaultButton = TaskDialogResult.CommandLink1;
            TaskDialogResult result = dialog.Show();
            if (result == TaskDialogResult.CommandLink1) return FillPatternTarget.Model;
            if (result == TaskDialogResult.CommandLink2) return FillPatternTarget.Drafting;
            return null;
        }

        private static List<PatternSegment> CollectSegments(Document document, View view, XYZ origin, XYZ normal,
            XYZ xAxis, XYZ yAxis, double width, double height, out string diagnostics)
        {
            var result = new List<PatternSegment>();
            List<CurveElement> visibleCurves = new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(CurveElement))
                .Cast<CurveElement>()
                .ToList();
            List<CurveElement> curves = visibleCurves
                .Where(curve => curve is ModelCurve || curve is DetailCurve)
                .ToList();

            int readableCurveCount = 0;
            int touchingCurveCount = 0;
            foreach (CurveElement curveElement in curves)
            {
                Curve curve;
                try { curve = curveElement.GeometryCurve; }
                catch { continue; }

                IList<XYZ> points;
                try { points = curve.Tessellate(); }
                catch { continue; }
                if (points.Count < 2) continue;
                readableCurveCount++;

                List<PatternPoint> localPoints = points.Select(point =>
                {
                    XYZ projected = point - normal * (point - origin).DotProduct(normal);
                    return ToLocal(projected, origin, xAxis, yAxis);
                }).ToList();
                var captureBounds = new PatternBounds(0.0, 0.0, width, height);
                bool touchesOriginalTile = false;
                for (int i = 0; i + 1 < localPoints.Count; i++)
                {
                    if (!TryClipBoundedSegment(localPoints[i], localPoints[i + 1], captureBounds, out _, out _)) continue;
                    touchesOriginalTile = true;
                    break;
                }
                // 반복 틀과 실제로 닿지 않는 주변 Revit 선을 평행 이동해 틀 안으로 끌어오지 않는다.
                if (!touchesOriginalTile) continue;
                touchingCurveCount++;

                for (int i = 0; i + 1 < localPoints.Count; i++)
                {
                    PatternPoint a = localPoints[i];
                    PatternPoint b = localPoints[i + 1];
                    foreach (PatternSegment wrapped in WrapAndClip(a, b, width, height)) result.Add(wrapped);
                }
            }

            // 경계 위에서 양쪽 타일로 중복 수집된 동일 선분은 하나로 정리한다.
            List<PatternSegment> deduplicated = result
                .Where(segment => segment.Length > document.Application.ShortCurveTolerance * 0.25)
                .GroupBy(segment => SegmentKey(segment, width, height))
                .Select(group => group.First())
                .ToList();
            diagnostics = $"현재 뷰의 선 요소 {visibleCurves.Count:N0}개 · 모델선·상세선 {curves.Count:N0}개 · 읽은 곡선 {readableCurveCount:N0}개 · 사각 틀 교차 {touchingCurveCount:N0}개 · 최종 선분 {deduplicated.Count:N0}개";
            return deduplicated;
        }

        private static IEnumerable<PatternSegment> WrapAndClip(PatternPoint a, PatternPoint b, double width, double height)
        {
            int minTileX = (int)Math.Floor(Math.Min(a.X, b.X) / width) - 1;
            int maxTileX = (int)Math.Floor(Math.Max(a.X, b.X) / width) + 1;
            int minTileY = (int)Math.Floor(Math.Min(a.Y, b.Y) / height) - 1;
            int maxTileY = (int)Math.Floor(Math.Max(a.Y, b.Y) / height) + 1;
            minTileX = Math.Max(minTileX, -8); maxTileX = Math.Min(maxTileX, 8);
            minTileY = Math.Max(minTileY, -8); maxTileY = Math.Min(maxTileY, 8);

            var bounds = new PatternBounds(0, 0, width, height);
            for (int tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                for (int tileY = minTileY; tileY <= maxTileY; tileY++)
                {
                    PatternPoint shift = new PatternPoint(tileX * width, tileY * height);
                    if (TryClipBoundedSegment(a - shift, b - shift, bounds, out PatternPoint clippedA, out PatternPoint clippedB))
                        yield return new PatternSegment(clippedA, clippedB);
                }
            }
        }

        private static bool TryClipBoundedSegment(PatternPoint a, PatternPoint b, PatternBounds bounds,
            out PatternPoint clippedA, out PatternPoint clippedB)
        {
            PatternPoint d = b - a;
            double t0 = 0.0;
            double t1 = 1.0;
            if (!Clip(-d.X, a.X - bounds.MinX, ref t0, ref t1) ||
                !Clip(d.X, bounds.MaxX - a.X, ref t0, ref t1) ||
                !Clip(-d.Y, a.Y - bounds.MinY, ref t0, ref t1) ||
                !Clip(d.Y, bounds.MaxY - a.Y, ref t0, ref t1))
            {
                clippedA = default;
                clippedB = default;
                return false;
            }
            clippedA = a + d * t0;
            clippedB = a + d * t1;
            return (clippedB - clippedA).Length > 1e-10;
        }

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (Math.Abs(p) < 1e-14) return q >= 0.0;
            double r = q / p;
            if (p < 0.0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }

        private static List<PatternGridDefinition> CompileSegments(IReadOnlyList<PatternSegment> source,
            double width, double height, double shortCurveTolerance, List<string> warnings)
        {
            var result = new List<PatternGridDefinition>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (PatternSegment original in source)
            {
                PatternPoint a = original.Start;
                PatternPoint b = original.End;
                PatternPoint delta = b - a;
                if (delta.X < -1e-12 || Math.Abs(delta.X) < 1e-12 && delta.Y < 0.0)
                {
                    PatternPoint temp = a; a = b; b = temp;
                    delta = b - a;
                }

                (int p, int q) = RationalDirection(delta, width, height);
                PatternPoint latticeDirection = new PatternPoint(p * width, q * height);
                double period = latticeDirection.Length;
                if (period <= 1e-12) continue;
                PatternPoint direction = latticeDirection / period;
                PatternPoint normal = new PatternPoint(-direction.Y, direction.X);
                double visibleLength = Math.Abs(Dot(delta, direction));
                if (visibleLength <= shortCurveTolerance * 0.25) continue;
                visibleLength = Math.Min(visibleLength, period);
                PatternPoint midpoint = (a + b) * 0.5;
                PatternPoint start = midpoint - direction * (visibleLength * 0.5);

                int ia = -q;
                int ib = p;
                if (!TryBezout(ia, ib, out int u, out int v))
                {
                    warnings.Add("일부 경사 선분의 반복 벡터를 계산하지 못해 제외했습니다.");
                    continue;
                }
                PatternPoint translation = new PatternPoint(u * width, v * height);
                double shift = Dot(translation, direction);
                double offset = Dot(translation, normal);
                if (offset < 0.0) { shift = -shift; offset = -offset; }
                // 같은 격자를 나타내는 선 방향 정수 주기를 제거해 Revit에 거대한 Shift가 저장되지 않게 한다.
                shift -= Math.Round(shift / period) * period;
                if (Math.Abs(offset) < 1e-12) continue;

                double angle = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
                if (angle < 0.0) angle += 360.0;
                var grid = new PatternGridDefinition
                {
                    AngleDegrees = angle,
                    OriginX = start.X,
                    OriginY = start.Y,
                    Shift = shift,
                    Offset = offset,
                };
                double gap = period - visibleLength;
                if (gap > Math.Max(shortCurveTolerance * 0.1, 1e-9))
                {
                    grid.Segments.Add(visibleLength);
                    grid.Segments.Add(-gap);
                }

                string key = GridKey(grid);
                if (keys.Add(key)) result.Add(grid);
            }
            return result;
        }

        private static (int p, int q) RationalDirection(PatternPoint delta, double width, double height)
        {
            (int p, int q) vertical = (0, delta.Y >= 0.0 ? 1 : -1);
            if (Math.Abs(delta.X) < 1e-12 || delta.Length <= 1e-12) return vertical;
            double normalizedSlope = delta.Y * width / (delta.X * height);
            // 거의 수직인 선은 기울기가 int 범위를 넘는다. 그대로 (int)Math.Round로 캐스팅하면 값이
            // int.MinValue가 되고 바로 아래 Math.Abs(q)가 OverflowException을 던져 캡처가 통째로 실패한다.
            // 이런 선은 애초에 유리수 근사 대상이 아니므로 세로 선군으로 바로 처리한다.
            if (double.IsNaN(normalizedSlope) || double.IsInfinity(normalizedSlope) ||
                Math.Abs(normalizedSlope) > MaximumSlopeNumerator) return vertical;

            int bestP = 1;
            int bestQ = (int)Math.Round(normalizedSlope);
            double bestError = double.MaxValue;
            PatternPoint original = delta / delta.Length;
            for (int p = 1; p <= MaximumSlopeDenominator; p++)
            {
                double rawQ = normalizedSlope * p;
                if (Math.Abs(rawQ) > MaximumSlopeNumerator) continue;
                int q = (int)Math.Round(rawQ);
                if (Math.Abs(q) > MaximumSlopeNumerator) continue;
                int gcd = GreatestCommonDivisor(Math.Abs(p), Math.Abs(q));
                int rp = p / Math.Max(1, gcd);
                int rq = q / Math.Max(1, gcd);
                PatternPoint candidateVector = new PatternPoint(rp * width, rq * height);
                PatternPoint candidate = candidateVector / candidateVector.Length;
                double error = 1.0 - Math.Abs(Dot(original, candidate));
                if (error < bestError)
                {
                    bestError = error;
                    bestP = rp;
                    bestQ = rq;
                }
            }
            return (bestP, bestQ);
        }

        private static bool TryBezout(int a, int b, out int u, out int v)
        {
            int limit = MaximumSlopeDenominator * 10;
            for (int magnitude = 0; magnitude <= limit; magnitude++)
            {
                if (TryBezoutCandidate(a, b, -magnitude, out int negativeV))
                { u = -magnitude; v = negativeV; return true; }
                if (magnitude > 0 && TryBezoutCandidate(a, b, magnitude, out int positiveV))
                { u = magnitude; v = positiveV; return true; }
            }
            u = 0; v = 0;
            return false;
        }

        private static bool TryBezoutCandidate(int a, int b, int candidateU, out int candidateV)
        {
            int remaining = 1 - a * candidateU;
            if (b == 0)
            {
                candidateV = 0;
                return remaining == 0;
            }
            if (remaining % b != 0)
            {
                candidateV = 0;
                return false;
            }
            candidateV = remaining / b;
            return true;
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            if (a == 0) return Math.Max(1, b);
            if (b == 0) return Math.Max(1, a);
            while (b != 0) { int temp = a % b; a = b; b = temp; }
            return Math.Abs(a);
        }

        private static string SegmentKey(PatternSegment segment, double width, double height)
        {
            PatternPoint a = segment.Start;
            PatternPoint b = segment.End;
            // 한 선분 전체가 최대 경계 위에 있을 때만 동일한 최소 경계로 평행 이동한다.
            // 끝점을 각각 옮기면 경계에 닿는 대각선의 방향 자체가 바뀌어 다른 선분과 충돌한다.
            if (Math.Abs(a.X - width) < 1e-8 && Math.Abs(b.X - width) < 1e-8)
            {
                a = new PatternPoint(a.X - width, a.Y);
                b = new PatternPoint(b.X - width, b.Y);
            }
            if (Math.Abs(a.Y - height) < 1e-8 && Math.Abs(b.Y - height) < 1e-8)
            {
                a = new PatternPoint(a.X, a.Y - height);
                b = new PatternPoint(b.X, b.Y - height);
            }
            if (a.X > b.X || Math.Abs(a.X - b.X) < 1e-10 && a.Y > b.Y) { PatternPoint t = a; a = b; b = t; }
            return $"{Math.Round(a.X, 8)},{Math.Round(a.Y, 8)}|{Math.Round(b.X, 8)},{Math.Round(b.Y, 8)}";
        }

        private static string GridKey(PatternGridDefinition grid) =>
            $"{Math.Round(grid.AngleDegrees, 7)}|{Math.Round(grid.OriginX, 7)}|{Math.Round(grid.OriginY, 7)}|" +
            $"{Math.Round(grid.Shift, 7)}|{Math.Round(grid.Offset, 7)}|{string.Join(",", grid.Segments.Select(value => Math.Round(value, 7)))}";

        private static PatternPoint ToLocal(XYZ point, XYZ origin, XYZ xAxis, XYZ yAxis)
        {
            XYZ delta = point - origin;
            return new PatternPoint(delta.DotProduct(xAxis), delta.DotProduct(yAxis));
        }

        private static XYZ ProjectToPlane(XYZ vector, XYZ normal) => vector - normal * vector.DotProduct(normal);
        private static double Dot(PatternPoint a, PatternPoint b) => a.X * b.X + a.Y * b.Y;

        private static Result Save(Document document, PatternStudioSaveRequest request, ref string message)
        {
            try
            {
                FillPattern revitPattern = PatternStudioCommand.BuildFillPattern(request.Pattern, request.Name);
                using var transaction = new Transaction(document, "모델선 캡처 패턴 저장");
                transaction.Start();
                if (request.OverwriteSource)
                {
                    if (request.SourceElementId == null || document.GetElement(request.SourceElementId) is not FillPatternElement source)
                        throw new InvalidOperationException("덮어쓸 원본 패턴을 찾을 수 없습니다.");
                    source.SetFillPattern(revitPattern);
                }
                else
                {
                    if (PatternStudioCommand.FindPatternByName(document, request.Pattern.Target, request.Name) != null)
                        throw new InvalidOperationException($"'{request.Name}' 이름의 패턴이 이미 있습니다.");
                    _ = FillPatternElement.Create(document, revitPattern);
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit 패턴 저장 트랜잭션이 완료되지 않았습니다.");
                string action = request.OverwriteSource ? "원본 패턴을 수정했습니다." : "새 패턴을 만들었습니다.";
                TaskDialog.Show("모델선 패턴 캡처", $"{action}\n\n이름: {request.Name}\n유형: {PatternStudioCommand.TargetLabel(request.Pattern.Target)}\n선군: {request.Pattern.Grids.Count}개");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("모델선 패턴 캡처", "패턴을 저장하지 못했습니다.\n\n" + ex.Message);
                message = string.Empty;
                return Result.Cancelled;
            }
        }
    }
}
